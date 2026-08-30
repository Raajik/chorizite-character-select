# Character Select Plus — Handoff

## Project boundary

Standalone Chorizite **Client-environment** plugin. Replaces the AC plugin's character select screen with an enhanced one (two-line population box, last-known level, allegiance). NOT a launcher plugin; shares nothing with Server Browser.

- Workspace: `A:\ai\projects\chorizite-mods\chorizite-character-select`
- Product: `CharacterSelect` (manifest id `CharacterSelect`, name "Character Select Plus")
- Installed to: `C:\Games\Chorizite\plugins\CharacterSelect`
- Runtime data: `C:\Games\Chorizite\data\CharacterSelect\` (`characters.json`, `settings.json`)
- Depends on: AC plugin (`plugins\AC` 0.0.5), Lua 0.0.13, RmlUi 0.0.11, Chorizite 0.0.15 stack.
- Current version: **0.1.8** · GitHub: https://github.com/Raajik/chorizite-character-select (CI on push/PR; tagging `v<manifest version>` publishes a release zip)

## Validated facts (all from decompiling the installed stack)

- **Screen override works**: RmlUi keeps `_gameScreenRmls[name] → rmlPath` and re-registering a name overwrites it. Our `RegisterScreen("CharSelect", ours)` at Initialize wins over AC's own registration (AC registers CharSelect first at its Initialize; log line `Showing document CharSelect ...plugins\CharacterSelect\assets\screens\CharSelect.rml` confirmed ours loads).
- **Key C# property IDs** (Chorizite.Common 1.0.2, cross-checked against AC.dll's own decompiled accessors): `PropertyInt.Level = 25` (Creature.Level uses it), `PropertyInstanceId.Monarch = 26`, `PropertyString.AllegianceName = 47`, `PropertyString.MonarchsName = 11`, `PropertyString.Name = 1`. (The `Property*` enums live in **Chorizite.Common**, not Chorizite.ACProtocol — verified by loading the NuGet DLL.)
- **Capture event**: `ACPlugin.Net.S2C.OnLogin_PlayerDescription` (an `EventHandler<Login_PlayerDescription>`; `BaseQualities.IntProperties/StringProperties` dictionaries). AC's own Character class subscribes the same way.
- **Typed backend surface** (Chorizite.Core 0.0.13, verified against the NuGet DLL): `Chorizite.Core.Backend.Client.IClientBackend` declares `int GameScreen { get; set; }` and `IClientUIBackend UIBackend { get; }` with `event EventHandler<EventArgs> OnScreenChanged`. The ACChoriziteBackend `GameScreen` setter queues `UIFlow.m_instance->QueueUIMode` natively (no-op with a core log line while `m_instance` is null) and the getter returns 0 until the flow exists — safe to call any time. The plugin ctor takes `IClientBackend? = null`: the loader's `ResolveParameter` DI-injects it when registered; the plugin falls back to reflecting `ACPlugin.ClientBackend` otherwise.
- **OnScreenChanged is real**: raised in `Chorizite.NativeClientBootstrapper`'s native hook `UIFlow_UseNewMode_Impl` (Hooks/ACClientHooks.cs) — i.e. **synchronously on the game thread right after each UI mode switch**. Exceptions escaping a handler there run inside native code, so our handler swallows everything.
- **UIMode values** (AcClient `_Enums.cs`): `IntroUI = 0x10000001` (268435457), `DisconnectedUI = 0x10000002`, `DataPatchUI = 0x10000003`, `CreditsUI = 0x10000005`, `GamePlayUI = 0x10000008` (268435464), `EpilogueUI = 0x10000009`, `CharacterManagementUI = 0x1000000A` (268435466), `CharGenMainUI = 0x1000000B` (268435467).
- **Audio stack** (bootstrapper decompile): every DAT wave playback funnels through `ACChoriziteBackend.PlaySound(uint)` into a private `Dictionary<int, AudioPlaybackEngine> _audioEngines` (keyed by sample rate, engines created lazily on first PlaySound of that rate). **`AudioPlaybackEngine` has NO Volume property** — its private `outputDevice` field is a NAudio `WaveOutEvent`, which **does** have `float Volume { get; set; }` (backed by `waveOutSetVolume`; the getter reads a cached value, so polling it is cheap). Muting that per-engine device is the sanctioned cheap mute; no IL hook needed.
- **Plugin settings contract** (Chorizite.Core decompile): a plugin implementing `ISerializeSettings<T>` (protected `TypeInfo: JsonTypeInfo<T>`, `SerializeBeforeUnload() → T`, `DeserializeAfterLoad(T?)`) gets `<DataDirectory>/settings.json` read at construction (before `Initialize()`) and written at unload. Implement `TypeInfo` via a source-gen `JsonSerializerContext` (in-box System.Text.Json; type identity with the host works because the package is `ExcludeAssets="runtime"`).
- **XLua enum casting is unreliable**: `ac:SetScreen(GameScreen.CharCreate)` from RML Lua throws `invalid value for enum AC.Lib.Screens.GameScreen` (XLua ObjectCasters). **Pass the raw uint** (`268435467`) — pcall + fallback pattern in CharSelect.rml.
- **XLua method calls need COLON syntax**: for C# *instance* methods, `csp:Method(id)` / `csp[fnName](csp, id)` is correct; `csp.Method(id)` (dot) tries to convert the first argument to the plugin object and errors. Properties are the opposite (`ac.ServerName`). Proven idiom in the same screen: `ac:Login(state.SelectedId)`. The 0.1.2 fix moved `Lookup` onto the instance but kept the dot call — the pcall in `factsFor` swallowed the error every time, so the display path never worked even when the store had data (0.1.5 finding).
- **XLua numbers surface as floats**: C# `int` properties read from Lua arrive as `0.0`/`128.0`. Format with `string.format("%d", v)` before display (plain `tostring` renders "1.0").
- **Lua↔C# visibility (critical)**: `require('Plugins.<Id>')` returns the plugin *instance object* (`AssemblyPluginInstance.Instance` = the IPluginCore). **XLua only sees instance members on it — a separate C# static class is invisible.** 0.1.1's `Lookup` sat in a static `CharacterStoreApi` class, so `csp.Lookup(id)` silently returned nil and every row showed `Level ?`. Fixed in 0.1.2 by moving `Lookup(uint)`/`Record(...)` onto the `CharacterSelectPlugin` instance (public instance methods).
- **World box art geometry** (dat://0x06004D64, extracted 193×110 BGRA): fully transparent rows 0–24 and 83–109; opaque bevel bands rows 25–30 (bright cyan highlight) and 77–82 (bottom edge); **transparent interior rows 31–76 (45px tall, center y≈53.5)** with only thin side rails. The box art stretches horizontally to the 250px span. Text must sit inside rows ~31–76. The stock screen centered its single line via `line-height: 110px`.
- **DAT extraction workflow** (works, no vision needed): AC client dats live at `C:\Games\RynthCore\AcClient\client_portal.dat`. Scratch console app (`A:\tmp\datprobe`) referencing **the Chorizite install's own** `DatReaderWriter.dll` + `SixLabors.ImageSharp.dll` (`<Reference><HintPath>C:\Games\Chorizite\...dll</HintPath>`); open with `new DatDatabase(o => o.FilePath = path, new StreamBlockAllocator(opts))`, `dat.TryGetFileBytes(id, out var bytes)`; SurfaceTexture (0x06) files parse as `[id u32][type u32 = 6][width u32][height u32][tag u32][len u32][raw BGRA...]` (big textures like the 0x06004d63 background embed JPEG instead). Then dump an ASCII density map (alpha+brightness per row) to "see" the art without model vision.
- **WeakEvent gotcha**: AC's events are `WeakEvent<T>` — delegates are held by WeakReference with a keep-alive table keyed on `handler.Target`. A delegate created via `CreateDelegate(type, this, method)` keeps `this` alive as Target, so our subscription survives. If handlers ever silently stop firing, suspect the target being collected.
- **RmlUi theme font**: theme.rcss declares `font-family: Tahoma` but only LatoLatin-Regular.ttf ships. Small text renders with a substitute and looks distorted at 12px with `font-effect: outline`. Population line: 13px, no outline, `line-height: 20px`.

## Architecture (this repo)

- `CharacterSelectPlugin` (IPluginCore): Initialize → RegisterScreen override + `SubscribeCapture()` (reflection: ACPlugin.Instance → Net → S2C → closed-generic bridge) + `HookSoundIntroAndAudio()` (typed `IClientBackend` — ctor-injected or reflected from ACPlugin.ClientBackend — subscribes `UIBackend.OnScreenChanged`, starts a 500ms watchdog, lazily resolves `_audioEngines`). Instance methods `Lookup(uint)` / `Record(uint,string,int,string)` are the Lua surface.
- **Intro skip**: `OnScreenChanged` handler + watchdog both call `SkipIntroNow(trigger)`: when the current screen is `IntroUI` (268435457), set `backend.GameScreen = 268435466` (CharacterManagementUI). The watchdog also covers the race where IntroUI is already playing before we subscribed. The watchdog tick is Interlocked-guarded and never throws.
- **Sound mute**: watchdog `ApplySoundVolumes()` — desired state is `MuteSelectSounds && screen != GamePlayUI`; iterates a snapshot of `_audioEngines` values, walks each engine's `outputDevice` (WaveOutEvent) `Volume` property, writes 0/1 only when it differs. Dispose restores volume 1 (never leave the client silent after unloading mid-mute).
- `CharacterStore`: `data/CharacterSelect/characters.json`, `Dictionary<uint, CharacterInfo>` (Id, Name, Level, Allegiance, LastSeenUtc). Load/save both wrapped; corrupt store resets.
- **Settings**: `CspSettings` (SkipIntro, MuteSelectSounds; both default true) + `CspSettingsContext` source-gen; `ISerializeSettings<CspSettings>` implemented explicitly on the plugin → `data/CharacterSelect/settings.json` handled by the loader. `SkipIntro`/`MuteSelectSounds` are `private set` — the only writer besides the ctor is `DeserializeAfterLoad`.
- `assets/screens/CharSelect.rml`: stock layout + two-line population box (`.world-name` / `.world-population`), per-row `.char-name` / `.char-allegiance` (`<name>`) / `.char-level` (20px gold, right-aligned; `.unknown` shows "Level ?"). Lua `factsFor(id)` calls `csp.Lookup(id)` → JSON → row data. `logDebug()` prints `[CharacterSelect] ...` to the log.
- Capture path: `OnLogin_PlayerDescription` → read IntProperties[25] + StringProperties[47] (fallback StringProperties[11] = MonarchsName when 47 is empty) → `CurrentCharacter()` (reflection Game.Character.Id/Name) → `CharacterStore.Record`.

## 0.1.8 — self-healing screen registration (brown-vanilla-screen fix) (2026-08-30)

User report: every 2nd/3rd logoff the char select "reverts to the default" — a brown vanilla-looking screen. Log forensics (13 logoffs total, all with our document mounting and rows rebuilding cleanly — the data path was never at fault):

1. **The log shows mid-session `Reloading plugins` cycles** (3 during the reported sitting, clustered around logoffs; ~26 across the log). Each cycle unloads+reloads **Lua Scripting, RmlUi, Launcher Interface, Plugin Manager UI, Community Server Browser** — but NOT AC / CharacterSelect / Juggernaut. The launcher's own Simple-screen components log `Element is null, skipping prop update` storms right before each cycle, and launcher documents (PluginsBar, Simple, Server Browser) get re-shown right after.
2. **Why the vanilla screen appears**: RmlUi's screen registry resets when the RmlUi plugin reloads. AC and this plugin are not part of the cycle, so nobody re-registers "CharSelect" — the next CharacterManagementUI entry falls back to the **native vanilla char select** (the brown one, with models). It is NOT a custom plugin screen and NOT our document failing. Likely trigger for the mid-session cycles: `deploy.sh` copying new plugin files to disk while the client is open (0.1.6/0.1.7 were deployed mid-session during that sitting).
3. **Fix**: the 500ms watchdog now re-runs `RmlUiPlugin.Instance?.RegisterScreen("CharSelect", _screenRmlPath)` every tick (a cheap map insert — the RML loads at show time), healing the registration within 500ms of any RmlUi reload, and also winning back the name if anything else ever registers over it. Guarded so RmlUi being mid-reload (Instance null / throwing) just waits for the next tick.

**Known non-fix**: the 1–3 second window right after logoff where the document show is still pending (log gap 26–50 lines) can still flash the native UI — core-side timing, out of our control.

**Deployment status (2026-08-30, verified):** 13/13 tests; deployed; DLL probed for `0.1.8`. Going forward: run `deploy.sh` with the client CLOSED, or expect a reload blip (now self-healing).

## 0.1.7 — multi-character layout fix (2026-08-30)

0.1.6 user round on a 9-character account ("really weird shit"): the log proved the data path perfect (9 rows, `Jochi 275`, `Muckfuppet 275`, `Hot Goat Summer 17`, six level-0s) — the breakage was two layout bugs:

1. **All level numbers piled at the panel's top-right.** `.char-level` is `position: absolute`, but `#panel li` was never `position: relative`, so every row's number anchored to the **#panel** instead of its row. With one character it coincidentally looked right — row 1's top == panel content top, which was ALSO the real explanation for 0.1.5's "level touching the top edge" (the 0.1.6 4px nudge only moved the pile). Fix: `#panel li { position: relative; }` — each number now renders inside its own row.
2. **Rows overflowed the panel art.** 9 × 42px = 378px > the 292px panel content; the last rows spilled over the bottom buttons. Fix: a `compact` row class (height 29px, name 12px, allegiance 9px, level 15px) applied from Lua when `#state.Characters > 6` — 29px × 10 = 290px fits the panel; 42px rows stay for ≤6 characters.

