using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Chorizite.Core.Backend.Client;
using Chorizite.Core.Plugins;
using Chorizite.Core.Plugins.AssemblyLoader;
using Microsoft.Extensions.Logging;
using RmlUi;

namespace CharacterSelect;

/// <summary>
/// Toggleable plugin settings, persisted by the plugin loader to
/// <c>&lt;DataDirectory&gt;/settings.json</c> via <see cref="ISerializeSettings{T}"/>.
/// </summary>
public sealed class CspSettings {
    public bool SkipIntro { get; set; } = true;
    public bool MuteSelectSounds { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CspSettings))]
internal sealed partial class CspSettingsContext : JsonSerializerContext { }

/// <summary>
/// Character Select Plus: replaces the AC plugin's character select screen with
/// one that shows the population on two lines, plus each character's
/// last-known level and allegiance. Also skips the intro videos and mutes
/// character-select sounds (both toggleable, persisted to settings.json).
/// </summary>
/// <remarks>
/// The screen override and the typed <c>IClientBackend</c> surface
/// (<c>GameScreen</c>, <c>UIBackend.OnScreenChanged</c>) come from Chorizite.Core.
/// The AC plugin's <c>ACPlugin.Instance</c>/<c>Net</c> members and the
/// bootstrapper's private <c>_audioEngines</c> dictionary are reached through
/// reflection so a mismatched AC plugin version degrades to "feature off" with
/// a log line instead of crashing the client.
/// </remarks>
public sealed class CharacterSelectPlugin : IPluginCore, ISerializeSettings<CspSettings> {
    /// <summary>UIMode.IntroUI — plays the intro videos.</summary>
    private const int UIModeIntroUi = 268435457;               // 0x10000001
    /// <summary>UIMode.GamePlayUI — in-world; never mute here.</summary>
    private const int UIModeGamePlayUi = 268435464;            // 0x10000008
    /// <summary>UIMode.CharacterManagementUI — the character select screen.</summary>
    private const int UIModeCharacterManagementUi = 268435466; // 0x1000000A

    private readonly ILogger _log;
    private readonly CharacterStore _store;
    private EventInfo? _playerDescriptionEvent;
    private Delegate? _playerDescriptionHandler;
    private object? _s2c;
    private IClientBackend? _clientBackend;
    private Timer? _uiWatchdog;
    private int _watchdogActive;
    private bool? _lastMutedApplied;

    // Reflection handles into the bootstrapper's audio stack, resolved lazily:
    // ACChoriziteBackend._audioEngines is a Dictionary<int, AudioPlaybackEngine>;
    // each engine holds a NAudio WaveOutEvent in its private `outputDevice`
    // field, and WaveOutEvent.Volume (float, 0..1) is the mute surface.
    private FieldInfo? _audioEnginesField;
    private FieldInfo? _outputDeviceField;
    private PropertyInfo? _deviceVolumeProperty;

    // Settings (persisted to settings.json by the loader via ISerializeSettings).
    public bool SkipIntro { get; private set; } = true;
    public bool MuteSelectSounds { get; private set; } = true;

