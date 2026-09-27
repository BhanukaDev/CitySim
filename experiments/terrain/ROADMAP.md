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
- **C++ for erosion** (since M5.0): a plain `extern "C"` library in `native/erosion/`, loaded with `NativeLibrary` and
  called through C# function pointers on `HeightMap.Data` in place. Not a GDExtension: no godot-cpp/scons, no
  `Call("…")` strings, no array copies, and it stays engine-agnostic.
- **`HeightMap` stays engine-agnostic** (plain C#, `System.Numerics`). Godot-specific code lives in
  `Terrain`, `Terrain3DBridge` and `TerrainSkirt`. The generator (`src/Terrain/Generation/`) is engine-agnostic too (vendored C# FastNoiseLite).
- **Rendering via Terrain3D** (since M6 phase 2; M1–M5 used our own 64²-cell chunk meshes): `HeightMap`/`SplatMap` are the
  source of truth, Terrain3D holds a render copy, and edits push only the dirty rectangle once per frame.
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
native/erosion/build.sh                                        # only after changing the C++ (the macOS dylib is committed)
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
$G --headless --path . --quit-after 120                        # runtime errors, generation timing
$G --path . -- --screenshot=/path/out.png --screenshot-frames=90   # real render → PNG, then view it
# extra flags: --demo-sculpt / --demo-paint / --demo-camera (scripted strokes + undo check), --demo-mapfile (save/load round trip),
#   --demo-heightmap (16-bit PNG/RAW export+import round trip), --cam=x,z,distance,pitch,yaw (close-ups), --flat[=height] (empty map),
#   --load=path.csmap, --heightmap=path[,min,max] (import a 16-bit PNG/RAW as a 2 km map), --game (game mode),
#   --preset=island|coast|archipelago|mountains|flat-lowlands|rolling-hills, --seed=n, --show-generator (open the panel),
#   --demo-generate (generator timing, preview vs full, tiling, one-step undo of live updates),
#   --demo-erosion (erosion per preset, repeatability, lakes obey the spill rule, one-step undo), --erode[=light|medium|heavy],
#   --show-erosion (open the Erosion & Lakes panel), --show-materials (open the Materials panel),
#   --rule-debug=<index>|layers|cost (material debug views), --demo-materials, --write-default-look,
#   --demo-scale[=cells] (headless data benchmark at 8193²: generate/stroke/undo/save/load/RAM, then quits),
#   --size=cells (8192 = 28.7 km)
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
| 2 | grass_dirt | Ground037 | ✔ | medium slopes; worn band beyond shore sand (M3.4) |
| 3 | dirt | Ground103 | ✔ | – |
| 4 | gravel | Ground062S | ✔ | scree just below rock, broken up by noise |
| 5 | sand | Ground101 | ✔ | along lakes, rivers and the sea (ground masks, M5.1; was below `sand_height`) |
| 6 | rock | Rock051 | ✔ | steep slopes (gentler where scoured), triplanar |
| 7 | snow | Snow010A | ✔ | paint only (the automatic snow line was removed in M3.4) |

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
- Flatter maps, max slope, lakes (implemented, waiting for the user to test):
  - The hills weight is squared (hills rise out of the lowlands more slowly) and the lowland ripple is lower: ground under
    5° went from 49 → 60% (Rolling Hills), 10 → 24% (Mountains). Mountains peaks are lower (340 → 241 m on 3.6 km).
  - `Max Slope` (default 35°, 90 = off): `HeightMap.LimitSlope` lowers anything steeper (8-neighbour chamfer envelope as
    parallel row/column/diagonal sweeps), before smoothing, preview included. Measured max ~38° (octagon + rounding).
    28.7 km generate 2.2 → 2.6 s.
  - *(Removed in M5.0, replaced by erosion + real lakes)* **Lakes** section: lightning-like **channels** (zero lines of warped fBm, `ChannelDepth/Width/Spacing`; flat bed, banks)
    and **basins** (`BasinAmount/Depth/Size`), both only in the lowlands. On in Rolling Hills, Flat Lowlands and Coast.
    They're dips until M5 water fills them. Channels add ~1.5 s to a 28.7 km fill.
  - Basins are found on the coarse grid (`TerrainGen.FindBasins`: flood-fill labels, level = lowest ground − depth, then a
    chamfer distance carrying each basin's level), so every basin has one **level floor**; the bank blends the land back in.
  - **Gentle Shores** (`GenSettings.GentleShores`, default 60%): a ~1 km shore-style noise makes some shores beaches and
    others steep banks, for the sea (`ShapeMask.Signed` + `TerrainGen.Coast`: beach on land, shelf under water), basin banks
    (12–160 m) and channel banks. Coast shores within 3 m of sea level: under 5° went 19 → 78% (p90 still 8° for cliffs).
  - `--demo-generate` prints slope max / p99 / share under 5° per preset, basin floor/bank and coast shore slopes, and
    checks that channels and basins lower the ground.
- Next: user tests; water (M5) will make Sea Level real; maybe rivers/valley shapes, a "blend with current map" mode,
  and saving generator settings in the map file.

### 🔶 M5.0: Erosion + real lakes (implemented, waiting for the user to test)
The M4.2 channels and basins were noise stamped onto the map, so they didn't follow the ground. Replaced (code, settings,
sliders and preset values removed) by a simulation and by lakes computed from the heights.
- **C++ library** `native/erosion/` (`erosion.h/.cpp`, `build.sh`, credits in `LICENSE.md`), see Tech decisions:
  - `cs_erode`: droplet erosion after Beyer 2015 / Sebastian Lague's Hydraulic-Erosion (MIT). Heights in metres, slopes
    unitless, so settings work at any cell size; speed grows downhill (the reference had the sign flipped). Threads: tiles
    wider than a droplet's reach, coloured in a 3×3 pattern, one colour at a time; a random stream per tile and round,
    so a seed always gives the same map. The border is the base level: erosion fades in over the brush radius + 8 cells
    (without that, sediment carried off the edge cut 90–660 m canyons back into the map). Then a thermal (slump) pass
    that moves material pairwise, so none is lost. Last, **drain hollows** (breaching): from each pit under the
    Priority-Flood fill, follow the flood path over the rim and cut a descending channel if no cell needs more than
    `DrainDepth` (default 2 m). Must run last: sediment filled channels cut before the rain. Up to 3 passes (a channel's
    banks can reshape the hollows next to it).
  - Channel shape (after user feedback: straight trenches with hard corners looked fake): the 8-direction flood path is
    smoothed (24 × 1-2-1) and given a slow meander (±2 cells, 45-cell wave, faded in over 12 cells at each end), then
    carved as a ~2-cell bed falling 1 mm per cell with parabolic banks (30° three cells out) reaching 12 cells (shorter
    reaches leave cliffs where a channel notches a ridge: 8 cells → max slope 85°). All channels go into one surface
    that's applied once per pass; then bank tops are rounded by lowering cells near channels to their 8-neighbour
    average. Tried first: a smooth-min per channel, which lowered shared routes once per channel and dug new pits
    (4 → 13 lakes); the neighbour average can't make a pit.
  - `cs_find_lakes`: Priority-Flood (Barnes 2014) from the border and the sea (ground below sea level connected to the
    border, only when a shape is on), then connected raised cells are lakes; drops those under `MinDepth` (1 m) or
    `MinArea` (0.5 ha). Each lake is level at its spill height.
- **C#** `src/Terrain/Erosion/`: `ErosionSettings` + Light/Medium/Heavy presets, `LakeSettings`, `ErosionSim.Run`,
  `Lakes.Find` → `LakeMap` (level per vertex, NaN = dry; `WaterDepth`, `IsUnderwater`), `Native` (function pointers).
- `Terrain`: finds lakes on a worker 0.5 s after the last height edit (strokes, undo, generate, load, erosion) and drops
  stale results (`HeightVersion`). `GetWaterDepth`/`IsUnderwater` queries, `LakeSettings`, `ShowLakes`, `LakesChanged`.
- `LakeWater` + `shaders/lake_water.gdshader`: flat quads at each lake's level over cells touching water (row runs merged,
  256² cells per mesh), so the terrain cuts the shoreline. Depth-tinted from the depth buffer, soft shore fade, sine
  ripples that fade out by 450 m (they made a moiré further away). No shadows.
- **Erosion & Lakes panel** (bottom bar **Erode**, Map Editor only; closes tools/generator): preset, seed, Rain, Reach,
  Strength, Carry, Width, Drain Hollows, Slump Angle, **Run Erosion** (worker, % and Cancel; dropped if the terrain changed
  meanwhile), then one undo step (`TerrainToolController.ApplyEroded`). Lakes: Show water, Min Depth, Min Area, count/area.
- Generator preview shows lakes too.
- Measured (M1, 3.6 km Rolling Hills): Medium ~1.7 s (droplets ~1 s, the rest is draining), Light 0.9 s, Heavy 4.8 s;
  lakes ~95 ms. Medium: mean change 1.5 m, deepest cut ~20 m, max slope 38 → 56° (p99 37°), under 5° 76%. Lakes before
  erosion: 40 (3.1 km², mostly 1–2 m deep); after Medium: 5 (1.1 km²), linked by drained channels (rivers where they
  run into a lake). Mountains 3.6 km: Medium 1.4 s, 9 lakes (1.2 km²).
- 28.7 km Mountains (M1 8 GB): Medium 89 s (71 s before the channel reshaping), lakes 8 s, peak footprint 3.1 GB. Lakes:
  1432 before (266 km²), 293 after (227 km², 28% of the map, deepest 140 m): the noise makes closed valleys that a 2 m drain can't
  open, so they fill as big valley lakes. Needs a decision (deeper drain on big maps, or generator valleys that drain).
- The old channels/basins numbers in M4.2 no longer apply
  (`--demo-generate` coast shores under 5°: 78 → 66%, the basin floors no longer count).
- Not done / next: rivers (flow accumulation → water along channels), the sea plane (M5), saving lakes settings in the map
  file, a C# fallback or Windows/Linux builds of the library, erosion as a brush, partial re-search of lakes after small edits.

### 🔶 M5.1: Ground masks: sand by water, dirt/gravel where water erodes (implemented, waiting for the user to test)
Texturing from the water, worked out with the lakes (`cs_find_water` in `native/erosion/`, one Priority-Flood for both), so it
follows every edit (strokes, undo, generate, erosion) and needs no saving or undo of its own. Works on un-eroded maps too.
- Flow: multiple flow directions (share ∝ slope²; a single direction left parallel grid stripes), all to the steepest neighbour
  once `GullyMinArea` has gathered; flats and lakes follow the flood tree over the spill point. Walked in reverse flood order.
- Masks, packed RGBA8 per vertex (`LakeMap.Ground`), in Terrain3D's **colour map** (unused by us, already on the GPU):
  R **shore** = distance to lakes/sea/rivers (+4 m per metre above the water; rivers from `RiverMinArea`, 1 km², banks widen
  with catchment); G **gully** = distance to gully/stream beds; B **wear** = log stream power √area × slope;
  A **deposit** = sediment settling where capacity (area × slope) drops: fans at slope feet, deltas at lakes.
- Gully beds start at area × slope² ≥ `GullyMinArea` (10,000 m²) × 0.2², or `GullyMinArea` in a hollow (concave over 2 cells);
  they follow the steepest path down and end on open gentle ground unless they're big. Area alone drew parallel "roads"
  down every smooth plain; without carrying on downstream, flat beds made dashed lines.
- Shader (`WaterAndErosion` group): sand within `shore_sand_width` (10 m) except on rock; gravel in gully beds; dirt from
  `wear_dirt`, gravel from `wear_gravel`, rock at gentler slopes where scoured (`wear_rock`); dirt + gravel patches on
  deposits. `ground_debug` 1–4 shows one mask. The old height rule (`sand_height`) is gone: it put sand on dry lowlands.
- Bridge: every region gets a zeroed RGBA8 colour map (Terrain3D's default white would read as "all shore");
  `PushGround` uploads only regions whose masks changed.
- Cost (M1): 3.6 km lakes 90 → ~180 ms. 28.7 km Mountains: lakes 8 s → 16.5 s (flood 7.4, flow 5.6, chamfer 1.1, rest 1.3),
  on the worker, so textures catch up ~16 s after an edit there. Extra transient memory ≈ 14 B/vertex (~0.9 GB at 8193²).
- Not done: river water surfaces (the beds are dry sand/gravel), `RiverMinArea`/`GullyMinArea` in the Erosion panel,
  partial re-search after small edits (would help the 28.7 km delay).

### 🔶 M3.4: Material rule stack + Materials panel (implemented, waiting for the user to test)
The automatic ground is now data, not shader code, so it can be tuned in the Map Editor. Motivation: hard grass→sand and
grass→gravel edges along lakes, rivers and gullies. The automatic snow line is gone (snow is paint-only).
- **Rules** (`src/Terrain/Look/`): `TerrainLook` (resource, `materials/terrain_look.tres`, one shared file for every map) holds
  an ordered list of `MaterialRule`s, the 8 layer tints, and the height-blend knobs. Each rule lays one layer over the
  ones before it with coverage = strength × condition A × condition B. A condition reads one input (height, relative
  height, slope in degrees, shore distance, gully distance, wear, deposit, or one of four noise fields). It is 1 inside
  [from, to] (either end can be open) and fades out over `Fade`, which is the softness knob. `Edge Noise` moves the
  edges using the fine (~10 m), patchy (~35 m), medium or large field.
- Default look: the original mountain rules ported one to one (the cliff close-up is unchanged), plus softer water.
  Sand fades over 18 m of shore distance with patchy noise, a worn grass & dirt band lies beyond it, dirt fringes the
  gully gravel, and there's a "dry grass up high" rule. Shore distance counts each metre above the water as 4, so
  its fades must be wide.
- **Materials panel** (Map Editor bottom bar). The first version showed the raw rule editor, and the user found it too
  complex. Now it shows plain **cards**: beach sand (width, soft edge, patchy), worn grass near water, gully gravel, dirt along
  gullies, cliffs (starts at, soft edge), grassy dirt on slopes, scree, dry grass, eroded ground. Each card has an on/off
  box and 1–3 sliders that drive fields of the shipped rules, found by `MaterialRule.Id`. Collapsed below: **Colours &
  blending** (tints, height blend, softness) and **Advanced: all rules** (rule list, full rule editor, and a debug View
  showing the selected rule's coverage, the strongest layer, or cost = textures blended per pixel). Footer: Save / Revert /
  Defaults. Edits are live.
- **Performance** (`RuleShaderGen`): `Terrain` draws with a runtime copy of `terrain.gdshader` whose rule block (between
  `@rules-begin`/`@rules-end`, a generic loop in the file) is replaced by straight-line code for the current stack.
  Settled looks are **baked** with the numbers as constants. While editing, it reads them from the `rules[]` uniform
  (no recompiles while dragging) and re-bakes 0.5 s after the last edit. Findings on M1 at 1600×900 (cliff view, baseline 53):
  the generic loop gave 39 FPS; generated code reading uniforms 41–43 (uniform-edged smoothstep costs a division, so
  ramps use a precomputed 1/fade); baked 50–51. A free noise size per condition cost ~2 FPS each, so edge noise reuses
  the shader's existing noise fields. Terrain3D copies the override's code when it's set, so a code change calls
  `set_shader_override` again.
- Remaining cost is the softer look itself: the shore band blends 4 textures (see the Cost view). Shore close-up
  50 → 45 FPS, cliff 53 → 50. Layers that provably can't show in the height blend are no longer sampled.
- Debug flags: `--show-materials`, `--rule-debug=<index>|layers|cost`, `--demo-materials` (live → baked switch, structural
  edit, save/load round trip), `--write-default-look` (writes `TerrainLook.CreateDefault()` to the shared file, then quits).
- Not done: per-map looks and presets (the user chose one shared file), undo in the panel, rules keyed to painted layers.

### ⬜ M5: Water
- Sea level plane with a simple water shader (depth colour, shoreline foam)
- Later: rivers/lakes (flow simulation or painted water sources); buildable = above water
- Water queries in `Terrain`: `IsUnderwater(x, z)`, water depth

### 🔶 M6: Performance & scale (phases 0–2 done; next: phase 3)
Target (user): a 70 × 70 km map on an 8 GB M1. Tiered like CS2: a **28,672 m build area** (8192 cells × 3.5 m, an "8k"
heightmap) sculptable and buildable, centred in a coarse 70 km background (4096 cells ≈ 17 m, scenery only).
Rendering moves to the **Terrain3D** addon (v1.0.2, `addons/terrain_3d/`, MIT; GPU clipmap, 1024² regions). `HeightMap`
stays the source of truth for queries, tools and saves, and Terrain3D is only the render copy.

Phases (tick off as they land):
- [x] **0. Terrain3D spike**: go/no-go on 4.7.2 + 8 GB. Passed, numbers below.
- [x] **1. Data at build-area scale** (engine-agnostic, `src/Terrain/`; implemented, waiting for the user to test)
  - Sizes: `MapSize.All` in `GenSettings.cs` (1.8 / 3.6 / 7.2 / 28.7 km = 512–8192 cells, powers of two), all at
    `GenSettings.DefaultCellSize` = 3.5 m. `--demo-scale` tests the data. (28.7 km was hidden until phase 2.)
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
    slow edges neither stall nor creep. The shader reads these values straight from Terrain3D's control map (phase 2).
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
- [x] **2. Terrain3D renderer** (user tested at 28.7 km: no noticeable delay when editing)
  - `Terrain3DBridge.cs` is the only file with Terrain3D `Call`/`Get`/`Set` strings. It adds a `Terrain3D` child (not owned,
    never saved; collision off, `vertex_spacing` = cell size, `change_region_size` after `AddChild`) and builds each region
    directly (`Terrain3DRegion` + `add_region`, one region-sized image at a time; no 256 MB `import_images` copy).
    `TerrainChunk` is gone, and so are the splat `ImageTexture`s: the shader reads `SplatMap`'s u32s from Terrain3D's control map.
  - **Region size**: an edit re-uploads each touched region's whole map in `update_maps`, and that upload is most of the push
    cost (1024² regions: 3–10 ms for one brush). Terrain3D's region locations run -16..15, so regions are as small as 16 per
    side allows: 256² up to 4k maps, 512² at 28.7 km. The map sits at location (0, 0), so the `Terrain` node must stay at the origin.
  - Push (`Terrain._Process`, priority 100): `MarkDirty`/`MarkSplatDirty` union into one rect each. Per touched region a
    sub-rect `Rf` image is `BlitRect`ed into the region's own Image (edited in place), `update_heights` widens its height
    range, then one `update_maps` per map type and `set_edited(false)`. `ReplaceHeights` pushes everything and then
    `calc_height_range(true)`. The overlay shows "Last push N regions in X ms".
  - Only `cells`² vertices are drawn (decided in phase 1): the last heightmap row and column aren't drawn. Queries, raycasts and `Bounds` still
    use the full map; the skirt's inner loop and the edge fog use the drawn area. Pixels past the map (sizes that aren't a
    region multiple) get the hole bit.
  - Shader: `terrain.gdshader` is now a Terrain3D shader override. Its vertex stage, region lookup and bilinear normals come from
    `addons/terrain_3d/extras/shaders/minimum.gdshader`, and the rest of the look is unchanged. Painted layers come from the 4 surrounding control
    values, decoded and bilinear-weighted. Terrain3D's debug views append code to the override (they expect a `uv`
    local and overwrite ALBEDO). It turns `show_checkered` on when it has no textures of its own, so the bridge turns it off.
  - Uniforms: `Main.tscn`'s `ShaderMaterial` still holds the shader and the tuned values. The bridge copies them onto Terrain3D's
    material when a map is set (and every 0.5 s in the editor). Runtime overlays (brush, anchor, grid, contours, height range)
    go straight to Terrain3D's material.
  - Snow line (decided): `snow_line` (default 0.5) of the way up the height range, never below `snow_height` (125 m). The default
    map is unchanged. On the 28.7 km Mountains map (5–1130 m) only peaks above ~570 m get snow.
  - Skirt: can't use the Terrain3D shader, so it has its own fog-only `terrain_skirt.gdshader` (`SkirtMaterial` export; Terrain
    copies the `edge_fog_*` values into it).
  - 28.7 km is in the menus and generator panel (`MapSize.NeedsTerrain3D`/`Offered` and `--coarse` removed).
  - Measured (M1 8 GB, debug build, 1600×900): default 3.6 km map looks the same as the chunk renderer, whole-map view 19 → 55 FPS,
    close-up 52 → 57. Copy to Terrain3D: 3.6 km ~100 ms, 28.7 km ~2.0 s (plus 2.4 s generate). Push: 216×189 sculpt
    batch 1–4 ms at 3.6 km; 4 regions at a 28.7 km region corner 9.9 ms. 28.7 km Mountains: 46–48 FPS at 700–1,800 m,
    max RSS 1.76 GB, peak footprint 3.96 GB (unified memory, includes GPU).
  - Not done: 60 FPS at 28.7 km (46–48 now); stroke push at 28.7 km region corners (~10 ms for 4 × 1 MB uploads; could
    split a push across frames); load/generate > 2 s at 28.7 km (copy could overlap generation); camera still capped at
    1,800 m (phase 3). Snow and rock look blotchy at 28.7 km (noise sizes were tuned for 2 km); a tuning pass later.