Known limit: accounts with >10 characters (28px+ rows needed) would still overflow the panel — none seen (AC caps at 10).

**Deployment status (2026-08-30, verified):** 12/12 tests; deployed; RML probed for `position: relative`, `li.compact { height: 29px`, `compact = #state.Characters > 6`; DLL probed for `0.1.7`.

## 0.1.6 — level number nudge (2026-08-30)

0.1.5 user round: everything works (`looking good`). Only tweak: the big gold level number sat at `top: 0px` of its 42px row, touching the row's top edge — nudged to `top: 4px`. Next check: the same screen on an account with more characters (more data points for rows/allegiance).

## 0.1.5 — level display fix + population box geometry (2026-08-30)

First 0.1.4 user round: intro skip ✓ (`queued CharacterManagementUI (268435466); intro skipped via screen change`), mute ✓ (`muted 1 audio engine(s)`), **capture ✓ (`captured Breeze (0x500002FA): level 1, allegiance ''`, characters.json created)** — but the row still rendered `Level ?` and the two-line box text clipped the art's top bevel.

1. **`Level ?` root cause (two compounding Lua bugs, both now fixed):**
   - **Dot-call on an XLua instance method**: `csp.Lookup(id)` tried to convert `id` to the plugin object and threw; the `pcall` in `factsFor` swallowed it, so facts were ALWAYS nil — 0.1.2's fix moved the method onto the instance but never fixed the call syntax. Instance methods are now invoked as `csp[fnName](csp, id)` with a dot-call fallback (both pcalled, first non-error result wins). Colon-call is the proven idiom (`ac:Login(...)`).
   - **The `json` string shadowed the `json` module**: `local ok, json = pcall(...)` then `pcall(json.decode, json)` called `.decode` on a string — impossible by construction. Now `local okJson, jsonlib = pcall(require, 'json')` and `pcall(jsonlib.decode, raw)`.
   - **Defense in depth**: the plugin now exposes primitive instance methods `GetLevel(uint) → int` (0 = unknown) and `GetAllegiance(uint) → string` ("" when unknown), tried FIRST so the Lua path doesn't depend on JSON or the json module at all. JSON `Lookup` remains the fallback. `tostring(char.Level)` → `string.format("%d", ...)` (float gotcha). New per-row debug line `row <Name> id=0x.. level=<L> allegiance='..'` makes the next log read conclusive.
