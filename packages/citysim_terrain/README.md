# CitySim terrain package

The terrain and water for CitySim as a Godot addon. It covers the heightmap, painted materials, themes, the generator,
erosion, sculpt ops, map files, the water simulation and its rendering, the city camera, and the terrain's settings.
Every experiment and the main game use this one copy. The in-app Map Editor (tools, panels, menus) is the terrain
experiment (`experiments/terrain`), built on top of it.

Godot 4.7 .NET, C# (.NET 9). Rendering through Terrain3D (`packages/terrain_3d`, MIT).

## Add it to a project

1. Symlink both addons into the project (paths from `experiments/<name>/`):
   ```sh
   mkdir -p addons
   ln -s ../../../packages/citysim_terrain addons/citysim_terrain
   ln -s ../../../packages/terrain_3d addons/terrain_3d
   ```
   On Windows, clone with `git config core.symlinks true` (and Developer Mode on) so these come out as links.
2. In the `.csproj`: `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (the native libraries are called through function
   pointers) and `<Optimize>true</Optimize>` (the generator is ~6× faster). MSBuild compiles the package's C# through the
   symlink, so nothing else is needed.
3. In `project.godot`: enable the plugin (`[editor_plugins] enabled=PackedStringArray("res://addons/citysim_terrain/plugin.cfg")`).
4. Once per checkout: `addons/citysim_terrain/native/erosion/build.sh` and `…/native/water/build.sh` (`bin/` is gitignored).
   Ground textures: `experiments/terrain/tools/fetch_textures.sh` downloads and bakes them into this package, once for
   every project.
5. In a scene: a `Terrain` node (`src/Godot/Terrain.cs`), a `CityCamera` with `Terrain` set, and a sun and environment.
   `experiments/splines/scenes/Main.tscn` is a minimal example. Add a `ScreenshotCapture` node to get `--screenshot`,
   `--cam` and `--setting`.
6. Choosing the map at start-up: set `TerrainHost.TakeStartupRequest` once, e.g. from a module initializer. It either
   calls `TerrainCommandLine.Use()` (`--load=`, `--flat`, `--preset=`, `--seed=`, `--size=`) or returns your own
   `MapRequest`. Set `TerrainHost.IsMapEditor` if the project is an editor (it shows the map edge as a line).

A new Terrain with no request generates a map from its exports. The camera adds its own input actions (`cam_*`: WASD,
Q/E, R/F, Z/X) if the project doesn't define them.

## Public API

Everything below is the stable surface. Other public members exist for the terrain experiment's tools and may change.

**Coordinates.** Map data (heights, paint, water, sources, files) uses **map metres** from the map's (0, 0) corner, or
vertex indices (`x = metres / CellSize`). The world is centred on the origin: convert with `terrain.MapToWorld(x, z, y)`
and `terrain.WorldToMap(world)`. `VertexRect` is an inclusive vertex rectangle; `VertexRect.FromMapRect(...)` and
`VertexRect.Circle(...)` make one from metres.

**Queries** (on `Terrain`, world space, cheap enough to call per frame):
`GetHeight(x, z)`, `GetNormal(x, z)`, `GetSlopeDegrees(x, z)`,
`Raycast(origin, dir, out hit)` (heightmap ray march, no physics), `GetWaterDepth`, `IsUnderwater`, `GetWaterSurface`,
`GetWaterVelocity`, `GetWaterPollution`. For bulk or off-thread work, read `terrain.Map` (`HeightMap`: `SampleHeight`,
`SampleNormal`, indexer, `CellSize`, `Width`/`Depth`) directly. It's plain C#, with no Godot types.

**Maps.** `Open(MapRequest)`, `SetMap(heights, splat, water)`, `Generate(GenSettings)`; `MapFile.Save/LoadWithWater`
(`.csmap`), `HeightmapImage` (16-bit PNG/RAW). `Map`, `Splat`, `Water` (`WaterSim`), `Theme`, `SetTheme`, `ThemeLibrary`.

**Editing** (roads, canals, building pads, scripts), one undo history shared with the Map Editor's tools:
```csharp
using var edit = terrain.BeginEdit();          // BeginEdit(paint: true) to also change painted materials
edit.Touch(rect);                              // before writing: saves the undo copy
edit.Heights[x, z] = h;                        // write directly; edit.Original(x, z) = height before the edit
edit.Changed(rect);                            // optional: show it now (live preview while dragging)
// edit.Commit() (also on Dispose) = one undo step; edit.Cancel() = put everything back
terrain.Undo(); terrain.Redo();                // or through the tools; terrain.History is the stack
```
The renderer, water, lakes and ground masks follow every edit by themselves. Only one edit or tool stroke can be open
at a time (`BeginEdit` throws otherwise).

**Events.** `HeightsChanged(VertexRect)` and `PaintChanged(VertexRect)` fire once per frame with the union of that
frame's changes (edits, tool strokes, undo/redo, generator). Re-conform things placed on the ground here.
`MapReplaced` fires when a whole new map is set, and everything placed on the old one is gone.
Also `WaterChanged`, `LakesChanged`, `ThemeChanged`.

## Settings

Two resources on the `Terrain` node (inspector group **Settings**). When they're empty, the Terrain loads
`settings/graphics.tres` and `settings/tuning.tres`. Changes apply live, including from the inspector while the
editor shows a Terrain.

- **`TerrainGraphics`**: the player's graphics menu. `Preset` (Low/Medium/High/Custom: sets the others; a hand change
  makes it Custom), `WetShine`, `WaterfallFx` (Off/Low/High), `HorizonRing` (game edge: hills, or plain fog),
  `TerrainDetail` (Terrain3D mesh size 32/48/64; High is only reachable by hand). In the app the Terrain uses a copy
  with the player's choice from `user://settings/terrain_graphics.cfg` loaded on top (`Save()`/`Load()`). ROADMAP
  "Graphics settings" has the rules for adding one.
