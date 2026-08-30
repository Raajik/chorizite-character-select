using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chorizite.Core.Plugins;
using Chorizite.Core.Plugins.AssemblyLoader;
using Microsoft.Extensions.Logging;
using RmlUi;

namespace CharacterSelect;

/// <summary>
/// Character Select Plus: replaces the AC plugin's character select screen with
/// one that shows the population on two lines, plus each character's
/// last-known level and allegiance.
/// </summary>
/// <remarks>
/// The AC plugin's <c>ACPlugin.Instance</c> and <c>Net</c> members are
/// <c>internal</c>, so this plugin reaches them through reflection on the
/// loaded assembly. Every access is wrapped and logged so a mismatched AC
/// plugin version degrades to "no capture" instead of crashing the client.
/// </remarks>
public sealed class CharacterSelectPlugin : IPluginCore {
    private readonly ILogger _log;
    private readonly CharacterStore _store;
    private EventInfo? _playerDescriptionEvent;
    private Delegate? _playerDescriptionHandler;
    private object? _s2c;

    public CharacterSelectPlugin(
        AssemblyPluginManifest manifest,
        ILogger<CharacterSelectPlugin> log) : base(manifest) {
        _log = log;
        _store = new CharacterStore(DataDirectory);
        CharacterStoreApi.Inject(_store, _log);
    }

    protected override void Initialize() {
        var ourScreenPath = Path.Combine(AssemblyDirectory, "assets", "screens", "CharSelect.rml");
        try {
            var registered = RmlUiPlugin.Instance.RegisterScreen("CharSelect", ourScreenPath);
            _log.LogInformation("CharacterSelect: RegisterScreen(CharSelect, {Path}) -> {Result}", ourScreenPath, registered);
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: RegisterScreen failed");
        }

        SubscribeCapture();
        _log.LogInformation("CharacterSelect {Version} initialized", Manifest.Version);
    }

    private void SubscribeCapture() {
        try {
            var acType = FindAcPluginType();
            if (acType is null) {
                _log.LogWarning("CharacterSelect: AC plugin assembly not loaded; level capture disabled");
                return;
            }

            var instanceField = acType.GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            var acInstance = instanceField?.GetValue(null);
            if (acInstance is null) {
                _log.LogWarning("CharacterSelect: ACPlugin.Instance null at initialize (AC plugin loads later?); capture disabled this session");
                return;
            }

            var net = acType.GetProperty("Net", BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(acInstance);
            var s2c = net?.GetType().GetProperty("S2C")?.GetValue(net);
            if (s2c is null) {
                _log.LogWarning("CharacterSelect: S2C handler unavailable; capture disabled");
                return;
            }

            _s2c = s2c;
            var eventInfo = s2c.GetType().GetEvent("OnLogin_PlayerDescription");
            if (eventInfo is null) {
                _log.LogWarning("CharacterSelect: OnLogin_PlayerDescription event not found");
                return;
            }

            _playerDescriptionEvent = eventInfo;
            _playerDescriptionHandler = GetType()
                .GetMethod(nameof(OnPlayerDescriptionInternal), BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate(eventInfo.EventHandlerType!, this);
            eventInfo.AddEventHandler(s2c, _playerDescriptionHandler);
            _log.LogInformation("CharacterSelect: subscribed to OnLogin_PlayerDescription");
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: failed to subscribe to player description events");
        }
    }

    // Called via reflection-created delegate; sender/event typed as object.
    private void OnPlayerDescriptionBridge(object sender, EventArgs e) =>
        CaptureFromPlayerDescription(e);

    private void OnPlayerDescriptionInternal(object? sender, object e) =>
        CaptureFromPlayerDescription(e);

    private void CaptureFromPlayerDescription(object e) {
        try {
            var eType = e.GetType();
            var baseQualities = eType.GetField("BaseQualities")?.GetValue(e);
            if (baseQualities is null) {
                _log.LogWarning("CharacterSelect: Login_PlayerDescription.BaseQualities was null");
                return;
            }

            var level = 0;
            var allegiance = "";

            var intProps = baseQualities.GetType().GetField("IntProperties")?.GetValue(baseQualities) as IDictionary;
            if (intProps is not null) {
                foreach (DictionaryEntry entry in intProps) {
                    if (Convert.ToInt32(entry.Key) == 25) level = Convert.ToInt32(entry.Value);
                }
            }

            var stringProps = baseQualities.GetType().GetField("StringProperties")?.GetValue(baseQualities) as IDictionary;
            if (stringProps is not null) {
                foreach (DictionaryEntry entry in stringProps) {
                    if (Convert.ToInt32(entry.Key) == 47) allegiance = entry.Value as string ?? "";
                }
            }

            var (charId, charName) = CurrentCharacter();
            _store.Record(charId, charName, level, allegiance);
            _log.LogInformation(
                "CharacterSelect captured {Name} (0x{Id:X8}): level {Level}, allegiance '{Allegiance}'",
                charName, level, allegiance);
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: failed to capture player description");
        }
    }

    private (uint, string) CurrentCharacter() {
        var acInstance = FindAcInstance();
        var game = acInstance?.GetType().GetProperty("Game")?.GetValue(acInstance);
        var character = game?.GetType().GetProperty("Character")?.GetValue(game);
        if (character is null) return (0, "");
        var id = (uint)(character.GetType().GetProperty("Id")?.GetValue(character) ?? 0u);
        var name = character.GetType().GetProperty("Name")?.GetValue(character) as string ?? "";
        return (id, name);
    }

    private object? FindAcInstance() {
        var acType = FindAcPluginType();
        var instanceField = acType?.GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
        return instanceField?.GetValue(null);
    }

    private static Type? FindAcPluginType() {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!string.Equals(assembly.GetName().Name, "AC", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var type in assembly.GetTypes()) {
                if (type.Name == "ACPlugin") return type;
            }
        }
        return null;
    }

    protected override void Dispose() {
        try {
            if (_s2c is not null && _playerDescriptionEvent is not null && _playerDescriptionHandler is not null) {
                _playerDescriptionEvent.RemoveEventHandler(_s2c, _playerDescriptionHandler);
            }
        }
        catch (Exception ex) {
            _log.LogWarning(ex, "CharacterSelect: unsubscribe failed");
        }
    }
}

/// <summary>Lua-facing bridge for the screen script.</summary>
public static class CharacterStoreApi {
    private static CharacterStore? _store;
    private static ILogger? _log;

    internal static void Inject(CharacterStore store, ILogger log) {
        _store = store;
        _log = log;
    }

    /// <summary>Called from the screen's Lua to record facts captured in-world.</summary>
    public static void Record(uint id, string name, int level, string allegiance) {
        _store?.Record(id, name, level, allegiance);
    }

    /// <summary>JSON blob for one character, or null when unknown.</summary>
    public static string? Lookup(uint id) {
        if (_store is null) return null;
        return _store.TryGet(id, out var info)
            ? System.Text.Json.JsonSerializer.Serialize(info)
            : null;
    }
}