2. **Population box geometry**: extracted the box art (`dat://0x06004D64` → 193×110 BGRA via the datprobe workflow) and mapped it: opaque bevel rows 25–30/77–82, transparent interior 31–76. The old `padding-top: 30px` + 24px/20px line boxes left the name's cap top ≈36px — touching the bevel ("clipping the top edge"). The text lines are now **absolutely positioned inside the interior** (`.world-name` top: 35px, `.world-population` top: 55px, full-width + text-align center): name caps ≈39–51, population ≈58–71 → symmetric ~8px clearance from both bevels, no reliance on padding behavior.
3. **Hardening**: `HookSoundIntroAndAudio`'s reflection fallback no longer calls `GetValue(null)` on a null `ACPlugin.Instance` (that was the 0.1.3-era `TargetException` log line; it would have silently disabled intro/mute on cold starts where AC initializes after us) — it now warns and returns.

Regression coverage: `ScreenScriptTests` (colon-call + no shadowed json + primitive accessors; absolute text lines inside the art interior), `BackendFallbackWaitsForAcPluginInstance`.

**Deployment + release status (2026-08-30, verified):** 11/11 tests; deployed; DLL probed for `GetLevel`/`GetAllegiance`/`0.1.5`; deployed RML probed for the new geometry + call syntax. Remaining gate: a client restart.

