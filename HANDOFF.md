# Character Select Plus — Handoff

## Project boundary

Standalone Chorizite **Client-environment** plugin. It replaces the AC plugin's character select screen with an enhanced one (population box, last-known level, allegiance). It is NOT a launcher plugin and shares nothing with Server Browser.

- Workspace: `A:\ai\projects\chorizite-character-select`
- Product: `CharacterSelect` (manifest id `CharacterSelect`, name "Character Select Plus")
- Installed to: `C:\Games\Chorizite\plugins\CharacterSelect`
- Runtime data: `C:\Games\Chorizite\data\CharacterSelect` (characters.json)
- Depends on the **AC plugin** (`plugins\AC`, v0.0.5) and Lua/RmlUi plugins.

## How it works (validated by decompilation)

- The stock AC plugin registers its screen at init: `RegisterScreen(GameScreen.CharSelect, "assets/screens/CharSelect.rml")` (ACPlugin.cs:106). RmlUi keeps `_gameScreenRmls[name] -> rmlPath`; **re-registering the same screen name overwrites the path** (RmlUiPlugin.RegisterScreen). Our plugin registers `CharSelect` → our own RML at initialize. **OPEN QUESTION (spike #1): registration order between plugins — if AC loads after us, its path overwrites ours. Debug logs will show both registration lines; check log order.**
- The AC plugin's `ACPlugin.Instance` / `Net` are `internal`; we reach them via reflection (`FindAcPluginType` scans loaded assemblies for assembly name `AC`, type `ACPlugin`). All reflection wrapped + logged.
- **Capture**: subscribe (reflection) to `AC.Net.S2C.OnLogin_PlayerDescription`. That event's `BaseQualities.IntProperties` holds `PropertyInt.Level = 25`; `StringProperties` holds `PropertyString.AllegianceName = 47`. Facts recorded per character id into `data/CharacterSelect/characters.json`.
- The char-select Lua reads facts via `require('Plugins.CharacterSelect').Lookup(id)` (returns JSON or nil).

## Property ID reference (Chorizite.Common 1.0.2, verified against AC.dll decompile)

- `PropertyInt.Level = 25` (Creature.Level uses it)
- `PropertyInstanceId.Monarch = 26` (WorldObject stores weenie MonarchId there)
- `PropertyString.AllegianceName = 47`, `Name = 1`

## Debug instrumentation (0.1.0)

- Plugin logs: RegisterScreen result, subscription success/failure, every captured `{name, level, allegiance}`.
- Screen Lua `logDebug()` prints `[CharacterSelect] ...` lines: character count, world info updates.
- Log file: `C:/Games/Chorizite/data/logs/log.txt` — grep `CharacterSelect`.

## 0.1.1 iteration (user test #1 feedback)

- **Screen override CONFIRMED working** (user screenshot shows our two-line population box on the real char select).
- **Create Character button broken**: `ac:SetScreen(GameScreen.CharCreate)` throws `XLua.LuaException: invalid value for enum AC.Lib.Screens.GameScreen` (ObjectCasters.cs:508). Fix: pcall the typed call, fall back to `ac:SetScreen(268435467)` (raw UIMode.CharGenMainUI value). Debug log prints the failure + retry.
- **Population showed `0.0 / 128.0`**: XLua surfaces the C# ints as floats; Lua concat prints them raw. Fixed with `string.format("%d")` via `fmtInt()` + a preformatted `state.Population` string.
- **Distorted population font**: stock theme uses `font-family: Tahoma` but RmlUi only ships LatoLatin-Regular.ttf — small sizes render with a substitute that looks wrong. Raised to 13px and removed the outline effect on the population line.
- **Sound muting**: all DAT wave playback funnels through `ACChoriziteBackend.PlaySound(uint)` (NAudio engines per sample rate) — a single choke point. `ShouldMuteSound` added; wiring it as a harmony/IL hook is still TODO (see below).
- **Intro skip**: native client plays intro videos via UIFlow UIMode.IntroUI (268435457) → CharacterManagementUI (268435466). `TrySkipIntro` logs UIFlow.m_instance availability; actually forcing the mode needs either a `QueueUIMode` call through the backend's `GameScreen` setter (reflection) once UIFlow exists, or hooking at boot. TODO.
- XLua enum-cast lesson: **C# enum values passed to `SetScreen` from Lua can fail to cast ("invalid value for enum") — pass the raw uint instead, or pcall + fallback.**

## Current known limitations (0.1.0 spike)

- Level/allegiance list is empty until each character has logged in at least once with this plugin installed.
- Capture runs only when ACPlugin.Instance already exists at our Initialize; if AC loads later, capture is disabled for the session (logged). May need a lazy hook later.
- `Lookup` returns JSON decoded in Lua via pcall(json.decode) — the Lua plugin provides `json`.

## Build, test, deploy

```bash
./scripts/deploy.sh          # tests + build + copy to C:/Games/Chorizite
CHORIZITE_HOME=... ./scripts/deploy.sh
```

## Suggested next work

1. Verify screen override wins over AC plugin's registration (user test #1).
2. Verify capture on login (log line `CharacterSelect captured ...`).
3. Wire allegiance via Monarch IID lookup if PropertyString 47 is empty for self.
4. Publish to GitHub, tag releases.