- **`TerrainTuning`**: developer knobs that used to be constants, with the tuned defaults: lake search delays, water
  grid cap (map reload), water LOD distances, waterfall thresholds and distances, horizon hills and fog floor, rain
  ramp/dry times, undo size. Point `Terrain.Tuning` at a project's own copy to tweak without touching the package.

`SettingsPanel` is a plain window with a Graphics tab (saved for the player on every change) and an optional Tuning tab
(live, with **Save as defaults**). Its rows are built from the resources' exported properties, so a new `[Export]` with a
range or enum shows up without UI code. The terrain experiment opens it from Esc → **Settings…**.
From the command line (`ScreenshotCapture`): `--setting=Graphics.WetShine=false`, `--setting=Tuning.HorizonMinRelief=600`.

## Layout

- `src/Core/`: **engine-agnostic** (no Godot types; ports to the main game's sim as is)
  - `HeightMap`, `SplatMap` (painted materials: Terrain3D-style control values + a palette of material ids), `VertexRect`,
    `MapFile`, `HeightmapImage`, `MapRequest`, `TerrainHost`
  - `Generation/` (noise with flat buildable lowlands, heightmap placement, island/coast shapes, presets; vendored
    FastNoiseLite), `Erosion/` (C# side of the erosion library, lakes), `Sculpt/` (`SculptOps`, `PaintOps`,
    `ChannelOps`, brushes, `UndoStack`), `Water/` (`WaterSim` worker thread + snapshot queries, sources, files, weather)
- `src/Godot/`: `Terrain` (owns everything, queries, edit API), `TerrainEdit`, `Terrain3DBridge` (the only file
  that calls Terrain3D), `TerrainHorizon`, water drawing (`WaterSurface`, `WaterFalls`, markers, preview, flow arrows),
  `Themes/` (`TerrainTheme`, `ThemeLibrary`, `ThemeBaker`), `Camera/CityCamera`, `Settings/`, `UI/SettingsPanel`,
  `Debug/` (`ScreenshotCapture`, `TerrainCommandLine`)
- `shaders/sdk/`: what every terrain theme's shader includes; **`shaders/sdk/README.md` explains how to make a theme**.
  `shaders/`: water, waterfalls, horizon
- `themes/default`, `themes/winter`: built-in themes (`baked/*.png` are generated: fetch_textures.sh or Bake in the inspector)
- `materials/`: the water and waterfall materials (the water look is authored here)
- `native/`: C++ erosion and water (`extern "C"`, loaded with `NativeLibrary`; not GDExtensions)
- `TerrainPlugin.cs`: the editor plugin (Bake/Validate buttons on a `TerrainTheme`)
