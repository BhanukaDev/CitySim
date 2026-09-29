# Terrain Experiment

Standalone Godot project for prototyping the CitySim terrain system: the in-app **Map Editor** (sculpt, paint, channel,
generator, erosion, water sources, themes) and the test bed for the terrain.
The terrain itself (heights, paint, themes, water, camera, settings) lives in the shared **terrain package**,
`packages/citysim_terrain/` (symlinked here as `addons/citysim_terrain`). Read `packages/citysim_terrain/README.md` for
its API. This project is the app around it.

## Run

First fetch and bake the ground textures (CC0, from ambientCG; about 150 MB, not in git):

```sh
tools/fetch_textures.sh
```

Then open this folder in Godot 4.7 (.NET) and press F5, or:

```sh
/Applications/Godot_mono.app/Contents/MacOS/Godot --path .
```

Controls: WASD move · Q/E rotate · R/F tilt · Z/X or mouse wheel zoom.

Automated screenshot: `Godot --path . -- --screenshot=out.png [--screenshot-frames=60]`. After a fresh checkout, build
the native libraries once: `addons/citysim_terrain/native/erosion/build.sh; addons/citysim_terrain/native/water/build.sh`.

## Layout

- `addons/citysim_terrain` → `packages/citysim_terrain/`: the terrain package (see its README for its own layout)
- `addons/terrain_3d` → `packages/terrain_3d/`: the Terrain3D addon (MIT) the package draws with
- `src/App/MapSession.cs`: app mode (Map Editor / Game), the map the next scene opens, the current file. Hooks the package
  up through `TerrainHost` in a module initializer.
- `src/Tools/`: `TerrainToolController` (sculpt/paint/channel strokes on the package's ops, fixed 60 Hz tick, undo via
  `Terrain.History`), `WaterSourceTool`, `BrushLibrary` (brush masks in `assets/brushes/`)
- `src/UI/`: menus and panels built in code (bottom bar, tool/config panels, generator, erosion, water, theme panels, Esc
  menu with the package's Settings panel)
- `src/Debug/`: `DebugOverlay` (HUD, the `--demo-*` checks; adds the package's `ScreenshotCapture`), `TextureBaker`
  (brush masks), `ScaleDemo`, `WaterDemo`
- `src/Lookdev/`, `scenes/WaterLookdev.tscn`, `materials/templates/`: water look development
- `tools/fetch_textures.sh`, `fetch_particles.sh`, `fetch_brushes.sh`: download CC0 sources and bake them (textures and
  mist land in the package)
