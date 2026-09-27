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
- `src/Camera/CityCamera.cs`: city-builder orbit camera
- `src/Terrain/TerrainLayers.cs`, `SplatMap.cs`: ground layer list and engine-agnostic painted layers (sparse Terrain3D-style control values)
- `src/Terrain/Sculpt/PaintOps.cs`: paint/erase brush ops on the splat map
- `src/Debug/TextureBaker.cs`: packs the layer textures into the strips Godot imports as texture arrays
- `shaders/terrain.gdshader`: Terrain3D shader override: stylized texture splatting (automatic + painted layers, height
  blend, triplanar rock), soft lighting, grid, contour, brush and edge-fog overlays
