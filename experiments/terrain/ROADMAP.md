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

## Camera controls (Cities: Skylines style; `src/Camera/CityCamera.cs`)

| Input | Action |
|---|---|
| W/A/S/D | move relative to facing (speed scales with zoom, min 12 m/s, Shift = 2x) |
| Q/E | rotate yaw (`InvertRotate` export flips it) |
| R/F | tilt (`InvertTilt` export flips it) |
| Z/X, mouse wheel, trackpad scroll/pinch | zoom (wheel/scroll zooms toward the cursor) |
| Middle-mouse drag | rotate (horizontal) and tilt (vertical) |
| Mouse at window edge | pan, only if `EdgeScroll` is on (off by default) |

Behaviour depends on zoom `z` (0 = first person, 1 = god view, log-scaled): orbit radius reaches 0 (FPV at
eye height, can look up to -10°), min pitch rises -10 -> 40°, near plane and ground clearance grow.
Edge margin: the pivot **and the camera itself** stay `EdgeMargin` (200 m) inside the terrain edge, so walking or
rotating pushes the pivot inward instead of swinging out over the edge.
Smoothness: pivot height is the ground averaged over a footprint of ~12% of the zoom distance and follows slowly when
zoomed out (fast in FPV); hills are cleared by smoothly lifting the camera along a 32-sample line of sight (fast up,
slow down) rather than stepping the pitch.
`--demo-camera` feeds real input events (Z/X, wheel, WASD, Q/E) and checks zoom out of FPV and the margin
plus frame-to-frame jerk while flying over the mountains at four zooms (prints `Demo camera: all ok`).

## How to build / verify

```sh
cd experiments/terrain
dotnet build                                                   # compile check
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
$G --headless --path . --quit-after 120                        # runtime errors, generation timing
$G --path . -- --screenshot=/path/out.png --screenshot-frames=90   # real render → PNG, then view it
# extra flags: --demo-sculpt / --demo-paint / --demo-camera (scripted strokes + undo check), --cam=x,z,distance,pitch,yaw (close-ups)
tools/fetch_textures.sh      # first time (or after changing a texture): download, bake, import ground textures
```

VS Code (repo-root `.vscode/`): F5 "Terrain: Play" builds and runs with the C# debugger attached (also
paint/sculpt demo variants, "Open Godot editor", "Attach to Godot"). Tasks: build, fetch + bake textures,
rebake, headless check, screenshot (`screenshots/latest.png`).

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

### 🔶 M3: Texturing (implemented, waiting for the user to test)
Look: between realism and toon, leaning Ghibli. The shader tints each texture with a hand-picked colour and
keeps only its detail, so the palette lives in the material (`Style` uniforms), not in the photos.