## 0.1.4 — intro skip, sound mute, monarch fallback, settings (2026-08-30)

Wired the three remaining scaffolding stubs and added real settings persistence:

1. **Intro skip** (was TODO: only logged `UIFlow.m_instance` availability). Implemented via the **typed** `IClientBackend` surface after discovering it in the decompile — no reflection needed for the mode switch, and no pointer math (the old `ReadCurMode`/`ToPointer` memory-scanning helpers were removed; `backend.GameScreen` getter reads `UIFlow._curMode` natively and returns 0 until the flow exists). Two paths fire the same idempotent `SkipIntroNow`: the bootstrapper's `OnScreenChanged` hook (fires on the game thread after every mode switch) and a 500ms watchdog timer that also re-applies mute volumes and catches a missed event. Setting `GameScreen = 268435466` queues `CharacterManagementUI` natively; because the setter no-ops (with a core log line) while `UIFlow.m_instance` is null, polling never spams or crashes during early startup.
2. **Sound mute** (was TODO: `ShouldMuteSound` existed but nothing called it, and it used raw pointer scans). The decompile showed `AudioPlaybackEngine` has no volume of its own; the mute surface is each engine's NAudio `WaveOutEvent.Volume`. The watchdog mutes (0.0) whenever the screen is not `GamePlayUI` and restores (1.0) otherwise, walking the lazily-created `_audioEngines` dictionary (snapshot before iterating — the game thread adds engines concurrently). The watchdog pattern replaces a PlaySound IL hook: engines appear after our mute pass, but the next tick (≤500ms) catches them; a `<50ms` blip of a new-rate sound is the theoretical worst case. Dispose restores volumes so unloading mid-mute can't leave the client silent. `ShouldMuteSound`/`ReadCurMode`/`ToPointer`/`_playSoundMethod` scaffolding was deleted.
3. **Allegiance fallback** (was TODO): capture now also reads `PropertyString.MonarchsName = 11` (verified in Chorizite.Common 1.0.2 via a scratch console probe of the NuGet DLL — the `Property*` enums live in Chorizite.Common, not ACProtocol) and uses it when AllegianceName (47) is empty, logging `using monarch name '...' as allegiance`. The deeper option (InstanceValues[Monarch=26] → World.Get(monarchId).Name) remains a possible upgrade.
4. **Settings persistence**: `SkipIntro`/`MuteSelectSounds` claimed to be persisted since 0.1.1 but nothing wrote them. Now implemented via the loader's `ISerializeSettings<T>` contract (verified in the Chorizite.Core decompile: settings.json is read at construction — before `Initialize()` — and written at unload): `CspSettings` + source-gen `CspSettingsContext`, explicit interface impl on the plugin. To toggle: edit `C:\Games\Chorizite\data\CharacterSelect\settings.json` and restart the client (no in-game UI yet).

