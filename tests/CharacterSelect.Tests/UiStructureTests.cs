using System;
using System.IO;
using Xunit;

namespace CharacterSelect.Tests;

public class ScreenStructureTests {
    private static string ReadRml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "screens", "CharSelect.rml"));

    private static string ReadPlugin() =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterSelectPlugin.cs")));

    [Fact]
    public void PopulationBoxRendersNameAndPopulationOnSeparateLines() {
        var rml = ReadRml();

        // Two-line world box: name first, then population label.
        Assert.Contains("class = \"world-name\"", rml);
        Assert.Contains("\"Population: \" .. (state.Population or \"\")", rml);
        Assert.Matches(@"\.world-name \{[^}]*display: block", rml);
        // The single-line format string from the stock screen is gone.
        Assert.DoesNotContain("%s %d / %d", ReadRml());
    }

    [Fact]
    public void CharacterRowsShowNameLevelAndAllegiance() {
        var rml = ReadRml();

        // Name left, large level right, allegiance underneath in angle brackets.
        Assert.Contains("class = \"char-name\"", rml);
        Assert.Contains("char-allegiance", rml);
        Assert.Matches(@"\.char-level \{[^}]*font-size: 20px", rml);
        Assert.Matches(@"\.char-level \{[^}]*right: 8px", rml);
        // The big level number must not touch the row's top edge (0.1.6 fix).
        Assert.Matches(@"\.char-level \{[^}]*top: 4px", rml);
        // Angle-bracket allegiance rendering.
        Assert.Contains("\"<\" .. char.Allegiance .. \">\"", rml);
    }

    [Fact]
    public void PluginPersistsCapturedFactsAndRegistersScreen() {
        var cs = ReadPlugin();

        // Screen override attempt is logged with its result.
        Assert.Contains("RegisterScreen(\"CharSelect\"", cs);
        // Capture subscribes to the AC plugin's player-description event.
        Assert.Contains("OnLogin_PlayerDescription", cs);
        // Property ids: Level = PropertyInt 25, AllegianceName = PropertyString 47.
        Assert.Contains("== 25", cs);
        Assert.Contains("== 47", cs);
        // Store persists to characters.json.
        Assert.Contains("characters.json", ReadStore());
    }

    [Fact]
    public void CaptureBridgeBindsTheExactDelegateSignature() {
        var cs = ReadPlugin();

        // The capture delegate cannot name the AC plugin's event-args type at
        // compile time, so it must close a generic bridge over the type taken
        // from the delegate itself and bind the plugin as the instance target.
        // A plain (object, EventArgs) instance bridge throws ArgumentException
        // ("signature is not compatible") at every startup — the 0.1.2 bug that
        // meant no character ever recorded a level.
        Assert.Contains("OnPlayerDescriptionBridge<TArgs>", cs);
        Assert.Contains("MakeGenericMethod(argsType)", cs);
        Assert.Contains("Delegate.CreateDelegate(handlerType, this, openBridge.MakeGenericMethod(argsType))", cs);
        Assert.DoesNotContain("OnPlayerDescriptionBridge(object sender, EventArgs e)", cs);
    }

    private static string ReadStore() =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterStore.cs")));
}

public class FeatureWiringTests {
    private static string ReadPlugin() =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CharacterSelect", "CharacterSelectPlugin.cs")));

    [Fact]
    public void IntroSkipUsesTypedBackendScreenChangedAndWatchdog() {
        var cs = ReadPlugin();

        // The typed IClientBackend surface (Chorizite.Core): GameScreen setter
        // queues the native UIFlow::QueueUIMode; OnScreenChanged fires after
        // every mode switch from the bootstrapper's UseNewMode hook.
        Assert.Contains("_clientBackend.UIBackend.OnScreenChanged += OnScreenChanged;", cs);
        Assert.Contains("_clientBackend.GameScreen = UIModeCharacterManagementUi;", cs);

        // Verified UIMode values (decompiled _Enums.cs): IntroUI = 0x10000001,
        // GamePlayUI = 0x10000008, CharacterManagementUI = 0x1000000A.
        Assert.Contains("private const int UIModeIntroUi = 268435457;", cs);
        Assert.Contains("private const int UIModeGamePlayUi = 268435464;", cs);
        Assert.Contains("private const int UIModeCharacterManagementUi = 268435466;", cs);

        // The watchdog re-checks on a timer so a missed event (intro already
        // playing before we subscribed) is still caught.
        Assert.Contains("SkipIntroNow(\"watchdog\")", cs);
    }

