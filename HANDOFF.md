# Character Select Plus — Handoff

## Project boundary

Standalone Chorizite **Client-environment** plugin. Replaces the AC plugin's character select screen with an enhanced one (two-line population box, last-known level, allegiance). NOT a launcher plugin; shares nothing with Server Browser.

- Workspace: `A:\ai\projects\chorizite-mods\chorizite-character-select`
- Product: `CharacterSelect` (manifest id `CharacterSelect`, name "Character Select Plus")
- Installed to: `C:\Games\Chorizite\plugins\CharacterSelect`
- Runtime data: `C:\Games\Chorizite\data\CharacterSelect\characters.json`
- Depends on: AC plugin (`plugins\AC` 0.0.5), Lua 0.0.13, RmlUi 0.0.11, Chorizite 0.0.15 stack.
- Current version: **0.1.3** · GitHub: https://github.com/Raajik/chorizite-character-select (CI on push/PR; tagging `v<manifest version>` publishes a release zip)

## Validated facts (all from decompiling the installed stack)

- **Screen override works**: RmlUi keeps `_gameScreenRmls[name] → rmlPath` and re-registering a name overwrites it. Our `RegisterScreen("CharSelect", ours)` at Initialize wins over AC's own registration (AC registers CharSelect first at its Initialize; log line `Showing document CharSelect ...plugins\CharacterSelect\assets\screens\CharSelect.rml` confirmed ours loads).
- **Key C# property IDs** (Chorizite.Common 1.0.2, cross-checked against AC.dll's own decompiled accessors): `PropertyInt.Level = 25` (Creature.Level uses it), `PropertyInstanceId.Monarch = 26`, `PropertyString.AllegianceName = 47`, `PropertyString.Name = 1`.
- **Capture event**: `ACPlugin.Net.S2C.OnLogin_PlayerDescription` (an `EventHandler<Login_PlayerDescription>`; `BaseQualities.IntProperties/StringProperties` dictionaries). AC's own Character class subscribes the same way.
- **Sound choke point**: every DAT wave playback funnels through `ACChoriziteBackend.PlaySound(uint)` (Chorizite.NativeClientBootstrapper.dll; NAudio engines keyed by sample rate). One override = mute everything.
- **Intro videos** = UIFlow UIMode.IntroUI (268435457); char select = CharacterManagementUI (268435466); char create = CharGenMainUI (268435467). Native client switches modes via `UIFlow.QueueUIMode`.
- **XLua enum casting is unreliable**: `ac:SetScreen(GameScreen.CharCreate)` from RML Lua throws `invalid value for enum AC.Lib.Screens.GameScreen` (XLua ObjectCasters). **Pass the raw uint** (`268435467`) — pcall + fallback pattern in CharSelect.rml.
- **XLua numbers surface as floats**: C# `int` properties read from Lua arrive as `0.0`/`128.0`. Format with `string.format("%d", v)` before display.
- **Lua↔C# visibility (critical)**: `require('Plugins.<Id>')` returns the plugin *instance object* (`AssemblyPluginInstance.Instance` = the IPluginCore). **XLua only sees instance members on it — a separate C# static class is invisible.** 0.1.1's `Lookup` sat in a static `CharacterStoreApi` class, so `csp.Lookup(id)` silently returned nil and every row showed `Level ?`. Fixed in 0.1.2 by moving `Lookup(uint)`/`Record(...)` onto the `CharacterSelectPlugin` instance (public instance methods).
- **WeakEvent gotcha**: AC's events are `WeakEvent<T>` — delegates are held by WeakReference with a keep-alive table keyed on `handler.Target`. A delegate created via `CreateDelegate(type, this, method)` keeps `this` alive as Target, so our subscription survives. If handlers ever silently stop firing, suspect the target being collected.
- **RmlUi theme font**: theme.rcss declares `font-family: Tahoma` but only LatoLatin-Regular.ttf ships. Small text renders with a substitute and looks distorted at 12px with `font-effect: outline`. Population line: 13px, no outline, `line-height: 20px`.

## Architecture (this repo)

- `CharacterSelectPlugin` (IPluginCore): Initialize → RegisterScreen override + `SubscribeCapture()` (reflection: ACPlugin.Instance → Net → S2C → addEventHandler) + `HookSoundAndIntro()` (resolves ACChoriziteBackend + ClientBackend). Instance methods `Lookup(uint)` / `Record(uint,string,int,string)` are the Lua surface.
- `CharacterStore`: `data/CharacterSelect/characters.json`, `Dictionary<uint, CharacterInfo>` (Id, Name, Level, Allegiance, LastSeenUtc). Load/save both wrapped; corrupt store resets.
- `assets/screens/CharSelect.rml`: stock layout + two-line population box (`.world-name` / `.world-population`), per-row `.char-name` / `.char-allegiance` (`<name>`) / `.char-level` (20px gold, right-aligned; `.unknown` shows "Level ?"). Lua `factsFor(id)` calls `csp.Lookup(id)` → JSON → row data. `logDebug()` prints `[CharacterSelect] ...` to the log.
- Capture path: `OnLogin_PlayerDescription` → read IntProperties[25] + StringProperties[47] → `CurrentCharacter()` (reflection Game.Character.Id/Name) → `CharacterStore.Record`.

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

   Exact-signature match is guaranteed by construction, and binding against the runtime's own `Type` avoids any compile-time/load-context type-identity risk (the csproj does reference the `Chorizite.ACProtocol` NuGet package, but the runtime's delegate type is the only authority that matters). `this` as the bound target also satisfies the WeakEvent keep-alive (Target stays strongly reachable via the plugin instance). The success log line now prints the full handler type, so the next session's log will show the resolved args type explicitly.

   Hardening in the same pass: `BaseQualities`/`IntProperties`/`StringProperties` are read through a field-or-property helper (they were assumed to be fields); property keys compare via `Convert.ToUInt32` (enum backing could be uint); recording is skipped with a warning when `Game.Character` is not yet known; and the capture log line's `{Id:X8}` slot actually receives the character id now (the template had four placeholders but three args, which shifted level/allegiance into the wrong slots).

   Regression coverage: `UiStructureTests.CaptureBridgeBindsTheExactDelegateSignature`.

   **Deployment + release status (2026-08-30, verified):** `2c728a0` is pushed to GitHub; tags `v0.1.2` + `v0.1.3` are live with green CI, and `CharacterSelect-v0.1.3.zip` is published. The deployed DLL at `C:\Games\Chorizite\plugins\CharacterSelect` was byte-verified to contain this fix (UTF-16LE probe for the guard string `player description received but no current character known`), so the only remaining gate for checklist item 1 is a client restart.

