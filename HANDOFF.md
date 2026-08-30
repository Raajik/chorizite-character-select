# Character Select Plus — Handoff

## Project boundary

Standalone Chorizite **Client-environment** plugin. Replaces the AC plugin's character select screen with an enhanced one (two-line population box, last-known level, allegiance). NOT a launcher plugin; shares nothing with Server Browser.

- Workspace: `A:\ai\projects\chorizite-mods\chorizite-character-select`
- Product: `CharacterSelect` (manifest id `CharacterSelect`, name "Character Select Plus")
- Installed to: `C:\Games\Chorizite\plugins\CharacterSelect`
- Runtime data: `C:\Games\Chorizite\data\CharacterSelect\characters.json`
- Depends on: AC plugin (`plugins\AC` 0.0.5), Lua 0.0.13, RmlUi 0.0.11, Chorizite 0.0.15 stack.
- Current version: **0.1.2** · GitHub: https://github.com/Raajik/chorizite-character-select (CI on push/PR; tagging `v<manifest version>` publishes a release zip)

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

## Known issues / TODO (0.1.2)

1. **Intro skip not wired**: `TrySkipIntro` only logs `UIFlow.m_instance` availability. To implement: call the backend `GameScreen` setter (ACChoriziteBackend line ~72 → `UIFlow.m_instance->QueueUIMode`) with 268435466 once UIFlow exists — e.g. subscribe `ClientBackend.UIBackend.OnScreenChanged` and force the mode when it lands on IntroUI (268435457). Needs the OnScreenChanged hook (AC plugin's ClientBackend_UIBackend pattern).
2. **Sound mute not wired**: `ShouldMuteSound(uint)` exists (mutes when curMode ≠ GamePlayUI) but nothing calls it — muting requires an IL/harmony-style hook on `ACChoriziteBackend.PlaySound` or replacing `_audioEngines`. Alternative cheap approach: reflect the private `_audioEngines` dictionary and set each engine's volume to 0 while not in gameplay. Find engine volume API in NAudio (`AudioPlaybackEngine` is from the NAudio.Wrapper in the bootstrapper — check its class for a Volume property).
3. **Allegiance fallback**: PropertyString 47 may be empty for the player's own description on some servers. Fallback plan: read `InstanceValues[Monarch=26]` → `World.Get(monarchId).Name` while in-world, store monarch name; or use `PropertyString.MonarchsName = 11`.
4. **User report (won't fix here)**: Chorizite bar plugin icons are unclickable in-game — that's core/launcher behavior, unrelated to this plugin.
5. Population counts showed `0` on Unfamiliar Shores both times — verify `CurrentConnectionCount` updates (it comes from OnWorldInfo; may be server-dependent). Debug line `world info: ...` logs the values when received.

## Debug instrumentation

- Plugin: every step logged under `[CharacterSelect]` in `C:/Games/Chorizite/data/logs/log.txt` (RegisterScreen result, subscription status, `captured {name} ... level {L} allegiance '{A}'` on login, hook results).
- Screen: `logDebug()` prints character count, GameScreen enum resolution, SetScreen fallback firing, world info updates.
- After any confusing test: FIRST verify the deployed DLL contains the latest change (probe the DLL for a new string, UTF-16LE) and that the launcher was restarted; the launcher logs the loaded version only at startup.

## Build, test, deploy

```bash
cd A:/ai/projects/chorizite-mods/chorizite-character-select
./scripts/deploy.sh                     # tests + build + copy to C:/Games/Chorizite
CHORIZITE_HOME='D:/Games/Chorizite' ./scripts/deploy.sh
```

3 structural tests assert on the RML/plugin source (two-line population, row layout, capture+property IDs).

## Test checklist for next user round (0.1.2)

1. Restart client → Create Character button should now enter character creation (watch for `SetScreen(CharCreate) failed ... retrying` then success via raw value).
2. Log into a character → log should show `CharacterSelect captured Breeze ...: level 1`; log out → row should show `1` instead of `Level ?`.
3. Allegiance row shows `<name>` if the server sends PropertyString 47 (most EMU servers do for logged-in chars).
4. Population line: check format is clean integers and spacing inside the box looks right.
