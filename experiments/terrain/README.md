# Terrain Experiment

Standalone Godot project for prototyping the CitySim terrain system
(heightmap, chunked mesh, sculpting tools, texturing, placement queries).
Once stable, it gets merged into the main game.

## Run

Open this folder in Godot 4.7 (.NET) and press F5, or:

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
- `shaders/terrain.gdshader`: height/slope colouring plus a scale grid