Regression coverage: four new `FeatureWiringTests` assert the typed OnScreenChanged/GameScreen wiring, the exact UIMode constants, the `_audioEngines`/`outputDevice`/`Volume` mute path, the monarch fallback (key 11), and the settings contract.

**Deployment + release status (2026-08-30, verified):** deployed to `C:\Games\Chorizite\plugins\CharacterSelect` via `scripts/deploy.sh` (8/8 tests, 0 warnings); the deployed DLL byte-verified to contain the 0.1.4 changes (UTF-16LE probe for `queued CharacterManagementUI`, `subscribed to UIBackend.OnScreenChanged`, `_audioEngines`, `outputDevice`, `Volume`; UTF-8 metadata probe for `CspSettings`, `ISerializeSettings`). Remaining gate: a client restart.

## 0.1.3 — capture subscription fix (2026-08-30)

The 0.1.2 user round produced two findings:

1. **The interface the user opened in-game was Chorizite's Plugin Manager UI, and it re-shows stuck after logging out.** The log shows `Showing document PluginManagerUI` right after `Juggernaut: Logged off.`, landing on top of the freshly shown CharSelect screen, and its close control does nothing from there. That is core/PluginManagerUI behavior (same family as the unclickable in-game bar icons, known issue below), aggravated by PluginManagerUI's own bugs: it 404s on `plugin-index/plugins/Juggernaut.json` (Juggernaut is unpublished) and then throws `attempt to compare number with nil` at `manager.lua:203` plus a `MyObservable.ReadObservable` null-key error while rendering details. Workaround: close the plugin manager BEFORE logging out, or restart the client (restart clears it). Nothing to change in this plugin.