- [ ] **3. 70 km background**
  - `BackgroundMap`: 4097² `HeightMap` over 70 km from the same `TerrainGen` settings (world coords), build area downsampled into the centre.
  - Own coarse ring mesh (17 → 70 → 280 m cells) with a hole for the build area, welded to its border; replaces `TerrainSkirt`.
  - Edge fog moves to the 70 km border; subtle marker on the build border. `CityCamera`: `MaxDistance` → 8–10 km, `Far` → ~80 km,
    pivot stays inside the build area.
- [ ] **4. Measure + document**: extend `--demo-scale` (phase 1: data timings) with Terrain3D push timings and peak RAM. Targets on the M1:
  60 FPS at every zoom, generate < 3 s, load < 2 s, stroke < 4 ms/frame, RAM < 2.5 GB. All `--demo-*` flags still pass at 2 km and 28 km.

The addon was committed as-is (every platform) in c5cf543.

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
- M6 phase 2: Terrain3D regions are 256²/512² (smallest that fits 16 per side), not 1024², because each edit re-uploads whole
  regions. (The relative snow line decided here was removed in M3.4: snow is paint-only.)
- M3.4: ground rules are a data-driven stack in one shared look file (not per map), edited in an in-app Materials panel;
  the shader is specialised (baked) per look for speed. No automatic snow.
- M6 phase 1: new maps use 3.5 m cells (CS2's spacing), replacing the 2 m decision above; sizes are powers of two (1.8–28.7 km).
  Old 2 m maps still load at their own cell size. Paint is two layers per vertex plus coverage (Terrain3D's control format);
  map files store 16-bit heights, like CS2 and heightmap exports.
- M5.1: ground texturing by water is **derived from the current heights** (flow routing + stream power), not recorded from the
  erosion run, so it follows sculpting, needs no file/undo changes and works on un-eroded maps. Stored in Terrain3D's colour map.
- M5.0: lakes are **computed, not generated**: every depression fills to its spill height (Priority-Flood), so water always
  follows the ground. The M4.2 noise channels/basins were removed (user's choice). Erosion is a separate, undoable step run
  from its own panel, not part of the generator. C++ as a plain C library via P/Invoke-style function pointers rather than a
  GDExtension (no C# bindings, needs godot-cpp/scons, copies arrays). Shallow hollows are drained by cutting outlets
  (breaching) at the end of erosion; without it noise terrain turns 17–30% of the map into ponds and valley lakes.
