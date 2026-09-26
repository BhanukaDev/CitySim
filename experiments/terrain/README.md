# Terrain Experiment

Standalone Godot project for prototyping the CitySim terrain system
(heightmap, chunked mesh, sculpting tools, texturing, placement queries).
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
- `src/Terrain/TerrainGenerator.cs`: noise-based generation with flat, buildable lowlands
- `src/Terrain/TerrainChunk.cs`: one mesh tile built from the heightmap
- `src/Terrain/Terrain.cs`: owns the map and chunks, world-space queries, dirty-chunk rebuild hooks
- `src/Camera/CityCamera.cs`: city-builder orbit camera
- `src/Terrain/TerrainLayers.cs`, `SplatMap.cs`: ground layer list and engine-agnostic painted layer weights
- `src/Terrain/Sculpt/PaintOps.cs`: paint/erase brush ops on the splat map
- `src/Debug/TextureBaker.cs`: packs the layer textures into the strips Godot imports as texture arrays
- `shaders/terrain.gdshader`: stylized texture splatting (automatic + painted layers, height blend,
  triplanar rock), soft lighting, grid and contour overlays
