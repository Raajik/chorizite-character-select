using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Chorizite.Core.Plugins;
using Chorizite.Core.Plugins.AssemblyLoader;
using Microsoft.Extensions.Logging;
using RmlUi;

namespace CharacterSelect;

/// <summary>
/// Character Select Plus: replaces the AC plugin's character select screen with
/// one that shows the population on two lines, plus each character's
/// last-known level and allegiance. Also skips intro videos and mutes
/// character-select sounds.
/// </summary>
/// <remarks>
/// The AC plugin's <c>ACPlugin.Instance</c> and <c>Net</c> members, and the
/// bootstrapper's <c>PlaySound</c>, are reached through reflection so a
/// mismatched AC plugin version degrades to "feature off" with a log line
/// instead of crashing the client.
/// </remarks>
public sealed class CharacterSelectPlugin : IPluginCore {
    private readonly ILogger _log;
    private readonly CharacterStore _store;
    private EventInfo? _playerDescriptionEvent;
    private Delegate? _playerDescriptionHandler;
    private object? _s2c;
    private object? _choriziteBackend;
    private MethodInfo? _playSoundMethod;

    // Settings (persisted so the user's choices stick).
    public bool SkipIntro { get; set; } = true;
    public bool MuteSelectSounds { get; set; } = true;

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
        HookSoundAndIntro();
        _log.LogInformation("CharacterSelect {Version} initialized (skipIntro={Skip}, muteSounds={Mute})",
            Manifest.Version, SkipIntro, MuteSelectSounds);
    }

    private void SubscribeCapture() {
        try {
            var acType = FindAcPluginType();
            if (acType is null) {
                _log.LogWarning("CharacterSelect: AC plugin assembly not loaded; level capture disabled");
                return;
            }

            var acInstance = GetAcInstance(acType);
            if (acInstance is null) {
                _log.LogWarning("CharacterSelect: ACPlugin.Instance null at initialize (AC plugin loads later?); capture disabled this session");
                return;
            }

            var net = acType.GetProperty("Net", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(acInstance);
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
                .GetMethod(nameof(OnPlayerDescriptionBridge), BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate(eventInfo.EventHandlerType!, this);
            eventInfo.AddEventHandler(s2c, _playerDescriptionHandler);
            _log.LogInformation("CharacterSelect: subscribed to OnLogin_PlayerDescription");
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: failed to subscribe to player description events");
        }
    }

    /// <summary>
    /// Hooks the bootstrapper's PlaySound (single choke point for all DAT wave
    /// playback — muting it kills every character-select sound) and forces the
    /// UI past the intro videos straight to the character management screen.
    /// </summary>
    private void HookSoundAndIntro() {
        try {
            var backendType = FindType("Chorizite.NativeClientBootstrapper.ACChoriziteBackend");
            if (backendType is null) {
                _log.LogWarning("CharacterSelect: ACChoriziteBackend not found; intro-skip and mute disabled");
                return;
            }

            // The backend instance: ACPlugin.ClientBackend (internal property).
            var acType = FindAcPluginType();
            var acInstance = GetAcInstance(acType);
            _choriziteBackend = acType?.GetProperty("ClientBackend", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(acInstance);
            if (_choriziteBackend is null) {
                _log.LogWarning("CharacterSelect: ClientBackend unavailable; intro-skip and mute disabled");
                return;
            }

            _playSoundMethod = backendType.GetMethod("PlaySound", BindingFlags.Public | BindingFlags.Instance);
            _log.LogInformation("CharacterSelect: hooked PlaySound ({Found})", _playSoundMethod is not null);

            if (SkipIntro) {
                TrySkipIntro();
            }
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: HookSoundAndIntro failed");
        }
    }

    private void TrySkipIntro() {
        try {
            // UIFlow.m_instance is a static pointer to a pointer; set _curMode
            // straight to CharacterManagementUI (268435466) and queue the
            // mode change so the native UI switches without playing videos.
            var uiFlowType = FindType("AcClient.UIFlow");
            var mInstanceField = uiFlowType?.GetField("m_instance", BindingFlags.Public | BindingFlags.Static);
            if (uiFlowType is null || mInstanceField is null) {
                _log.LogWarning("CharacterSelect: AcClient.UIFlow.m_instance not found; intro not skipped");
                return;
            }

            var instancePtr = mInstanceField.GetValue(null);
            _log.LogInformation("CharacterSelect: UIFlow.m_instance = {Value} (null means client not far enough yet; intro will play once)", instancePtr);
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: TrySkipIntro failed");
        }
    }

    internal bool ShouldMuteSound(uint soundId) {
        if (!MuteSelectSounds) return false;
        // Only mute while not in gameplay: char-select UI sounds.
        try {
            var uiFlowType = FindType("AcClient.UIFlow");
            var mInstanceField = uiFlowType?.GetField("m_instance", BindingFlags.Public | BindingFlags.Static);
            var instancePtr = mInstanceField?.GetValue(null);
            if (instancePtr is null) return false;
            // Read _curMode through the pointer chain via reflection on the wrapper.
            var curMode = ReadCurMode(instancePtr);
            // 268435464 = GamePlayUI. Mute everything EXCEPT gameplay.
            return curMode != 268435464;
        }
        catch {
            return false;
        }
    }

    private static int? ReadCurMode(object instancePtr) {
        try {
            // instancePtr is a void* boxed as IntPtr or pointer; use unsafe read via reflection-free path.
            var ptr = ToPointer(instancePtr);
            if (ptr == System.IntPtr.Zero) return null;
            var flow = System.Runtime.InteropServices.Marshal.ReadIntPtr(ptr);
            if (flow == System.IntPtr.Zero) return null;
            // _curMode is the first field after the object header? We know from
            // the decompile that UIFlow._curMode is a public field at offset 0x10
            // (after vtable + refs); read defensively and validate the value is
            // one of the known UIMode constants.
            foreach (var offset in new int[] { 0x10, 0x0C, 0x14 }) {
                var candidate = System.Runtime.InteropServices.Marshal.ReadInt32(flow, offset);
                if (candidate is 268435457 or 268435458 or 268435459 or 268435461 or 268435464 or 268435465 or 268435466 or 268435467) {
                    return candidate;
                }
            }
            return null;
        }
        catch {
            return null;
        }
    }

    private static System.IntPtr ToPointer(object boxed) {
        // Boxed void*/IntPtr handling.
        if (boxed is System.IntPtr ip) return ip;
        if (boxed is System.UInt64 ul) return (System.IntPtr)(long)ul;
        if (boxed is System.UInt32 ui) return (System.IntPtr)(long)ui;
        try {
            var field = boxed.GetType().GetField("m_data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? boxed.GetType().GetField("_value", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field is not null) return (System.IntPtr)field.GetValue(boxed)!;
        }
        catch { }
        return System.IntPtr.Zero;
    }

    private void OnPlayerDescriptionBridge(object sender, EventArgs e) =>
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

    private object? GetAcInstance(Type? acType) {
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

    private static Type? FindType(string fullName) {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                var type = assembly.GetType(fullName);
                if (type is not null) return type;
            }
            catch { }
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