    [Fact]
    public void SoundMuteTogglesEngineDeviceVolumes() {
        var cs = ReadPlugin();

        // No IL hook on PlaySound: mute via the bootstrapper's private
        // _audioEngines dictionary -> each engine's NAudio WaveOutEvent Volume.
        Assert.Contains("GetField(\"_audioEngines\"", cs);
        Assert.Contains("GetField(\"outputDevice\"", cs);
        Assert.Contains("GetProperty(\"Volume\")", cs);
        // Never mute during gameplay.
        Assert.Contains("CurrentScreen() != UIModeGamePlayUi", cs);
        // Unmute on dispose so unloading mid-mute doesn't leave the client silent.
        Assert.Contains("SetEngineVolumes(mute: false)", cs);
    }

    [Fact]
    public void AllegianceFallsBackToMonarchName() {
        var cs = ReadPlugin();

        // PropertyString.AllegianceName = 47; PropertyString.MonarchsName = 11
        // (verified against Chorizite.Common 1.0.2) as the fallback source.
        Assert.Contains("else if (key == 11u) monarchName", cs);
        Assert.Contains("using monarch name", cs);
    }

    [Fact]
    public void SettingsPersistThroughLoaderContract() {
        var cs = ReadPlugin();

        // The loader reads/writes <DataDirectory>/settings.json for any plugin
        // implementing ISerializeSettings<T> (TypeInfo + Serialize/Deserialize).
        Assert.Contains("ISerializeSettings<CspSettings>", cs);
        Assert.Contains("CspSettingsContext.Default.CspSettings", cs);
        Assert.Contains("DeserializeAfterLoad(CspSettings? settings)", cs);
    }

    [Fact]
    public void BackendFallbackWaitsForAcPluginInstance() {
        var cs = ReadPlugin();

        // ACPlugin.Instance can be null when the AC plugin initializes after
        // us; GetValue(null) there throws TargetException (0.1.3 log line).
        Assert.Contains("acType is null || acInstance is null", cs);
    }

    [Fact]
    public void WatchdogReRegistersTheScreenAfterRmlUiReloads() {
        var cs = ReadPlugin();

        // Mid-session core-plugin reload cycles (Lua/RmlUi unload+reload)
        // reset RmlUi's screen registry; AC and this plugin are not part of
        // those cycles, so nothing re-registers "CharSelect" and the client
        // falls back to the native character select. The watchdog must heal
        // the registration.
        Assert.Contains("_screenRmlPath = Path.Combine(AssemblyDirectory", cs);
        Assert.Contains("RmlUiPlugin.Instance?.RegisterScreen(\"CharSelect\", _screenRmlPath);", cs);
        Assert.Contains("RegisterScreen(\"CharSelect\", _screenRmlPath);", cs);
    }
}

public class ScreenScriptTests {
    private static string ReadRml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "screens", "CharSelect.rml"));

    [Fact]
    public void LuaCallsInstanceMethodsWithColonSyntaxAndNoShadowedJson() {
        var rml = ReadRml();

        // XLua: C# instance methods are called with COLON syntax. Dot syntax
        // tries to convert the argument to the plugin instance and errors —
        // the pcall in factsFor swallowed it, so rows always showed "Level ?".
        Assert.Contains("csp[fnName](csp, id)", rml);
        // The json module must come from require('json'); the JSON string
        // must not shadow the module when decoding.
        Assert.Contains("pcall(require, 'json')", rml);
        Assert.Contains("pcall(jsonlib.decode, raw)", rml);
        Assert.DoesNotContain("pcall(json.decode", rml);
        // Primitive accessors exist on the plugin for the primary path.
        Assert.Contains("callInstance('GetLevel', id)", rml);
    }

    [Fact]
    public void PopulationTextSitsInsideTheBoxArtInterior() {
        var rml = ReadRml();

        // Box art 0x06004D64 is 193x110: opaque bevel rows 25-30 and 77-82,
        // transparent interior rows 31-76. The text lines are absolutely
        // positioned inside that interior (centered block: 35 + 20 + 16).
        Assert.Matches(@"\.world-name \{[^}]*position: absolute; top: 35px", rml);
        Assert.Matches(@"\.world-population \{[^}]*position: absolute; top: 55px", rml);
        // The old padding-top approach (unreliable here) is gone.
        Assert.DoesNotContain("padding-top", rml);
    }

    [Fact]
    public void LevelNumbersAnchorToTheirRowAndRowsFitThePanel() {
        var rml = ReadRml();

        // .char-level is position:absolute; without position:relative on the
        // row, every level number anchors to the #panel and they all pile at
        // the panel's top-right (the many-characters render bug).
        Assert.Matches(@"#panel li \{[^}]*position: relative", rml);
        // Rows beyond 6 use the compact class so rows stay inside the panel
        // art (292px content: 42px x 6, or 29px x 10).
        Assert.Matches(@"#panel li\.compact \{[^}]*height: 29px", rml);
        Assert.Contains("compact = #state.Characters > 6", rml);
    }
}