## Known issues / TODO (0.1.3)

1. **Intro skip not wired**: `TrySkipIntro` only logs `UIFlow.m_instance` availability. To implement: call the backend `GameScreen` setter (ACChoriziteBackend line ~72 → `UIFlow.m_instance->QueueUIMode`) with 268435466 once UIFlow exists — e.g. subscribe `ClientBackend.UIBackend.OnScreenChanged` and force the mode when it lands on IntroUI (268435457). Needs the OnScreenChanged hook (AC plugin's ClientBackend_UIBackend pattern).
2. **Sound mute not wired**: `ShouldMuteSound(uint)` exists (mutes when curMode ≠ GamePlayUI) but nothing calls it — muting requires an IL/harmony-style hook on `ACChoriziteBackend.PlaySound` or replacing `_audioEngines`. Alternative cheap approach: reflect the private `_audioEngines` dictionary and set each engine's volume to 0 while not in gameplay. Find engine volume API in NAudio (`AudioPlaybackEngine` is from the NAudio.Wrapper in the bootstrapper — check its class for a Volume property).
3. **Allegiance fallback**: PropertyString 47 may be empty for the player's own description on some servers. Fallback plan: read `InstanceValues[Monarch=26]` → `World.Get(monarchId).Name` while in-world, store monarch name; or use `PropertyString.MonarchsName = 11`.
4. **User report (won't fix here)**: Chorizite bar plugin icons are unclickable in-game — that's core/launcher behavior, unrelated to this plugin.
5. Population counts showed `0` on Unfamiliar Shores both times — verify `CurrentConnectionCount` updates (it comes from OnWorldInfo; may be server-dependent). Debug line `world info: ...` logs the values when received.

## Debug instrumentation

- Plugin: every step logged under `[CharacterSelect]` in `C:/Games/Chorizite/data/logs/log.txt` (RegisterScreen result, subscription status, `captured {name} ... level {L} allegiance '{A}'` on login, hook results).
- Screen: `logDebug()` prints character count, GameScreen enum resolution, SetScreen fallback firing, world info updates.
- After any confusing test: FIRST verify the deployed DLL contains the latest change (probe the DLL for a new string, UTF-16LE) and that the launcher was restarted; the launcher logs the loaded version only at startup.
- Reading user screenshots without model vision (goose/Orca harness may omit image blocks): Windows built-in OCR via PowerShell WinRT — `Add-Type -AssemblyName System.Runtime.WindowsRuntime`, load `BitmapDecoder`/`OcrEngine` WinRT types, await `RecognizeAsync`. Working script: `C:\Users\jeremy\AppData\Local\Temp\goose_ocr.ps1` (Temp is ephemeral — recreate from that pattern if gone). Limit: 12–13px UI text is below OCR resolution; crop-zoom or rely on log lines instead.

## Build, test, deploy

```bash
cd A:/ai/projects/chorizite-mods/chorizite-character-select
./scripts/deploy.sh                     # tests + build + copy to C:/Games/Chorizite
CHORIZITE_HOME='D:/Games/Chorizite' ./scripts/deploy.sh
```

4 structural tests assert on the RML/plugin source (two-line population, row layout, capture+property IDs, exact-signature capture bridge).

## Test checklist for next user round (0.1.3)

1. Restart the client so the 0.1.3 DLL loads (0.1.2 cannot capture; verify the DLL was deployed). Startup log should show `CharacterSelect 0.1.3 initialized` AND a `subscribed to OnLogin_PlayerDescription` line whose `(System.EventHandler`1[...Login_PlayerDescription...])` suffix shows the resolved args type, with NO ArgumentException.
2. Log into a character → log should show `CharacterSelect captured <name> (0x...): level N, allegiance '...'`; `C:\Games\Chorizite\data\CharacterSelect\characters.json` should exist afterwards.
3. Log out → the character row should show the level number instead of `Level ?` (and the allegiance line when the server sends PropertyString 47).
4. Population line remains server-dependent (OnWorldInfo) — Unfamiliar Shores showed `0` both times.
5. Do not open the Plugin Manager UI in-world right before logging out — it re-shows stuck over the CharSelect screen (see 0.1.3 finding 1).
