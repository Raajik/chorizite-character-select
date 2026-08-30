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
