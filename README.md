# Parkasaurus Resurrection

An unofficial fan mod for Parkasaurus (Washbear Studio, Steam) that fixes
bugs and adds quality-of-life features. It is a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin
and works on Windows and on Linux through Proton.

> Not affiliated with or endorsed by Washbear Studio. You need your own copy of Parkasaurus.
> This repository contains only the mod's own code: no game code or game assets.

## Features

Everything can be switched on or off in the game under **Options > Resurrection**, most of it while
playing.

**Swimming**
- Swimmers change depth smoothly, turn in an arc and find routes through the water around corners.
- Swim targets further away, so aquatic dinos actually swim instead of bobbing in place.

**Building**
- **Move Item** tool (Destruction tab): click anything with a place in the park to pick it up and
  move it. The tool stays active until right-click or Esc.
- **Quiet objects**: trees, rocks, decorations, lights and other scenery can't be clicked during
  normal play, so clicks reach the guests, staff and dinos behind them.
- Remove Item: drag a rectangle to mark everything in it.
- Diagonal fences and paths become staircases; Shift while painting terrain changes only one exhibit.
- Optional smoother terrain slopes (4 triangles per tile).

**Park Info and lists**
- Day history: browse and compare the last days in Park Info.
- Live income during the day.
- Staff list: an Occupation column (what each employee is doing). Staff and dino lists: a Hat column.
- Adjustable text size; fixes the Dinosaurs tab lighting up together with Staff.

**Interface**
- Scrollable inventory with extra slots, calmer mouse-wheel scrolling.
- Right-click a hat or egg item to put it back in the inventory; Shift-click the trash to empty it.
- Type a number into sliders and shop amounts.
- Hide dino hats and neck items (their effects keep working).
- New pet names for dinos and staff.

**Performance and stability**
- Lighter physics for visitors, staff and cars; physics steps that follow the game speed.
- Quest tutorial videos turned off (they crash the game under Proton).

## Installing

1. **Download** `ParkasaurusResurrection-<version>.zip` from the latest release on the
   [Releases](../../releases) page (under "Assets").
2. **Open the game folder**: in Steam, right-click Parkasaurus > **Manage > Browse local files**.
   A folder with `Parkasaurus.exe` opens.
3. **Go one folder up**, to the `common` folder that contains the `Parkasaurus` folder:
   - Windows: usually `C:\Program Files (x86)\Steam\steamapps\common`
   - Linux: usually `~/.local/share/Steam/steamapps/common`
4. **Extract the zip there.** Its `Parkasaurus` folder merges with the game's folder; allow merging
   and replacing files. Afterwards `winhttp.dll` and a `BepInEx` folder sit next to `Parkasaurus.exe`.
5. **Linux (Proton) only:** in Steam, right-click Parkasaurus > **Properties > General**, and under
   *Launch options* enter:
   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```
6. **Start the game.** Under **Options** there is a new tab **Resurrection**: the mod works.

**Updating:** repeat steps 1 to 4 with the new zip. Your settings stay
(`BepInEx/config/nl.alber.parkasaurusresurrection.cfg` is not in the zip).

**Removing:** delete `winhttp.dll` next to `Parkasaurus.exe` (and the launch option on Linux). Saves
made with the mod still load without it.

The zip also contains these instructions as `ParkasaurusResurrection-INSTALL.txt`.

## Building

Requirements: the .NET SDK (8 or newer) and Parkasaurus with BepInEx 5 installed (the build references
the game's and BepInEx's DLLs from the game folder; they are not part of this repository).

```sh
dotnet build ParkasaurusResurrection                       # Linux default Steam path
dotnet build ParkasaurusResurrection -p:GameDir="/path/to/Parkasaurus"
```

The build copies the plugin into the game's `BepInEx/plugins` folder.

The `tools` folder also has a few helper scripts used during development (save editing, exporting a
dinosaur model to Blender, dumping UI trees); they read your own game install.

## Credits

- **ajgrasmeijer**: idea, design, testing and playing far too many hours of Parkasaurus.
- **Claude** (Anthropic): co-author of the code, written together with ajgrasmeijer in Claude Code.

## License

The mod's code is released under the [MIT License](LICENSE).

Release zips include [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and
[Harmony](https://github.com/pardeike/Harmony) (MIT), which keep their own licenses.
Parkasaurus and its assets are the property of Washbear Studio.
