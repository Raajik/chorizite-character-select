# chorizite-character-select

Enhanced character select screen for the [Chorizite](https://github.com/Chorizite/Chorizite) AC client: the world/population box renders on two lines, and each character row shows the character's last-known level (large, right) and allegiance (underneath, `<allegiance>`).

Repository: `chorizite-character-select` · Product/plugin id: `CharacterSelect` ("Character Select Plus")

## Features

- **Population box**: server name on line 1, `Population: X / 128` on line 2, inside the existing decorated box.
- **Per-character level**: last-known level shown to the right of the name in large gold numbers. Learned at login (client receives level only after entering world), persisted to `data/CharacterSelect/characters.json`, shown thereafter on the character select screen. Unknown characters show `Level ?`.
- **Allegiance**: last-known allegiance name shown beneath the character name in `<angle brackets>`.

## Install

Deploy with `./scripts/deploy.sh` (copies into `C:/Games/Chorizite/plugins/CharacterSelect`), or grab the latest zip from [Releases](https://github.com/Raajik/chorizite-character-select/releases) and extract it into `plugins/CharacterSelect`. Requires the AC plugin (Client environment).

## Status

0.1.2 — screen override, login capture, per-character level + allegiance. CI on push/PR; releases via `v*` tags.