2. **Level capture never fired — and could never have.** Every startup logged `failed to subscribe to player description events System.ArgumentException: Cannot bind to the target method because its signature is not compatible with that of the delegate type` at `SubscribeCapture()`. `Delegate.CreateDelegate` requires exact parameter types, but the bridge was `(object sender, EventArgs e)` while the event is `EventHandler<Login_PlayerDescription>` — different args types, so binding was impossible by construction. `characters.json` was never created, so every row would have shown `Level ?` even with an unobstructed view of the screen.

   **Fix:** the bridge is now a generic instance method closed over the args type taken from the delegate itself at runtime:

   ```csharp
   var argsType = handlerType.GetMethod("Invoke")!.GetParameters()[1].ParameterType;
   var openBridge = GetType().GetMethod(nameof(OnPlayerDescriptionBridge), BindingFlags.NonPublic | BindingFlags.Instance)!;
   _playerDescriptionHandler = Delegate.CreateDelegate(handlerType, this, openBridge.MakeGenericMethod(argsType));
   ```

   Exact-signature match is guaranteed by construction, and binding against the runtime's own `Type` avoids any compile-time/load-context type-identity risk. `this` as the bound target also satisfies the WeakEvent keep-alive (Target stays strongly reachable via the plugin instance). The success log line prints the full handler type.

   Hardening in the same pass: `BaseQualities`/`IntProperties`/`StringProperties` are read through a field-or-property helper; property keys compare via `Convert.ToUInt32` (enum backing could be uint); recording is skipped with a warning when `Game.Character` is not yet known; and the capture log line's `{Id:X8}` slot receives the character id (the template had four placeholders but three args, which shifted level/allegiance into the wrong slots).

   Regression coverage: `UiStructureTests.CaptureBridgeBindsTheExactDelegateSignature`.

   **Status:** `2c728a0` pushed; tags `v0.1.2` + `v0.1.3` live with green CI, `CharacterSelect-v0.1.3.zip` published; deployed DLL byte-verified (UTF-16LE probe for `player description received but no current character known`). The 0.1.3 capture itself is still awaiting a client-restart validation — the 0.1.4 round tests it together with the new features.

## Known issues / TODO (0.1.5)

