#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -W)"
chorizite_home="${CHORIZITE_HOME:-C:/Games/Chorizite}"
output="$repo_root/src/CharacterSelect/bin/net8.0"
destination="$chorizite_home/plugins/CharacterSelect"

dotnet test "$repo_root/tests/CharacterSelect.Tests/CharacterSelect.Tests.csproj"
dotnet build "$repo_root/src/CharacterSelect/CharacterSelect.csproj" --no-restore
mkdir -p "$destination"
cp "$output/CharacterSelect.dll" "$output/CharacterSelect.pdb" "$output/CharacterSelect.deps.json" "$output/CharacterSelect.runtimeconfig.json" "$output/manifest.json" "$destination/"
rm -rf "$destination/assets"
cp -R "$output/assets" "$destination/"
printf 'Deployed Character Select to %s\n' "$destination"