    public CharacterSelectPlugin(
        AssemblyPluginManifest manifest,
        ILogger<CharacterSelectPlugin> log,
        IClientBackend? clientBackend = null) : base(manifest) {
        _log = log;
        _store = new CharacterStore(DataDirectory);
        _clientBackend = clientBackend;
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
        HookSoundIntroAndAudio();
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
            var handlerType = eventInfo.EventHandlerType!;
            var argsType = handlerType.GetMethod("Invoke")!.GetParameters()[1].ParameterType;
            var openBridge = GetType().GetMethod(
                nameof(OnPlayerDescriptionBridge), BindingFlags.NonPublic | BindingFlags.Instance)!;
            // Delegate.CreateDelegate demands EXACT parameter types. The AC
            // plugin's event-args type cannot be named at compile time, so close
            // the generic bridge over the type taken from the delegate itself and
            // bind `this` as the instance target. The previous (object, EventArgs)
            // bridge could never bind (ArgumentException at every startup), so
            // capture never fired and no character ever got a level.
            _playerDescriptionHandler = Delegate.CreateDelegate(handlerType, this, openBridge.MakeGenericMethod(argsType));
            eventInfo.AddEventHandler(s2c, _playerDescriptionHandler);
            _log.LogInformation("CharacterSelect: subscribed to OnLogin_PlayerDescription ({HandlerType})", handlerType.FullName);
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: failed to subscribe to player description events");
        }
    }

    /// <summary>
    /// Wires the intro skip and the sound mute to the typed
    /// <c>IClientBackend</c>: <c>GameScreen</c> is a plain int property whose
    /// setter queues a native <c>UIFlow::QueueUIMode</c>, and
    /// <c>UIBackend.OnScreenChanged</c> fires (from a native hook on
    /// <c>UIFlow::UseNewMode</c>) after every UI mode switch.
    /// </summary>
    private void HookSoundIntroAndAudio() {
        try {
            if (_clientBackend is null) {
                // The loader only injects IClientBackend when the DI container
                // registers it; fall back to ACPlugin.ClientBackend. Both the
                // type and the instance must exist — GetValue(null) throws
                // TargetException when the AC plugin has not initialized yet.
                var acType = FindAcPluginType();
                var acInstance = GetAcInstance(acType);
                if (acType is null || acInstance is null) {
                    _log.LogWarning("CharacterSelect: AC plugin not ready for the ClientBackend fallback (loads later?); intro-skip and mute disabled this session");
                    return;
                }
                if (acType.GetProperty("ClientBackend", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(acInstance) is IClientBackend backend) {
                    _clientBackend = backend;
                }
            }
            if (_clientBackend is null) {
                _log.LogWarning("CharacterSelect: IClientBackend unavailable; intro-skip and mute disabled");
                return;
            }

            _clientBackend.UIBackend.OnScreenChanged += OnScreenChanged;
            _log.LogInformation("CharacterSelect: subscribed to UIBackend.OnScreenChanged (backend={Backend})",
                _clientBackend.GetType().Name);

            _uiWatchdog = new Timer(_ => UiWatchdogTick(), null,
                TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            _log.LogInformation("CharacterSelect: audio engines discovered={Engines} (private _audioEngines on {Backend})",
                CountAudioEngines(), _clientBackend.GetType().Name);

            if (SkipIntro) {
                SkipIntroNow("initialize");
            }
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: HookSoundIntroAndAudio failed");
        }
    }

    /// <summary>
    /// Fired on the game thread by the bootstrapper's native
    /// <c>UIFlow::UseNewMode</c> hook after each UI mode switch. Never let an
    /// exception escape back into native code.
    /// </summary>
    private void OnScreenChanged(object? sender, EventArgs e) {
        try {
            if (SkipIntro && CurrentScreen() == UIModeIntroUi) {
                SkipIntroNow("screen change");
            }
        }
        catch { /* swallow — see remarks */ }
    }

    /// <summary>
    /// Cheap catch-all so features survive a missed event: re-checks the screen
    /// (intro may already be playing before we subscribed) and re-applies mute
    /// volumes to any audio engines created since the last pass (engines are
    /// created lazily per sample rate by the bootstrapper's PlaySound).
    /// </summary>
    private void UiWatchdogTick() {
        if (Interlocked.Exchange(ref _watchdogActive, 1) == 1) return;
        try {
            if (SkipIntro && CurrentScreen() == UIModeIntroUi) {
                SkipIntroNow("watchdog");
            }
            ApplySoundVolumes();
        }
        catch { /* the watchdog must never crash the client */ }
        finally {
            Interlocked.Exchange(ref _watchdogActive, 0);
        }
    }

    private int CurrentScreen() => _clientBackend?.GameScreen ?? 0;

    private void SkipIntroNow(string trigger) {
        try {
            if (_clientBackend is null || CurrentScreen() != UIModeIntroUi) return;
            // The GameScreen setter is a no-op (with a core log line) while
            // UIFlow.m_instance is still null, and queues the mode natively
            // otherwise. The raw value is the same uint the RML screen's Lua
            // fallback passes to SetScreen.
            _clientBackend.GameScreen = UIModeCharacterManagementUi;
            _log.LogInformation("CharacterSelect: queued CharacterManagementUI ({Mode}); intro skipped via {Trigger}",
                UIModeCharacterManagementUi, trigger);
        }
        catch (Exception ex) {
            _log.LogWarning(ex, "CharacterSelect: intro skip via {Trigger} failed", trigger);
        }
    }

    private int CountAudioEngines() {
        try {
            if (GetAudioEngines() is IDictionary engines) return engines.Count;
        }
        catch { }
        return 0;
    }

    private IDictionary? GetAudioEngines() {
        if (_clientBackend is null) return null;
        _audioEnginesField ??= _clientBackend.GetType().GetField("_audioEngines", BindingFlags.NonPublic | BindingFlags.Instance);
        return _audioEnginesField?.GetValue(_clientBackend) as IDictionary;
    }

    /// <summary>
    /// Mutes (volume 0) or restores (volume 1) every audio engine the
    /// bootstrapper has created. Muting while not in gameplay kills every
    /// character-select / intro / login sound without hooking PlaySound.
    /// </summary>
    private void ApplySoundVolumes() {
        var mute = MuteSelectSounds && CurrentScreen() != UIModeGamePlayUi;
        var count = SetEngineVolumes(mute);
        if (_lastMutedApplied != mute) {
            _log.LogInformation("CharacterSelect: {State} {Count} audio engine(s) (screen={Screen})",
                mute ? "muted" : "restored volume on", count, CurrentScreen());
            _lastMutedApplied = mute;
        }
    }

    private int SetEngineVolumes(bool mute) {
        var engines = GetAudioEngines();
        if (engines is null) return 0;

        // Snapshot: the bootstrapper adds engines from the game thread while
        // this runs on the watchdog thread.
        object[] engineList;
        try {
            engineList = engines.Values.Cast<object>().ToArray();
        }
        catch {
            return 0;
        }

        var desired = mute ? 0f : 1f;
        var touched = 0;
        foreach (var engine in engineList) {
            try {
                _outputDeviceField ??= engine.GetType().GetField("outputDevice", BindingFlags.NonPublic | BindingFlags.Instance);
                var device = _outputDeviceField?.GetValue(engine);
                if (device is null) continue;
                _deviceVolumeProperty ??= device.GetType().GetProperty("Volume");
                if (_deviceVolumeProperty?.PropertyType != typeof(float)) continue;
                var current = (float?)_deviceVolumeProperty.GetValue(device);
                if (Math.Abs((current ?? 1f) - desired) > 0.01f) {
                    _deviceVolumeProperty.SetValue(device, desired);
                    touched++;
                }
            }
            catch { /* engine may be mid-dispose; skip it */ }
        }
        return touched;
    }

    /// <summary>
    /// Closed over the real event-args type at runtime (see SubscribeCapture).
    /// Once the generic parameter is fixed, the remaining signature
    /// (object sender, TArgs e) exactly matches EventHandler&lt;TArgs&gt; — and any
    /// custom delegate with the same shape — so CreateDelegate can bind it with
    /// this instance as target (which also keeps the WeakEvent subscription alive).
    /// </summary>
    private void OnPlayerDescriptionBridge<TArgs>(object? sender, TArgs e) =>
        CaptureFromPlayerDescription(e!);

    private void CaptureFromPlayerDescription(object e) {
        try {
            var baseQualities = GetMemberValue(e, "BaseQualities");
            if (baseQualities is null) {
                _log.LogWarning("CharacterSelect: Login_PlayerDescription.BaseQualities was null");
                return;
            }

            var level = 0;
            var allegiance = "";
            var monarchName = "";

            // PropertyInt.Level = 25, PropertyString.AllegianceName = 47,
            // PropertyString.MonarchsName = 11.
            if (GetMemberValue(baseQualities, "IntProperties") is IDictionary intProps) {
                foreach (DictionaryEntry entry in intProps) {
                    if (Convert.ToUInt32(entry.Key) == 25u) level = Convert.ToInt32(entry.Value);
                }
            }

            if (GetMemberValue(baseQualities, "StringProperties") is IDictionary stringProps) {
                foreach (DictionaryEntry entry in stringProps) {
                    var key = Convert.ToUInt32(entry.Key);
                    if (key == 47u) allegiance = entry.Value?.ToString() ?? "";
                    else if (key == 11u) monarchName = entry.Value?.ToString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(allegiance) && !string.IsNullOrEmpty(monarchName)) {
                // Some servers never send AllegianceName (47) in the player's
                // own description; the monarch name is the next-best allegiance
                // line (the InstanceValues/Monarch -> World.Get path stays a
                // possible upgrade).
                allegiance = monarchName;
                _log.LogInformation("CharacterSelect: allegiance name empty; using monarch name '{Monarch}' as allegiance", monarchName);
            }

            var (charId, charName) = CurrentCharacter();
            if (charId == 0 || string.IsNullOrEmpty(charName)) {
                _log.LogWarning("CharacterSelect: player description received but no current character known; not recording");
                return;
            }

            _store.Record(charId, charName, level, allegiance);
            _log.LogInformation(
                "CharacterSelect captured {Name} (0x{Id:X8}): level {Level}, allegiance '{Allegiance}'",
                charName, charId, level, allegiance);
        }
        catch (Exception ex) {
            _log.LogError(ex, "CharacterSelect: failed to capture player description");
        }
    }

    /// <summary>Reads a field or property by name, whichever exists.</summary>
    private static object? GetMemberValue(object target, string name) {
        var t = target.GetType();
        return t.GetField(name)?.GetValue(target) ?? t.GetProperty(name)?.GetValue(target);
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

    /// <summary>JSON blob for one character, or null when unknown. Called from the screen's Lua.</summary>
    /// <remarks>Must be an INSTANCE method: require('Plugins.CharacterSelect') from Lua returns the
    /// plugin instance object, and XLua can only see instance members on it — static classes are invisible.</remarks>
    public string? Lookup(uint id) {
        return _store.TryGet(id, out var info)
            ? System.Text.Json.JsonSerializer.Serialize(info)
            : null;
    }

    /// <summary>Records facts captured in-world. Callable from Lua (instance method).</summary>
    public void Record(uint id, string name, int level, string allegiance) {
        _store.Record(id, name, level, allegiance);
    }

    /// <summary>Last-known level for a character id (0 = unknown). Instance method: callable from Lua as csp:GetLevel(id).</summary>
    public int GetLevel(uint id) => _store.TryGet(id, out var info) ? info.Level : 0;

    /// <summary>Last-known allegiance for a character id ("" when unknown). Instance method: callable from Lua as csp:GetAllegiance(id).</summary>
    public string GetAllegiance(uint id) => _store.TryGet(id, out var info) ? info.Allegiance : "";

    protected override void Dispose() {
        _uiWatchdog?.Dispose();
        _uiWatchdog = null;

        if (_clientBackend is not null) {
            try {
                _clientBackend.UIBackend.OnScreenChanged -= OnScreenChanged;
            }
            catch (Exception ex) {
                _log.LogWarning(ex, "CharacterSelect: OnScreenChanged unsubscribe failed");
            }
            try {
                // Return the audio devices to the client's control if we are
                // unloading while muted.
                SetEngineVolumes(mute: false);
            }
            catch { }
        }

        try {
            if (_s2c is not null && _playerDescriptionEvent is not null && _playerDescriptionHandler is not null) {
                _playerDescriptionEvent.RemoveEventHandler(_s2c, _playerDescriptionHandler);
            }
        }
        catch (Exception ex) {
            _log.LogWarning(ex, "CharacterSelect: unsubscribe failed");
        }
    }

    // ---- ISerializeSettings<CspSettings> (invoked by the plugin loader) ----

    JsonTypeInfo<CspSettings> ISerializeSettings<CspSettings>.TypeInfo =>
        CspSettingsContext.Default.CspSettings;

    CspSettings ISerializeSettings<CspSettings>.SerializeBeforeUnload() =>
        new() { SkipIntro = SkipIntro, MuteSelectSounds = MuteSelectSounds };

    void ISerializeSettings<CspSettings>.DeserializeAfterLoad(CspSettings? settings) {
        if (settings is null) return;
        SkipIntro = settings.SkipIntro;
        MuteSelectSounds = settings.MuteSelectSounds;
        _log.LogInformation("CharacterSelect settings loaded: skipIntro={Skip}, muteSounds={Mute}",
            SkipIntro, MuteSelectSounds);
    }
}
