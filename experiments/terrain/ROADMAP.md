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
  `Terrain` and `TerrainChunk`. The generator (`src/Terrain/Generation/`) is engine-agnostic too (vendored C# FastNoiseLite).
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
eye height, locked to eye level or above: can look up to -30° but not down), min pitch rises -30 -> 40°, max pitch
opens 0 -> 89° by ~20 m orbit, near plane and ground clearance grow.
Edge margin: the pivot **and the camera itself** stay `EdgeMargin` (200 m) inside the terrain edge, so walking or
rotating pushes the pivot inward instead of swinging out over the edge.
Smoothness: pivot height is the ground averaged over a footprint of ~12% of the zoom distance and follows slowly when
zoomed out (fast in FPV); hills are cleared by smoothly lifting the camera along a 32-sample line of sight (fast up,
slow down) rather than stepping the pitch. The lift is stored as a fraction of the orbit distance so it shrinks
on zoom-in (an absolute lift left at distance ~0 pointed the view straight down).
`--demo-camera` feeds real input events (Z/X, wheel, WASD, Q/E) and checks zoom in/out of FPV, the FPV eye-level lock and the margin
plus frame-to-frame jerk while flying over the mountains at four zooms (prints `Demo camera: all ok`).

## How to build / verify

```sh
cd experiments/terrain
dotnet build                                                   # compile check
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
$G --headless --path . --quit-after 120                        # runtime errors, generation timing
$G --path . -- --screenshot=/path/out.png --screenshot-frames=90   # real render → PNG, then view it
# extra flags: --demo-sculpt / --demo-paint / --demo-camera (scripted strokes + undo check), --demo-mapfile (save/load round trip),
#   --demo-heightmap (16-bit PNG/RAW export+import round trip), --cam=x,z,distance,pitch,yaw (close-ups), --flat[=height] (empty map),
#   --load=path.csmap, --heightmap=path[,min,max] (import a 16-bit PNG/RAW as a 2 km map), --game (game mode),
#   --preset=island|coast|archipelago|mountains|flat-lowlands|rolling-hills, --seed=n, --show-generator (open the panel),
#   --demo-generate (generator timing, preview vs full, tiling, one-step undo of live updates),
#   --demo-scale[=cells] (headless data benchmark at 8193²: generate/stroke/undo/save/load/RAM, then quits),
#   --size=cells (8192 = 28.7 km), --coarse=n (show an n² copy of the map, for sizes the chunk renderer can't draw)
# any flag skips the start menu (scenes/Menu.tscn)
tools/fetch_textures.sh      # first time (or after changing a texture): download, bake, import ground textures
tools/fetch_brushes.sh       # only after changing a brush: download stamps, bake + import brush masks (baked masks are committed)
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

Brush: `[` `]` or Shift+wheel for size (the UI shows diameter, 16–800 m; code uses radius), Alt+wheel for strength,
hold Ctrl + move mouse to rotate (brush stays put), Ctrl+Q/E 15° steps (Ctrl+Shift 45°), Ctrl+wheel 5°. `C` toggles contours. Ctrl/Cmd+Z undo,
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

### 🔶 M3.1: Edge fog (implemented, waiting for the user to test)
Hides the map's hard edges and corners behind a fog bank. This is separate from the distance fog in the Environment.
- `terrain.gdshader` `EdgeFog` group: the ground fades into an unlit fog colour (emission, no lighting or specular)
  over `edge_fog_width` metres from the border. Corners are rounded (`edge_fog_corner_radius`), and two drifting noise
  octaves push the fog line inward by up to `edge_fog_wobble` so it billows. `Terrain` sets `terrain_origin`/`terrain_size`.
- `TerrainSkirt` (child of `Terrain`, same material): a ring mesh whose inner loop is the terrain's border vertices, so
  there's no gap, then sinks below the lowest point and spreads 11 km out. Everything outside the bounds is pure fog in
  the shader, so the skirt reads as a fog floor instead of the sky showing under the map. Corners are fanned. It's rebuilt
  when a sculpt edit touches the border. No shadows.
- Check with `--cam=250,250,1500,40,225` (whole map, corner in front) and `--cam=260,260,450,30,45` (corner close-up).
- Known: a faint line where the skirt ends at the horizon (distance fog colour vs sky horizon colour). With the 200 m
  camera margin, the pivot can sit inside the thinner part of the fog. Tune width and wobble if that bothers the user.

### 🔶 M3.2: Brush textures and rotation (implemented, waiting for the user to test)
- 9 brushes (`BrushLibrary`, `src/Tools/`): Soft Round (the old falloff, default), 5 terrain stamps from
  [Roland09/Terrain-Stamps](https://github.com/Roland09/Terrain-Stamps) (MIT: Hills, Ridged, Plateau, Plateau Talus, Terraces)
  and 3 generated paint alphas (Splatter, Noise Patch, Streaks).
- `tools/fetch_brushes.sh` downloads the 2048² 16-bit stamps to `assets/brushes/src/` (gitignored) and runs
  `--bake-brushes` (`TextureBaker.BakeBrushes`): crop to content, 256² L8, normalise, radial edge fade. The baked
  `assets/brushes/*.png` (~15 KB each) and `LICENSE.md` are committed. `detect_3d` is off in their `.import` so they stay lossless.
- Engine-agnostic `BrushMask`; `Brush(Radius, Strength, Mask, Angle)` with `Weight(dx, dz)`. `SculptOps`/`PaintOps` visit with
  weights instead of distances. Masks apply to Shift, Level, Smooth and Paint; Slope stays round. Shift's built-in rounding is
  ¼ strength for masked brushes so the detail survives.
- Rotation modes (config panel button): Fixed, Random (new angle per click), Follow (turns along the drag).
- Shader preview: the fill shows the rotated mask (`brush_mask`, `brush_angle`), plus a tick on the ring at the brush angle.
- Contours now show with no tool selected too (toggle with `C`).
- `--demo-sculpt` stamps Ridged (45°) and Terraces on a levelled pad; `--demo-paint` paints Splatter and Streaks (30°).

### 🔶 M3.3: Menus (implemented, waiting for the user to test)
- `scenes/Menu.tscn` (`src/UI/MainMenu.cs`) is now the main scene: New Map, Load Map, Quit (see M4).
  Any command-line user flag (`--screenshot`, `--demo-*`, `--cam`, ...) skips it and loads `Main.tscn` directly.
- Esc menu (`src/UI/PauseMenu.cs`, added by `GameUi`): Resume, Save, Save As, Load, Main Menu, Quit (see M4).
  Pauses the tree while open. With a tool selected, Esc deselects the tool first.
- Deliberately plain: default buttons in a column, no art.

### 🔶 M4: Map files + New Map (implemented, waiting for the user to test)
Done:
- `MapFile` (`src/Terrain/MapFile.cs`, engine-agnostic): `.csmap` = magic `CSMP` + u16 version + zlib stream of
  width, depth, cell size, layer count, f32 heights and f32 splat weights. Lossless. Written to `.tmp` then moved, so a
  failed save never destroys the old file. Loading a file with a different layer count keeps the shared layers.
  A 2 km map with edits is ~4.6 MB, saves in ~65 ms, loads in ~30 ms (M1).
- `MapSession` (`src/App/`, engine-agnostic): `AppMode` (MapEditor / Game), the pending `MapRequest` for the next map
  scene (`GeneratedMapRequest` with `GenSettings`, `LoadedMapRequest`; see M4.2) and the current file path.
  Files are read in the menu *before* the scene change, so a bad file shows an error instead of an empty scene.
- `Terrain.Open(request)` / `Terrain.SetMap(map, splat)`: build chunks from any map. `Generate()` now goes through `SetMap`.
- Start menu → **New Map**: Generated (seed + Random) or **Flat (empty)** (height, default 40 m), size 1 / 2 / 4 km.
  **Load Map** uses the native file dialog, default folder `user://maps`.
- Esc menu: Save (Ctrl/Cmd+S anywhere; asks for a file the first time), Save As, Load (reloads the scene with the new map).
  A toast at the top confirms saves and shows errors.
- Mode flag: `ToolTab.EditorOnly` (the Paint tab) is hidden in Game mode. The menu always starts the Map Editor; `--game` tests game mode.
- CLI: `--flat[=height]`, `--load=path`, `--game`, `--demo-mapfile` (save, load, compare every value, prints
  `Demo mapfile: round trip ok`; run after `--demo-sculpt --demo-paint` to round-trip real edits).

Next:
- Map metadata in the file (name, author, generator settings) once there's something to show it.
- 4 km maps (2049² verts, 1024 chunks) are allowed but untested for generation time and FPS (see M6).

### 🔶 M4.1: Heightmap import/export (implemented, waiting for the user to test)
- `HeightmapImage` (`src/Terrain/HeightmapImage.cs`, engine-agnostic): 16-bit PNG and RAW (`.r16`/`.raw`, square,
  little-endian u16, no header). Godot's `Image` drops 16-bit PNGs to 8 bits, so the PNG codec is in C#: it decodes
  grey / grey+alpha / RGB / RGBA at 8 or 16 bits (first channel = height, all 5 row filters) and rejects palette,
  sub-8-bit and interlaced files. Exports 16-bit grey with Sub filtering.
- Mapping: black = Lowest, white = Highest. Image row 0 → z = 0 (north), column 0 → x = 0. Non-square images are
  cropped to their centre square, then resampled to the map size: bilinear when upscaling, box average when shrinking > 1.5×.
- Export writes the map's own min–max as 0–65535 and stores the range in a PNG `tEXt` chunk (`CitySim`,
  `min=..;max=..`), which the import form reads back to prefill Lowest/Highest. RAW has nowhere to keep it; the toast shows it.
- UI: New Map → Terrain **From heightmap** (Browse, Lowest, Highest, Size). Esc menu → **Export Heightmap…**.
  Default folder `user://heightmaps`. Only heights come across; painted layers start empty.
- Checked: `--demo-heightmap` round trips PNG and RAW within half a 16-bit step (1.1 mm on the demo map; PNG 1.4 MB, RAW 2 MB);
  decodes a real 2048² 16-bit stamp to within 1 of Godot's 8-bit decode; a Python-made test set covering every filter and colour
  type decodes exactly. 2048² → 1025² import takes ~55 ms.
- Not done: GeoTIFF (common for real DEMs: convert with `gdal_translate -of PNG -ot UInt16 -scale` or export PNG/RAW from
  QGIS/Gaea), importing into the open map (only via New Map, which resets paint), non-square maps.

### 🔶 M4.2: Terrain generator panel (implemented, waiting for the user to test)
Making terrain moved from the start menu into the Map Editor, with a preview of what you'll get.
- Engine-agnostic core in `src/Terrain/Generation/`: `GenSettings` (records, copied with `with`), `TerrainGen`
  (`Create` full map, `Preview` n² over the same area, rows in `Parallel.For`), `ShapeMask`, `HeightmapSampler`, `GenPresets`.
  Noise uses the official C# **FastNoiseLite** (vendored, MIT), set up like Godot's wrapper: the default map is identical
  to the old `TerrainGenerator` (checked: rms 0 m), which is removed.
- Two layers: a base (Noise / Heightmap image / Flat) and a land/sea **shape** (None, Island, Coast, Archipelago) in map
  units (-1..1, so it's the same at any size). `h = lerp(seaFloor, lerp(SeaLevel, base, m), m)`: hills flatten to sea level,
  then drop to the sea floor over the shore width. Coastlines wobble with low-frequency fBm (`Coast Roughness`).
  No water yet (user's choice): the sea is sandy low ground (sand_height 9 m, presets use sea level 6 m).
- Heightmap placement: rotation (+90° button), scale, offset X/Z (map widths), edges Clamp / Tile / Mirror / Fill (flat at
  Lowest); box-averaged when shrinking. `HeightmapImage.ToHeightMap` is now the identity placement. Shapes apply to images too.
- Presets: Rolling Hills (old default), Mountains, Flat Lowlands, Island, Coast, Archipelago. A preset fills the hills and
  shape settings, and keeps size, seed and image.
- `GeneratorPanel` (right side, Map Editor only; bottom-bar **Generate**, Esc menu **Terrain Generator…**): 257² top-down
  preview (hillshade, height tint, blue below sea level) redrawn on every change (4–12 ms); **Live** regenerates the 3D
  terrain 0.3 s after the last change on a worker thread (newer runs supersede older ones), or **Apply**. All updates
  while the panel is open are **one undo step** (`TerrainToolController.ApplyGenerated` / `CommitGenerated`,
  `UndoStack.PushHeights`); painted ground is kept. A new size replaces the map (history and paint cleared).
  Opening a tool category closes the panel; Esc closes it first.
- Start menu New Map: Size + Start from **Generator** (preset, random seed) / **Flat** / **Heightmap image**. Generator and
  heightmap open the editor with the panel showing; height range and placement are set there.
- Timing (M1): 2 km full generate ~90 ms + rebuilding 256 chunks ~140 ms. `<Optimize>true</Optimize>` in the csproj
  (also for Debug) made generation ~6× faster (570 → 90 ms); the debugger may show locals as optimized away.
- Next: user tests; water (M5) will make Sea Level real; maybe rivers/valley shapes, a "blend with current map" mode,
  and saving generator settings in the map file.

### ⬜ M5: Water
- Sea level plane with a simple water shader (depth colour, shoreline foam)
- Later: rivers/lakes (flow simulation or painted water sources); buildable = above water
- Water queries in `Terrain`: `IsUnderwater(x, z)`, water depth

### 🔶 M6: Performance & scale (phases 0–1 done; phase 1 waiting for the user to test)
Target (user): a 70 × 70 km map on an 8 GB M1. Tiered like CS2: a **28,672 m build area** (8192 cells × 3.5 m, an "8k"
heightmap) sculptable and buildable, centred in a coarse 70 km background (4096 cells ≈ 17 m, scenery only).
Rendering moves to the **Terrain3D** addon (v1.0.2, `addons/terrain_3d/`, MIT; GPU clipmap, 1024² regions). `HeightMap`
stays the source of truth for queries, tools and saves, and Terrain3D is only the render copy.

Phases (tick off as they land):
- [x] **0. Terrain3D spike**: go/no-go on 4.7.2 + 8 GB. Passed, numbers below.
- [x] **1. Data at build-area scale** (engine-agnostic, `src/Terrain/`; implemented, waiting for the user to test)
  - Sizes: `MapSize.All` in `GenSettings.cs` (1.8 / 3.6 / 7.2 / 28.7 km = 512–8192 cells, powers of two), all at
    `GenSettings.DefaultCellSize` = 3.5 m. 28.7 km is `NeedsTerrain3D` and hidden in the menus until phase 2 (the chunk renderer
    can't draw 16k chunks); `--size=8192 --coarse=2049` shows a downsampled copy, `--demo-scale` tests the data.
  - New Map generates on a worker in the menu with a % readout, then opens the scene (`GeneratedMapRequest.Map`); the generator
    panel shows % too. `TerrainGen.Fill` writes rows straight into the map; `HeightMap.Smooth` is separable and parallel (rows,
    then 256-wide column strips with one row buffer each): ~90 ms at 8193².
  - Speed: slow fields (lowland mask, region, land/sea shape, warp octaves ≥ 168 m) are sampled on a ~14 m coarse grid and
    interpolated; per vertex only the fine warp octave and the hill fBm run. Warp octaves finer than 8 cells (6 m and 1 m
    wavelengths, sub-cell jitter that smoothing removed) are skipped. `FastNoiseLite.DomainWarpProgressiveOctaves` (our
    addition) runs a slice of the progressive warp. 8193²: 2.3–2.7 s for every preset (was 4.3–8 s).
  - Scale-aware noise: a region layer (`NoiseSettings.RegionSize` 10 km, `RegionStrength` 0.7) turns hills into ranges
    (taller, rugged, raised) and plains (flatter, lower). Weight 0 up to 4 km, full from 16 km (`TerrainGen.RegionWeight`),
    so small maps keep their tuned look. Coastline/archipelago noise gets +1 octave per doubling above 2 km.
  - `UndoStack`: nothing copied at stroke start; `Touch(rect)` is called *before* each edit and copies each 128² tile on first
    touch. One buffer per tile, swapped with the map on undo and on redo (holds whichever state is off the map). Generator
    entries keep one full copy. 768 MB budget, oldest entries dropped first.
  - `SplatMap`: one u32 control value per vertex in Terrain3D's layout (base 27–31, overlay 22–26, blend 14–21, hole 2, nav 1,
    auto 0) plus our **coverage** in bits 6–13 (Terrain3D's UV angle/scale, unused by us): painted share over the automatic
    ground, so soft brush edges still fade into the auto layers. Sparse 256² tiles, allocated on first paint. At most two
    painted layers per vertex; painting a third replaces the lighter one. `PaintOps` rounds to 8 bits with hashed dither so
    slow edges neither stall nor creep. The current shader still gets 8 weights (`WriteRgba8` decodes).
  - `HeightMap.GetRange`: min/max cached per 64² block; the indexer, `PasteRegion`/`SwapRegion` and `Invalidate(rect)` mark
    blocks stale. Bulk writers through `Data`/`Row` call `Invalidate()`. 0.4 ms after a stroke at 8193² (was a full scan).
  - `MapFile` v2: u16 heights over min/max, row-delta coded, zlib per 256² tile (parallel), painted control tiles only, an
    empty background slot for phase 3. Reads v1 (weights → two heaviest layers). 16-bit steps: 7.6 mm per 500 m of range.
  - Heightmap size (decided): keep `HeightMap` at cells + 1 = 8193². Phase 2 drops the last row/column in the Terrain3D copy
    (8192² px = exactly 8×8 regions); the phase 3 background ring covers that 3.5 m strip. Keeps the generator, files, queries
    and every small size unchanged.
  - `--demo-scale[=cells]` (8193², Mountains, M1 8 GB): generate 2.2 s, first range 19 ms, sculpt tick worst 1.9 ms, paint
    60 ticks 8 ms, undo/redo ok, save 0.36 s, load 0.19 s, 104 MB file, peak footprint 1.4 GB with two maps alive (spike: 3.6 GB).
  - Known: `--demo-camera`'s three "first person" zoom checks fail headless (stuck at 16.5 m). Same on the previous commit, so
    not caused by this work.
- [ ] **2. Terrain3D renderer**
  - New `Terrain3DBridge.cs`: the only file with Terrain3D `Call("...")` strings (create node, `change_region_size` *after*
    `AddChild`, `vertex_spacing`, collision `DISABLED`, import, push dirty rect, `update_maps`, material params).
  - `Terrain.cs`: remove `TerrainChunk` + per-chunk rebuild. `MarkDirty` collects a rect; `_Process` pushes it once per frame
    (`set_height` loop or region image) + `update_maps`. Paint the same way via the control map. Delete splat `ImageTexture` upload.
  - Port the look (tints, auto layers, height blend, triplanar rock, custom `light()`, brush ring, anchor, contours, grid, edge fog)
    into a Terrain3D shader override. Decode our coverage bits (see `SplatMap`). The snow line is an absolute 125 m: at 28 km
    (ranges up to ~1,100 m) nearly everything is snow, so make it a per-map setting (or relative to the height range). This is the biggest risk. Compare screenshots with `--cam=1300,700,120,25,30`.
  - Queries/raycast stay on `HeightMap`; maybe a coarser first march step for long rays.
- [ ] **3. 70 km background**
  - `BackgroundMap`: 4097² `HeightMap` over 70 km from the same `TerrainGen` settings (world coords), build area downsampled into the centre.
  - Own coarse ring mesh (17 → 70 → 280 m cells) with a hole for the build area, welded to its border; replaces `TerrainSkirt`.
  - Edge fog moves to the 70 km border; subtle marker on the build border. `CityCamera`: `MaxDistance` → 8–10 km, `Far` → ~80 km,
    pivot stays inside the build area.
- [ ] **4. Measure + document**: extend `--demo-scale` (phase 1: data timings) with Terrain3D push timings and peak RAM. Targets on the M1:
  60 FPS at every zoom, generate < 3 s, load < 2 s, stroke < 4 ms/frame, RAM < 2.5 GB. All `--demo-*` flags still pass at 2 km and 28 km.

Open question: commit the addon (51 MB, every platform) as-is, trim it to macOS, or fetch it with a script like the textures.

Phase 0 spike (`scenes/Spike.tscn`, `src/Debug/Terrain3DSpike.cs`, throwaway; run
`$G --path . res://scenes/Spike.tscn -- --spike-size=8192 --spike-out=dir`). M1 8 GB, Terrain3D **debug** library:
| | 2048² (7 km) | 8192² (28.7 km) |
|---|---|---|
| generate (`TerrainGen.Create`) | 0.58 s | 6.2 s |
| `import_images` (1024 regions) | 0.15 s | 2.5 s |
| FPS overview / 1.5 km / ground 20 m | 135 / 65 / 88 | 82 / 62 / 82 |
| draw calls, triangles | ~100, ~640k (constant) | same |
| video memory | 283 MB | 1088 MB |
| peak memory footprint | – | 3.6 GB (transient copies during generate + import) |
- Loads fine in Godot 4.7.2 .NET (officially 4.4–4.6). Heights match `HeightMap` exactly (`vertex_spacing` 3.5).
- `change_region_size` is ignored before the node is in the tree: call it after `AddChild`.
- Brush-sized edit (35² px): `set_height` ×1225 via `Call` ~1 ms, or rewrite a region image ~15 ms; `update_maps` ~0 ms.
- `get_height` via `Call` is ~2 µs vs 0.09 µs for `HeightMap.SampleHeight`: queries must stay on our side.
- Terrain3D keeps a CPU copy of every region map plus height/control/colour on the GPU (≈ 12 B/px).
- Noise presets are tuned for 2 km maps: at 28 km the hills look like pimples. The generator needs scale-aware settings.

### ⬜ M7: City-building queries (prep for merging)
- `IsBuildable(rect, maxSlope)`, `GetAverageHeight(rect)`, `FlattenForPlacement(rect or polygon)`
- Road support: a flatten/blend corridor along a spline with max grade and embankments
- Tree/foliage scatter respecting slope/height (MultiMeshInstance3D) as a stress test
- Clean public API surface; document it here before merging into the main game

### Nice-to-have / ideas
- Hydraulic + thermal erosion (a good first C++ GDExtension candidate)
- Edge-of-map: the M3.1 skirt is flat fog. A real fake-terrain ring (low-poly hills fading into the fog) could replace it
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
- M3.2: brush rotation copies the CS1 RotateBrush mods (neither CS1 nor CS2 rotates terrain brushes without mods): hold Ctrl + move mouse,
  Ctrl+Q/E steps. Not right-drag like CS buildings, because RMB already lowers/picks/erases. The camera ignores Q/E while Ctrl is held.
- The experiment is a **map editor tool** as well as a gameplay test bed (for us, modders and gamedevs), not the game.
  It runs as an in-app Map Editor (like the CS Map Editor), so modders don't need Godot. Menus and UI stay minimal;
  editor features (map files, New Map, heightmap import) come before UI polish. The editor/game mode flag landed in M4.
- M4.2: the generator lives in the Map Editor (panel with preview + live 3D), not the start menu. No water plane yet for
  island/coast presets: the user chose to wait for M5. Live generator updates are one undo step, not one per change.
- M6: 70 km map = 28 km build area at 3.5 m (8192²) + coarse 70 km background, like CS2. Uniform 2 m over 70 km would be
  1.2B heights (4.9 GB) plus 39 GB of paint weights. Rendering via the Terrain3D addon (user's choice over our own clipmap).
- M3.2: Random rotation rolls once per click, not per tick: a new angle every tick blurs a stationary brush into a round blob.
- M6 phase 1: new maps use 3.5 m cells (CS2's spacing), replacing the 2 m decision above; sizes are powers of two (1.8–28.7 km).
  Old 2 m maps still load at their own cell size. Paint is two layers per vertex plus coverage (Terrain3D's control format);
  map files store 16-bit heights, like CS2 and heightmap exports.