1. **Population counts showed `0` on Unfamiliar Shores both times** — verify `CurrentConnectionCount` updates (it comes from OnWorldInfo; may be server-dependent). Debug line `world info: ...` logs the values when received.
2. **WaveOutEvent.Volume semantics**: `waveOutSetVolume` applies per waveOut device id, and `WaveOutEvent` opens the default device — muting zeroes the client's WinMM wave device, which is exactly "mute the client" (all DAT playback goes through these engines) but is restored to 1.0 whenever gameplay is entered and on Dispose. If a user ever reports other audio being affected, that's why.
3. **Allegiance fallback upgrade path**: MonarchsName may be missing too on some servers; the full fallback is `InstanceValues[Monarch=26]` → `World.Get(monarchId).Name` while in-world. (Observed so far: allegiance '' for a fresh level-1 character — correct, no allegiance.)
4. **No in-game settings UI**: toggling requires editing `settings.json` + client restart. Chorizite's plugin framework may support a settings view later.
5. **User report (won't fix here)**: Chorizite bar plugin icons are unclickable in-game — that's core/launcher behavior, unrelated to this plugin.

## Debug instrumentation

- Plugin: every step logged under `[CharacterSelect]` in `C:/Games/Chorizite/data/logs/log.txt` (RegisterScreen result, capture subscription + resolved handler type, `subscribed to UIBackend.OnScreenChanged (backend=...)`, `queued CharacterManagementUI (...) ; intro skipped via {initialize|screen change|watchdog}`, `muted/restored volume on N audio engine(s) (screen=...)`, `settings loaded: skipIntro=..., muteSounds=...`, `captured {name} ... level {L} allegiance '{A}'`).
- Screen: `logDebug()` prints character count, GameScreen enum resolution, SetScreen fallback firing, world info updates.
- After any confusing test: FIRST verify the deployed DLL contains the latest change (probe the DLL for a new string, UTF-16LE for IL literals / UTF-8 for metadata type names — note interpolated strings split at the `{...}` holes, so probe a segment between holes) and that the launcher was restarted; the launcher logs the loaded version only at startup.
- Reading user screenshots without model vision (goose/Orca harness may omit image blocks): Windows built-in OCR via PowerShell WinRT — `Add-Type -AssemblyName System.Runtime.WindowsRuntime`, load `BitmapDecoder`/`OcrEngine` WinRT types, await `RecognizeAsync`. Working script: `C:\Users\jeremy\AppData\Local\Temp\goose_ocr.ps1` (Temp is ephemeral — recreate from that pattern if gone). Limit: 12–13px UI text is below OCR resolution; crop-zoom or rely on log lines instead.
- Probing NuGet enum/type values: scratch console app at `A:\tmp\enumprobe` (`dotnet run`) — loads the package DLL via `Assembly.LoadFrom` and prints fields. (PowerShell reflection fails there: netstandard deps unresolvable.)

## Build, test, deploy

```bash
cd A:/ai/projects/chorizite-mods/chorizite-character-select
"C:/Program Files/Git/bin/bash.exe" scripts/deploy.sh   # tests + build + copy to C:/Games/Chorizite (plain `bash` = WSL, fails on `pwd -W`)
CHORIZITE_HOME='D:/Games/Chorizite' "C:/Program Files/Git/bin/bash.exe" scripts/deploy.sh
```

8 structural tests assert on the RML/plugin source (two-line population, row layout, capture+property IDs, exact-signature capture bridge, typed intro-skip wiring + UIMode constants, audio-engine mute path, monarch fallback, settings contract, XLua colon-call script path, box-art text geometry).

## Test checklist for next user round (0.1.5)

1. Restart the client (0.1.5 deployed + verified). Startup log should show `CharacterSelect 0.1.5 initialized` plus the 0.1.4 hook lines (`subscribed to OnLogin_PlayerDescription`, `subscribed to UIBackend.OnScreenChanged (backend=ACChoriziteBackend)`), NO ArgumentException/TargetException.
2. **Level display**: the char select screen should now show `1` (gold, right) for Breeze instead of `Level ?` — the store already contains `Breeze level 1` from the 0.1.4 round, and the Lua path is fixed (colon calls + primitive `GetLevel`). Log shows `row Breeze id=500002FA level=1 allegiance=''` when the screen loads. If it STILL shows `Level ?`, the `row ...` line tells us whether facts resolved (level=1 → RML/CSS issue) or not (XLua call issue).
3. **Population box**: server name + `Population: X / 128` should sit centered inside the box's beveled interior with clear space above/below — no clipping at the top edge.
4. Intro skip + mute as in the 0.1.4 round: no intro videos, no select/login sounds, sounds return in-game.
5. Population count remains server-dependent (known issue 1). Do not open the Plugin Manager UI in-world right before logging out (0.1.3 finding 1).
