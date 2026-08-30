# chorizite-character-select

Enhanced character select screen for the [Chorizite](https://github.com/Chorizite/Chorizite) AC client: the world/population box renders on two lines, and each character row shows the character's last-known level (large, right) and allegiance (underneath, `<allegiance>`).

Repository: `chorizite-character-select` · Product/plugin id: `CharacterSelect` ("Character Select Plus")

## Features

- **Population box**: server name on line 1, `Population: X / 128` on line 2, inside the existing decorated box.
- **Per-character level**: last-known level shown to the right of the name in large gold numbers. Learned at login (client receives level only after entering world), persisted to `data/CharacterSelect/characters.json`, shown thereafter on the character select screen. Unknown characters show `Level ?`.
- **Allegiance**: last-known allegiance name shown beneath the character name in `<angle brackets>`. If the server doesn't send an allegiance name, the monarch's name is used as a fallback.
- **Intro skip**: the intro videos are skipped straight to the character select screen (watches `UIFlow` screen changes + a cheap watchdog).
- **Sound mute**: character-select / intro / login sounds are muted outside of gameplay by zeroing the bootstrapper's audio engine volumes.
- **Settings**: intro-skip and mute are persisted to `data/CharacterSelect/settings.json` (edit + restart to toggle; no in-game settings UI yet).

## Install

Deploy with `./scripts/deploy.sh` (copies into `C:/Games/Chorizite/plugins/CharacterSelect`), or grab the latest zip from [Releases](https://github.com/Raajik/chorizite-character-select/releases) and extract it into `plugins/CharacterSelect`. Requires the AC plugin (Client environment).

## Status

0.2.0 — pure-CSS reskin + allegiance capture from the dedicated allegiance S2C message; releases published for v0.1.2–v0.2.0.