Texture pipeline:
- `tools/fetch_textures.sh` downloads 8 CC0 ambientCG sets (2K Color, NormalGL, Displacement) into
  `assets/textures/terrain/<layer>/` (gitignored, with a `.gdignore` so Godot doesn't import them). It then runs
  `--bake-terrain-textures` (`TextureBaker`) and `--import`. To swap a texture, edit the ID list in the script.
- The baker writes two 1024²×8 vertical strips: `terrain_albedo_height.png` (colour normalised to a 0.4 grey
  average, height in A) and `terrain_normal.png`. Their committed `.import` files import them as BC7
  `CompressedTexture2DArray`s with mipmaps (about 25 MB of GPU memory).

Layers (`TerrainLayers.cs`, same indices in the shader):
| # | Layer | Texture | Paint | Automatic rule |
|---|---|---|---|---|
| 0 | grass | Grass005 | ✔ | base |
| 1 | grass_dry | Grass004 | ✔ | noise patches, more on high ground |
| 2 | grass_dirt | Ground037 | ✔ | medium slopes (`dirt_slope`) |
| 3 | dirt | Ground103 | ✔ | – |
| 4 | gravel | Ground062S | ✔ | scree just below rock, broken up by noise |
| 5 | sand | Ground101 | ✔ | below `sand_height` (future shorelines) |
| 6 | rock | Rock051 | ✔ | slope > `rock_slope`, triplanar |
| 7 | snow | Snow010A | ✔ | above `snow_height` on gentle slopes; noisy, thinning edge over `snow_blend` |

Done:
- Shader: automatic weights from height, slope and noise. Painted weights override them by their coverage.
  The 4 strongest layers are sampled and height-blended (`height_blend`, `blend_softness`). Planar XZ
  sampling at two scales, blended by distance (`tile_near`, `tile_far`) to hide tiling; rock is triplanar.
  Large-scale and mid-scale noise vary brightness and warmth. A custom `light()` adds wrapped diffuse, an
  optional soft toon ramp (`toon_bands`) and a warm terminator.
- `SplatMap` (engine-agnostic): 8 float weights per heightmap vertex, summing to ≤ 1. `Terrain` uploads it as
  two RGBA8 textures once per frame when dirty. The whole texture is uploaded each time; partial upload is
  left for M6.
- Paint tool: a "Paint" tab with 8 layer buttons. LMB paints, RMB erases back to automatic, and it reuses the
  brush size and strength. `UndoStack` now holds height and/or splat regions, so sculpt and paint share
  one undo history.
- The grid is off by default: toggle with `G` or the config panel.
- Atmosphere: warm sun, softer shadows, blue sky with a pale horizon, lighter blue aerial fog, AgX tonemap,
  saturation 1.1.
- `--demo-paint`: dirt path, sand patch, gravel patch and erase, plus an undo check.
- 60 FPS (vsync) on M1 at 1600×900.
- Look pass after first user test (fixes tiling, washed-out colour, rough borders):
  - Dropped the snow_grass layer (Snow015 tiled badly). The snow line now wanders with noise and thins out, and
    height blending lets grass show through its edge.
  - Stronger texture detail: `detail_contrast` 0.85, `detail_saturation` 0.65, `normal_strength` 1.0. Environment
    keeps AgX, adds `adjustment_contrast` 1.12 and saturation 1.15 (Filmic/ACES blew out the snow).
  - Softer borders: `height_blend` 0.12, `blend_softness` 0.45. At 0.35/0.2, flat ground sitting near 50/50 between
    two layers was decided per pixel by texture height, which gave crisp speckled edges. A fine `edge_noise_scale`
    noise wobbles the automatic slope and height thresholds.

- Ghibli palette pass: warmer, more saturated tints; grass drifts from cool green (low) to gold-green (high);
  rock is triplanar with two scales mixed by noise plus domain warp (no visible repeat), softened normals and
  a warm mossy top / cool violet face tint; teal-violet fill in shadows (`shadow_tint`); lavender-blue fog
  matching the sky horizon, cream sun. Screenshot check: `--cam=1300,700,120,25,30` (cliff close-up).

Next:
- User tests in Godot and tunes the `Style`/`AutoLayers` uniforms on the terrain material, then commit
- Splat save/load belongs with M4
- Possibly: per-layer tile sizes, a brush that paints only on slopes above or below an angle

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
- Camera: double-click to focus

---

## Decision log
- Camera rework: zoom-dependent limits, FPV at distance 0, zoom-scaled edge margin, CS-style directions and mouse drag (implemented, waiting for the user to test).
- Started with C# only, adding C++ later only for measured hot paths.
- R/F interpreted as **tilt**, not altitude. The user confirmed by asking only for the direction to be inverted.
- Q/E and R/F directions inverted per user feedback.
- M2 UI layout follows a Cities: Skylines reference: the tool panel is centred above the bottom bar and
  the config panel is at bottom-left. The tool panel has tabs so vegetation and similar tools can be
  added later.
- Cell size changed from 4 m to 2 m (1024×1024 cells, still a 2,048 m map), so small brushes have
  enough vertices to look round. The extra cost is accepted until LOD/performance work in M6.
- Level with no picked height uses the "dominant" height (weighted histogram), not the plain average.
- M3: stylized, not photoreal. Textures are only a detail source, and colour comes from per-layer tints in the
  material. Ground textures are downloaded by a script, not committed (about 150 MB). Painted layers are
  stored per vertex (2 m), and height blending adds sharper detail at the borders.
- M3: sand swapped from Ground080 (strong ridges, ugly when painted on slopes) to Ground101 (fine, smooth grain).
- M3: rock swapped from Rock060 (marble-like veins) to Rock051 (layered ledges, which read better as cliffs).
