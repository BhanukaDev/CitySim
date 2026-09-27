# Terrain Experiment

Standalone Godot project for prototyping the CitySim terrain system
(heightmap, Terrain3D rendering, sculpting tools, texturing, placement queries).
Once stable, it gets merged into the main game.

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

Automated screenshot: `Godot --path . -- --screenshot=out.png [--screenshot-frames=60]`.

## Layout

- `src/Terrain/HeightMap.cs`: engine-agnostic height grid, with sampling, normal and slope queries
- `src/Terrain/Generation/`: engine-agnostic generator (noise with flat buildable lowlands, placed heightmap images, island/coast/archipelago shapes, presets)
- `src/Terrain/Terrain.cs`: owns the map, world-space queries, edit hooks (pushed to the renderer once per frame)
- `src/Terrain/Terrain3DBridge.cs`: the render copy in the Terrain3D addon (`addons/terrain_3d/`, MIT); the only file
  that calls Terrain3D
- `src/Terrain/TerrainSkirt.cs`, `shaders/terrain_skirt.gdshader`: fog ring hiding the map edge
- `native/erosion/`: C++ library (plain C ABI, no Godot) for droplet + thermal erosion, draining hollows and finding
  lakes. The macOS build in `bin/` is committed; rebuild with `native/erosion/build.sh` after changing the C++
- `src/Terrain/Erosion/`: engine-agnostic C# side (settings, `ErosionSim`, `Lakes`/`LakeMap`, function-pointer calls
  into the library); `src/Terrain/LakeWater.cs` + `shaders/lake_water.gdshader` draw the lakes; `src/UI/ErosionPanel.cs`
- `src/Camera/CityCamera.cs`: city-builder orbit camera
- `src/Terrain/SplatMap.cs`: engine-agnostic painted materials (sparse Terrain3D-style control values, plus the map's
  theme id and a palette of material ids, so paint survives theme changes)
- `src/Terrain/Sculpt/PaintOps.cs`: paint/erase brush ops on the splat map
- **Terrain themes** (M3.5): a theme is made in Godot and bundles a shader, its materials and erosion slots.
  `terrain_sdk/` holds the shader side every theme includes (Terrain3D vertex stage, painting, erosion slots, overlays,
  edge fog; see `terrain_sdk/README.md`). `themes/<id>/` holds each theme (`theme.tres`, `terrain.gdshader`, the baked
  `materials.gdshaderinc` and `baked/`). `src/Terrain/Themes/`: `TerrainTheme`, `TerrainMaterial`, `ErosionSlot`,
  `ThemeLibrary` (finds themes), `ThemeBaker` (texture arrays, previews, include). `addons/citysim_themes/`: editor
  plugin with Bake/Validate buttons. `src/UI/ThemePanel.cs`: pick the map's theme in the Map Editor
- `src/Debug/TextureBaker.cs`: bakes the brush masks
