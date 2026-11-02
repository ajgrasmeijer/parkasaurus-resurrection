#!/usr/bin/env bash
# Builds dist/ParkasaurusResurrection-<version>.zip: BepInEx + the mod, all inside a "Parkasaurus" folder,
# so the zip can be extracted straight over the game's install folder. No config file included.
set -euo pipefail
cd "$(dirname "$0")/.."
GAME=${GAME:-~/.local/share/Steam/steamapps/common/Parkasaurus}
DLL=ParkasaurusResurrection/bin/Release/net472/ParkasaurusResurrection.dll
V=$(grep -oP 'BepInPlugin\("[^"]+", "[^"]+", "\K[0-9.]+' ParkasaurusResurrection/Plugin.cs)
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
ROOT="$STAGE/Parkasaurus"
mkdir -p "$ROOT/BepInEx/core" "$ROOT/BepInEx/plugins"
cp "$GAME"/BepInEx/core/* "$ROOT/BepInEx/core/"
cp "$DLL" "$ROOT/BepInEx/plugins/"
cp "$GAME/winhttp.dll" "$GAME/doorstop_config.ini" "$GAME/.doorstop_version" "$ROOT/"
cat > "$ROOT/ParkasaurusResurrection-INSTALL.txt" <<TXT
Parkasaurus Resurrection $V for Parkasaurus (BepInEx 5.4.23.5)

1. Extract this zip into the folder that CONTAINS your Parkasaurus game folder
   (Steam: right-click Parkasaurus > Manage > Browse local files, then go one folder up,
   usually ...\steamapps\common). The "Parkasaurus" folder in the zip merges with the game's;
   winhttp.dll and doorstop_config.ini end up next to Parkasaurus.exe.
2. Linux (Proton) only: in Steam, right-click Parkasaurus > Properties > General >
   Launch options, and enter:
       WINEDLLOVERRIDES="winhttp=n,b" %command%
   Windows needs nothing extra.
3. Start the game. Settings are in Options > Resurrection, and in
   BepInEx/config/nl.alber.parkasaurusresurrection.cfg (created on the first start).

Updating from Sea Monster Fix (0.27 or older): delete BepInEx/plugins/SeaMonsterFix.dll.
Your settings are taken over from nl.alber.seamonsterfix.cfg on the first start.

To remove the mod: delete winhttp.dll (and the launch option on Linux).
Saves made with the mod still load in the game without it.
TXT
mkdir -p dist
OUT="$PWD/dist/ParkasaurusResurrection-$V.zip"
rm -f "$OUT"
(cd "$STAGE" && zip -qr "$OUT" Parkasaurus)
echo "$OUT"
