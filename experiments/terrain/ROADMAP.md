# Terrain Experiment: Roadmap

Working document for the terrain system. **Keep it updated**: tick items off, add findings, and note
decisions when a milestone lands. A new session should read this file and `README.md` first.

## Goal

A standalone Godot 4.7 (.NET, C#) project that proves out the terrain system for CitySim, a
Cities: Skylines-style city builder. When it's stable, it's merged into the main game. The terrain has to:

- look good at city scale (2 km+ maps, viewed from 15 m to 1.5 km away)
- be sculptable in real time
- answer queries that later systems depend on: height, slope and normal at a point, whether an area is
  buildable, and flattening ground under roads and buildings

## Tech decisions (settled)

- **Godot 4.7.2 .NET** at `/Applications/Godot_mono.app`. **C# only** for now. C++ via GDExtension is
  deferred until profiling shows a hot path. GDExtension classes have no generated C# bindings, so every
  call from C# goes through `Call("method")` strings.
- **.NET 9**: `TargetFramework net9.0`, because no .NET 8 runtime is installed.
- **`HeightMap` stays engine-agnostic** (plain C#, `System.Numerics`). Godot-specific code lives in
  `Terrain`, `TerrainChunk` and `TerrainGenerator`.
- **Chunked mesh**: 64×64-cell tiles, and only dirty chunks are rebuilt. Normals come from the global
  heightmap, so chunk borders have no seams.
- Namespaces: `CitySim.TerrainSystem`, `CitySim.CameraSystem`, `CitySim.Debug`. Don't name a class the
  same as its namespace.

## Camera controls (user-specified)

| Keys | Action |
|---|---|
| W/A/S/D | move (relative to facing, speed scales with zoom) |
| Q/E | rotate yaw (direction was inverted once at the user's request; current mapping is correct) |
| R/F | tilt: R toward top-down, F toward horizon (also inverted once; current mapping is correct) |
| Z/X, mouse wheel | zoom |

## How to build / verify

```sh
cd experiments/terrain
dotnet build                                                   # compile check
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
$G --headless --path . --quit-after 120                        # runtime errors, generation timing
$G --path . -- --screenshot=/path/out.png --screenshot-frames=90   # real render → PNG, then view it
# extra flags: --demo-sculpt (scripted strokes + undo check), --cam=x,z,distance,pitch,yaw (close-ups)
```

The screenshot flag is handled in `src/Debug/DebugOverlay.cs`. Use it to check visual changes
before handing them to the user. Don't commit until the user has tried the change.

---

## Milestones

### ✅ M1: Project, terrain, camera (committed in `715b8b6`)
- Godot project, input map, scene (sky, sun with shadows, fog)
- `HeightMap`: bilinear height, normal and slope sampling
- `TerrainGenerator`: FastNoiseLite fBm + domain warp, plus a low-frequency "flatness" mask for buildable lowlands
- `TerrainChunk` (ArrayMesh), `Terrain` (exports, Regenerate tool button, `MarkDirty`/`RebuildDirty` hooks)
- Height/slope colouring shader with a 64 m grid
- `CityCamera` orbit rig with smoothing, bounds clamp and ground clearance
- Debug overlay (FPS, camera info) and automated screenshot mode
- Result: 513×513 vertices, 64 chunks, ~200 ms generation, ~60 FPS (vsync) on an M1

### 🔶 M1.1: Remove spiky terrain (uncommitted work in progress)
Changes in the working tree that still need the user to check them visually:
- octave count clamped so the finest octave spans ≥ 8 cells (stops aliasing into spikes)
- `Gain` export (default 0.42) and `SmoothPasses` export (default 2), using a 1-2-1 blur in `HeightMap.Smooth`
- `TerrainChunk` splits each quad along the diagonal whose corners are closest in height

Next step: screenshot, get the user's OK, commit.

### 🔶 M2: Mouse picking + sculpt brushes (implemented, waiting for the user to test)
Done:
- `Terrain.Raycast`: ray-marches the heightmap in cell-sized steps inside the XZ bounds, then refines
  with 16 rounds of binary search. No physics.
- Engine-agnostic sculpt core in `src/Terrain/Sculpt/`: `Brush` (smoothstep falloff, plus a plateau
  variant), `SculptOps` (Shift, Level, Smooth, Slope, DominantHeight) and `UndoStack`. `VertexRect`
  and `HeightMap.CopyRegion`/`PasteRegion`/`Snapshot`/`CircleRect` are also engine-agnostic.
- `TerrainToolController` (`src/Tools/`): fixed 60 Hz edit tick, strokes, undo/redo, brush keys.
  Edits call `Terrain.MarkDirty`, and `Terrain._Process` (ProcessPriority 100) rebuilds dirty chunks
  once per frame.
- The brush ring, and the slope start marker with its guide line, are drawn in `terrain.gdshader`
  (uniforms `brush_*` and `anchor_*`).
- UI (`src/UI/`, built in code): a bottom bar with category buttons, a tool panel centred above it
  (tabs, close button and tool buttons) and a config panel at bottom-left (size and strength steppers,
  per-tool rows and hints). Tools, tabs and categories are data in `ToolCatalog.cs`. Every
  button has an `Icon` slot, and text placeholders are shown until real icons arrive.
- Shift has a max-slope limit (config panel, 10–80°, default 40°). It never pushes a vertex steeper
  than that against a neighbour, and ground that's already steeper is left alone. Shift also applies
  light built-in rounding. Together these stop small brushes making spikes and long holds making spires.
- Contour lines (toggle and spacing of 1/2/5/10/20 m in the config panel, every 5th line bolder), drawn in
  the shader (`show_contours`, `contour_interval`). Shown only while a terrain tool is active.
- `--demo-sculpt` CLI flag: scripted strokes plus an undo/redo self-check (prints `Demo sculpt: undo ok, redo ok`).

Tools (user-specified):
| Tool | LMB | RMB |
|---|---|---|
| Shift | raise | lower |
| Level | level to target | pick target height. With no target set, each stroke uses the falloff-weighted dominant height under the brush |
| Smooth | smooth | – |
| Slope | press at end point B and drag: pulls ground toward ramp A→B | set start point A |

Brush: `[` `]` or Shift+wheel for size (the UI shows diameter, 16–800 m; code uses radius), Alt+wheel for strength. Ctrl/Cmd+Z undo,
Ctrl/Cmd+Shift+Z or Ctrl+Y redo, Esc deselects the tool. Clicks on UI panels never reach the terrain.

Measured on an M1 (debug build): at 4 m cells a 60 m-radius brush rebuilt 8 chunks in ~8 ms. At 2 m cells
the same brush rebuilds 18 chunks in ~13 ms, and generation takes ~0.85 s. Accepted for now: features first, then performance (M6 LOD).

Next:
- User tests in Godot, then commit
- If rebuild cost hurts: `ChunkCells` 32, or update vertex buffers in place
  (`RenderingServer.MeshSurfaceUpdateVertexRegion`)
- Real icons (user will provide) → set `Icon` in `ToolCatalog`
- Later tabs/categories (vegetation etc.) plug into `ToolCatalog`

### ⬜ M3: Better texturing
- Texture splatting (grass, dirt, rock, sand) with triplanar mapping on steep slopes
- A paintable splat map (RGBA `ImageTexture`) with a paint brush that reuses the M2 brush code
- Detail/normal textures; hide tiling with distance blending
- Keep the grid overlay as a toggle, since it's useful for building placement later

### ⬜ M4: Save / load + heightmap import
- Binary save of the heightmap and splat map (versioned header), load back
- Import a 16-bit PNG/RAW heightmap (real-world DEM data) and export as well
- A "new map" dialog with seed and size

### ⬜ M5: Water
- Sea level plane with a simple water shader (depth colour, shoreline foam)
- Later: rivers/lakes (flow simulation or painted water sources); buildable = above water
- Water queries in `Terrain`: `IsUnderwater(x, z)`, water depth

### ⬜ M6: Performance & scale
- Level of detail per chunk: geomorphing, or skirts to hide cracks between detail levels
- Frustum culling is automatic per MeshInstance3D; check draw calls at larger maps
- Try 4096×4096 cells (for example 16 km at 4 m). Measure generation time, memory and FPS.
- Profile generation and rebuild; candidates for multithreading (`Parallel.For` on
  height generation using per-thread noise or precomputed noise images) or C++ GDExtension
- Consider a GPU heightmap (vertex shader displacement from a texture) as an alternative to CPU meshes

### ⬜ M7: City-building queries (prep for merging)
- `IsBuildable(rect, maxSlope)`, `GetAverageHeight(rect)`, `FlattenForPlacement(rect or polygon)`
- Road support: a flatten/blend corridor along a spline with max grade and embankments
- Tree/foliage scatter respecting slope/height (MultiMeshInstance3D) as a stress test
- Clean public API surface; document it here before merging into the main game

### Nice-to-have / ideas
- Hydraulic + thermal erosion (a good first C++ GDExtension candidate)
- Edge-of-map treatment (distant "fake" terrain ring beyond the playable area)
- Camera: edge-scroll with the mouse, middle-drag rotate, double-click to focus

---

## Decision log
- Started with C# only, adding C++ later only for measured hot paths.
- R/F interpreted as **tilt**, not altitude. The user confirmed by asking only for the direction to be inverted.
- Q/E and R/F directions inverted per user feedback.
- M2 UI layout follows a Cities: Skylines reference: the tool panel is centred above the bottom bar and
  the config panel is at bottom-left. The tool panel has tabs so vegetation and similar tools can be
  added later.
- Cell size changed from 4 m to 2 m (1024×1024 cells, still a 2,048 m map), so small brushes have
  enough vertices to look round. The extra cost is accepted until LOD/performance work in M6.
- Level with no picked height uses the "dominant" height (weighted histogram), not the plain average.
