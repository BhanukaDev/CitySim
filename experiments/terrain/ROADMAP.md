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
- **C++ for water** (since M5.2): same approach, `native/water/`, but **stateful** (a handle owning the grids) and stepped
  continuously by a C# worker thread (`WaterSim`), with a persistent thread pool (`native/common/parallel.h`). Our own
  code: no mature Godot water-sim plugin exists (Waterways only bakes flow maps along splines; Terrain3D/MTerrain have none).
- `native/*/bin/` is **gitignored** (not committed, whatever older notes say): run each `build.sh` after a fresh checkout.
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
native/erosion/build.sh; native/water/build.sh                 # first checkout and after changing the C++ (bin/ is gitignored)
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
$G --headless --path . --quit-after 120                        # runtime errors, generation timing
$G --path . -- --screenshot=/path/out.png --screenshot-frames=90   # real render → PNG, then view it
# --screenshot-zoom=60,120,250 (with --cam): more shots from the same pivot at those distances in one run, sim paused
#   (out_<distance>.png), for checking how things look as the camera zooms
# extra flags: --demo-sculpt / --demo-channel / --demo-paint / --demo-camera (scripted strokes + undo check), --demo-mapfile (save/load round trip),
#   --demo-heightmap (16-bit PNG/RAW export+import round trip), --cam=x,z,distance,pitch,yaw (close-ups; x, z in map metres from the
#   map's corner, as the HUD pivot shows: the world origin is the map's centre), --flat[=height] (empty map),
#   --load=path.csmap, --heightmap=path[,min,max] (import a 16-bit PNG/RAW as a 2 km map), --game (game mode),
#   --preset=island|coast|archipelago|mountains|flat-lowlands|rolling-hills, --seed=n, --show-generator (open the panel),
#   --demo-generate (generator timing, preview vs full, tiling, one-step undo of live updates),
#   --demo-erosion (erosion per preset, repeatability, lakes obey the spill rule, one-step undo), --erode[=light|medium|heavy],
#   --show-erosion (open the Erosion & Lakes panel), --theme=<id> (switch the map's terrain theme), --show-theme (Theme panel),
#   --view=materials|cost|slot:<n>|wetlook (debug views), --demo-themes (paint across theme switches, map file v3/v2),
#   --rain[=intensity] (it starts raining), --wetness=x (whole-map wetness at once), --edge=line|fog|horizon, --sea=level (a Sea source),
#   --demo-water (water self-checks + tool check, then a stream/river/lake on the map, 15 sim-minutes at once),
#   --demo-falls (1.8 km made-up map: plateau sloping 5 %, 25 m cliff, a narrow and a wide fall into a sea; try --cam=1150,1000,160,40,20),
#   --show-water (Water panel), --hide-water, --water-speed=n, --water-run=seconds (simulate that long at once, 2 s in),
#   --water-arrows (flow arrows on), --no-tool (--demo-water ends with no tool out: no flow arrows),
#   --preview-at=x,z,level (the Lake placement preview there), --stream-at=x,z,flow (a Stream source there, e.g. far out),
#   --stream-flow=m³/s (the --demo-water stream's flow, default 40), --pollute=kg/s (the --demo-water stream carries pollutant),
#   --bake-theme=<id>|all (bake a theme's texture arrays, previews and materials.gdshaderinc, then quit; run --import after),
#   --demo-scale[=cells] (headless data benchmark at 8193²: generate/stroke/undo/save/load/RAM, water memory/speed, then quits),
#   --water-cells=n (cap the water grid side; 2048 = the pre-3f 14 m cells on 28.7 km),
#   --size=cells (8192 = 28.7 km),
#   --profile[=frames] (frame avg/p95 + render CPU + draws with vsync off, after the first lake search; quits),
#   --profile-during-search (don't wait for it), --demo-push (push ms per frame mid-region / on a region corner),
#   --demo-lake-window (an edit → window lake search → background full search; --lake-shot=path screenshots it)
# peak memory: /usr/bin/time -l $G ... ("peak memory footprint"); steady: footprint <pid>
# any flag skips the start menu (scenes/Menu.tscn)
tools/fetch_textures.sh      # first time (or after changing a texture): download the ground textures, bake every theme, import
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

### 🔶 M2.1: Channel tool (implemented, waiting for the user to test)
A sculpt tool (Terrain tab, after Slope) that cuts a cross-section along the drag: rivers, canals, ditches, road cuts.
Named Channel, not River: it only shapes ground and never places water (the user places sources).
- Engine-agnostic `ChannelOps` (`src/Terrain/Sculpt/`): `ChannelProfile` (shape, top width, depth), `ChannelPoint`
  (position, reference height). `CarveSegment` sweeps one profile along each path segment, turned across the direction of
  travel, with the reference interpolated along it. **Cut only** by default: keeps the lower of ground and channel, so dips
  below the bed stay and a repeated cut changes nothing. Beyond the top edge, **banks** rise at a set angle (15–85°,
  default 35°) up to 1.5 widths + 3 depths out, so a cut across a hillside isn't a cliff.
- Shapes: **V** `\/`, **U** (parabolic, default), **Flat Bed** `\_/` (bed 40 % of the width), **Box** `|_|` (canal walls).
  Width 4–400 m (`[ ]`/Shift+wheel), depth 0.5–60 m. Shape buttons use SVG icons (`assets/icons/channel_*.svg`, a green
  box carved by the profile; the tool icon is the flat-bed one).
- **Follow Ground** (drag): the reference is the ground from *before the stroke* (`UndoStack.StrokeOriginal`, so the cut
  doesn't feed on itself), averaged across the channel and smoothed along the path over ~1 width. **Downhill Only**
  (default on): the reference never rises along a stroke. **Intensity** 10–100 % (Alt+wheel): lower pulls the ground
  toward the profile by 1 − e^(−4·intensity·dt) per tick. Path points closer than max(cell, width/8) are skipped.
  A second stroke over a channel follows the new, lower ground, so it deepens it. One stroke = one undo step.
- **Runaway fix** (user report: holes much bigger than the ring on a first stroke on flat ground): the cursor raycast hit
  the *carved* ground, so a tilted view's ray passed over the lip onto the far wall, which was cut next, and so on while the
  mouse stood still. During a Channel stroke the cursor now raycasts the pre-stroke ground (`Terrain.Raycast`'s
  `heightAt`). The demo check measured 7.7 m of creep in 2 s at 30° pitch without the fix, 0 with it.
- **Graded** (reworked; no more start/end profiles): LMB sets **A**, LMB sets **B**, LMB again or Enter cuts the straight
  channel A→B on the grade between their ground heights, as one undo step. B then becomes the next A (chaining keeps a
  bent canal on one grade). RMB/Esc steps back (clears B, then A). The preview draws the corridor at full width (shader
  `anchor_band`), and the config panel shows **Points** and a live **Grade** readout: ↑/↓ %, length, rise, the deepest cut
  and the highest wall (`ChannelOps.Measure`). Intensity and Downhill Only are hidden in Graded.
- **Fill** (Graded only, default off): the bed is laid exactly (dips filled), walls with a flat crest (max(2 cells, 10 %
  width)) stand at the grade, and embankments slope down from them at the Banks angle. Reach = crest + 2 × max fill /
  tan(bank); past that an embankment on a steep hillside ends in a cliff.
- `--demo-channel`: a winding downhill U river (bed never rises, reaches depth); a still tilted ray held 120 ticks (no creep);
  a graded 30 m flat bed across the river: cut-only keeps the dip, Fill lays the bed on the grade (±5 cm) with walls up to it,
  a repeat is a no-op, undo/redo; plus Follow Ground undo/redo and 30 % intensity (prints `Demo channel: … ok`). Ends in
  Graded, previewing a next segment from B.
- Next: user tests; editable paths once roads/networks exist (canals as a network, see M7).

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
  preview (hillshade, height tint) redrawn on every change (4–12 ms). Since M2.1 it shows **ground only**, like the 3D map
  (no water is placed): below sea level is seabed (sand → grey-brown with depth) with a blue **shore line** at sea level,
  where a Sea source would fill to; hollows are no longer tinted as lakes (and aren't searched, so it's faster). **Live** regenerates the 3D
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
- *(Replaced in M5.2 by the simulated water)* `LakeWater` + `shaders/lake_water.gdshader`: flat quads at each lake's level over cells touching water (row runs merged,
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
  ~~partial re-search after small edits~~ (done in M6 3c step 3: window searches, 9–36 ms at 28.7 km).

### 🔶 M3.4: Material rule stack + Materials panel (replaced by M3.5 themes; the rules live on as the default theme's shader + slots)
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

### 🔶 M3.5: Terrain themes made in Godot (implemented, waiting for the user to test)
The Materials panel edited a fixed world (8 hard-coded layers, one texture array, one shared look), so a winter map was
impossible and the in-game rule editor was clunky. Now a **theme** is made in the Godot editor (for us, gamedevs and later
modders) and bundles its own shader, materials and erosion slots. Nothing about grass/sand/rock is built into the game.
Authoring guide: `terrain_sdk/README.md`.
- **Shader contract** (`terrain_sdk/`): `terrain_core.gdshaderinc` (Terrain3D vertex stage and lookups, uniforms, sampling,
  `TerrainInputs`, helpers `lay`/`above`/`below`) and `terrain_fragment.gdshaderinc` (`fragment()`: base → `terrain_auto` →
  scour/deposit slots → optional `terrain_auto_late` → shore/stream slots → paint → top-4 height blend → overlays, fog; default
  `light()`). A theme shader only includes them and writes hooks, so the engine side can change without breaking themes.
  Terrain3D copies the override's code into a pathless shader, so `Terrain` makes relative `#include`s absolute first.
- **Data** (`src/Terrain/Themes/`): `TerrainTheme` (`themes/<id>/theme.tres`: id, name, `ShaderMaterial` with tuned uniforms,
  up to 16 `TerrainMaterial`s, 8 `ErosionSlot`s), `TerrainMaterial` (source texture *paths*, so the game never loads the
  2K sources; tint, tiling, triplanar, detail/normal factors, paintable), `ErosionSlot` (material or empty = off; strength,
  edge, fade, noise, slope limit; defaults are the M5.1 tuned values), `ThemeLibrary` (scans `res://themes/*/theme.tres`),
  `ThemeBaker` (the old TextureBaker's grey-normalise/height-stretch into `baked/albedo_height.png` + `normal.png` with BC7
  texture-array `.import`s, a CPU-lit 128² preview per material, and `materials.gdshaderinc` with `MAT_<ID>`/`SLOT_<NAME>`).
- **Slots** (erosion/water features a shader can't infer): Deposits, Thick deposits, Scoured, Heavily scoured (wear), Shore
  fringe, Shore (shore distance), Stream banks, Stream beds (gully distance). Filled slots are `#define`d (empty ones cost
  nothing); their numbers are uniforms, so they tune live.
- **Per map**: `MapFile` v3 appends the theme id and a palette (material id per painted index). `SplatMap.Remap` moves paint
  by id when the theme changes; ids the new theme lacks stay at the end of the palette (the shader skips indices past the
  theme's count), so switching to winter and back loses nothing. v1/v2 files load as `default` with the old 8 layer ids.
  New Map has a Theme dropdown (`MapSession.NewMapTheme`).
- **UI**: Materials tab → small **Theme** panel (theme dropdown, material swatches, which slots are on, debug views, Reload).
  Paint tab buttons come from the theme's paintable materials with their baked swatches.
- **Godot tooling**: `addons/citysim_themes/` (enabled in project.godot): Bake theme + Validate buttons in a `TerrainTheme`'s
  inspector. In the editor, setting `Terrain.DefaultTheme` shows that theme in the viewport, refreshed every 0.5 s.
- Content: `themes/default` (the M3.4 look ported exactly: screenshots match the old shader to within 3/255 per channel),
  `themes/winter` (demo: snow base, drifted snow, wind-scoured slopes with frozen grass/earth, snow on rock ledges, gravel
  shores, ice stream beds, deposits/fringe/banks empty, cooler lighting). New CC0 sets: Snow004, Ice002.
- Removed: `TerrainLook`/`MaterialRule`/`RuleCondition`/`RuleShaderGen`, `MaterialsPanel`, `TerrainLayers`,
  `materials/terrain_look.tres`, `shaders/terrain.gdshader`, flags `--show-materials`, `--rule-debug`, `--demo-materials`,
  `--write-default-look`, `--bake-terrain-textures`.
- Not done / next (**M3.6**): loading themes from mod `.pck` files in `user://mods`, a modder template project, frozen lake
  water (lakes still use one water shader for every theme), a theme preview button that launches the game on the theme.

### 🔶 M3.7: Wet ground look + rain (implemented, waiting for the user to test)
Wet ground looked the same as dry ground apart from the Wet slot's material swap (one matte roughness of 0.9, and a
`light()` with diffuse only). It now reads darker, richer and smoother, and catches the sun. The user asked for a
**global wetness** too, so rain can wet the whole map.
- **`t.wet_look`** (new in `TerrainInputs`, 0–1): max(wet paint or the waterline band × `wet_amount`, rain). The damp bank
  beside the water is `wet_shore_band` m (16; fully wet for the first 20 %, then a long fade, noisy edge) of the sim's distance
  to water (each metre above the water counts 4, so steep banks stay narrow). At 3 m it sat almost entirely under the
  water's edge and couldn't be seen (user); 12 m still barely showed; 25 m covered the whole sand strip, so no dry sand
  was left (user). 16 m leaves dry sand at the grass side. Rain is `global_wetness`, a little stronger on flat
  ground. The Wet *slot* still uses `t.wet` only, so rain doesn't turn the map to sand.
- **Look** (`terrain_fragment.gdshaderinc`): saturation +`wet_saturation` (0.3), albedo × (1 − `wet_darken` 0.5),
  roughness → `wet_roughness` (0.5), specular 0.2 → `wet_specular` (0.3). The fine noise jitters the roughness
  (`glint_sparkle`), so the glint glitters in patches. First try (0.35 darken, 0.35 roughness, 0.5 specular): the sky
  reflection cancelled the darkening (only ~11 % darker on screen), so it's more matte and darker now.
- **Sun glint** in the default `light()`: a toon highlight (`smoothstep` on N·H^`glint_size` 150, `glint_strength` 0.8),
  only where roughness < 0.6, added to `SPECULAR_LIGHT`.
- **Puddles** once `global_wetness` > `puddle_start` (0.55): patches (patchy noise) on flat (< `puddle_max_slope` 4°), low
  ground (near gully/stream beds or water, not hilltops: the first try put them on flat hilltops, where they read as frost),
  tinted toward `puddle_color`, darker, roughness 0.12. Faded out past 4 × the far blend distance (no specks far away).
- **Rain** (`src/Water/Weather.cs`, engine-agnostic): `Raining`, `Intensity`, wetness rising over `RampMinutes` (10) and
  drying over `DryHours` (2), on **sim time** (paused/sped with the water sim). `Terrain.Weather`, pushed as
  `global_wetness` when it changes. Look only (no water in the sim yet; that's M5.3's rain event) and **not saved** with the map.
  Water panel: **Rain** switch + **Intensity** slider, wetness in the stats.
- `Terrain.WetShine` → `wet_shine` (off: darker but matte, no glint): the "Wet ground shine" graphics setting.
- Themes: winter has less darkening, crisper icy glints and a pale puddle colour. SDK README lists the WetGround group.
- Debug: `--rain[=intensity]`, `--wetness=x`, `--view=wetlook` (`ground_debug` 6).
- Checked: dry ground renders as before (max 2/255 difference against the previous commit); `--demo-water`,
  `--demo-themes` ok. Screenshots at wetness 0 / 0.5 / 1 (`--cam=1720,1900,90,30,200`, `--cam=1300,700,120,20,290` for
  the glint). Cost not measured precisely (FPS in windowed screenshot runs is too noisy); no new texture samples.
- Not done: SSR (not planned), rain drops/streaks on screen, darker sky and cloud shadows while it rains.

### Graphics settings (plan for the game)
The experiment tries visual features; the game exposes the optional ones in a **Graphics settings** menu so players
can switch them off (or pick a quality) on weaker hardware. Build each optional effect so it can be switched from the start:
- A `bool`/`int` uniform (or a shader `#define` variant when an off switch should also drop the cost entirely), read
  from one settings object, never hard-wired per scene.
- Off must look fine, just plainer (no holes, no broken blends), and switching must work while the game runs.
- Note each feature's measured cost here when it lands, so the menu can say what it costs.
- Three kinds of setting:
  - **Bool** (on/off): the effect is there or not.
  - **Enum level** (Off / Low / Medium / High, or a feature's own names): the same effect at several costs.
  - **Number** (slider): continuous trade-offs such as draw distances.
- An overall **preset** (Low / Medium / High / Custom) only sets the individual settings. Changing one by hand makes it
  Custom.

Candidates so far (tick when the switch exists):
- [x] Wet ground shine (M3.7): bool (`Terrain.WetShine`; no menu yet).
- [ ] Water quality (M5.4–M5.6): enum. Low = no caustics, no white-water streaks; High = everything. The far-fall bias
  stays on at every level (it fixes a bug, it isn't an effect).
- [ ] Water mesh detail: enum on the near LOD (1 or 2 vertices per cell, M5.4).
- [ ] Horizon ring (M6 3e): bool, off = the Fog edge (`Terrain.EdgeStyle`).
- [ ] Edge fog (M3.1, M6 phase 3a): enum, Flat / Animated billows. (The on/off per mode from 3a is a mode rule, not a setting.)
- [ ] Effect draw distance (caustics, glints): number, later if needed.
- [ ] Flow arrows are an editor aid, not a graphics setting (stay a tool option).

### 🔶 M5: Water (M5.0/M5.1 lakes + masks, M5.2 simulation, M5.4 look + sim performance, M5.5 sources-only water done; M5.3 next)
- Sea level: a Sea source (M5.2). Buildable = above water: `Terrain.IsUnderwater`/`GetWaterDepth` (M5.2).

### 🔶 M5.2: Water simulation + Water tab (implemented, waiting for the user to test)
Water flows over the terrain and reacts to edits, placed as sources like the CS2 **Water Features** mod (yenyang).
The static M5.0 lakes (flat quads over every hollow) are gone: water is simulated; hollows are only *filled* once.
- **Sim** (`native/water/`, C++): shallow water with **virtual pipes** (O'Brien & Hodgins 1995, Mei et al. 2007) on the
  terrain's vertex grid (every 2nd vertex at 28.7 km since M6 phase 3f: `WaterSim.MaxCells` 4096; sparse tiles). Pipe cross-section = cell × depth (the
  classic cell² made thin sheets race at the speed cap), Manning-like bed friction (n 0.03, d^1.5), flux damping
  0.2/s, velocity capped at Froude 3 (thin cells read absurd speeds otherwise, and the CFL limit followed them), CFL
  substeps (≤ 0.5 s). Only 64² tiles that are wet, next to wet ones or under a source are stepped. Evaporation (mm per
  sim-minute) dries unfed water; open map edges drain it (closed under sources, so a border river feeds the map).
- **Sources** (`WaterSource`): **Stream** (constant m³/s), **River** (holds a level, water flows in or out; snaps to the
  border within radius + 30 m), **Lake** (fills to its level at up to Max Flow, never drains), **Sea** (holds every border
  cell below sea level at sea level; one per map). *(Since M2.1 no source is placed automatically, see Decisions.)*
- **C#** (`src/Water/`): `WaterSim` owns the handle and a worker thread (Speed × real time, backlog dropped when the CPU
  can't keep up, paused with the tree), queues main-thread changes (ground rects from `Terrain.MarkDirty`, sources,
  settings, fill/clear) and publishes a snapshot ~30×/s (display surface, depth, velocity per cell; dirty 256² pages).
  Queries: `Terrain.GetWaterDepth/IsUnderwater/GetWaterSurface/GetWaterVelocity` (bilinear on the snapshot).
- *(Replaced in M5.5 by Add Lake Sources)* **Fill Hollows**: raised the water to the Priority-Flood lake levels (`LakeMap.Level`, Erosion panel Min Depth/Area) and
  floods the sea from the border. Rivers/lakes aren't flood-filled (tried: a high river drowned the whole map). New maps
  and pre-v4 files fill once when the hollow search finishes, so they look as before.
- **Drawing** (`WaterSurface`, `shaders/water.gdshader`): per 256² page an RGBA32F texture (uploaded ≤ 20 Hz, dirty pages
  only); per 64² tile a grid mesh (LOD step 1/2/4/8 by distance) displaced in the vertex shader, drawn only while wet.
  Dry cells next to water reach into the bank (the ground cuts the shoreline), others are discarded. Depth tint and
  shore fade as before, two-phase flow-map ripples along the velocity, foam on fast/steep water, films < ~5 cm and the
  last 250 m before the map edge fade out.
- **Editor**: Terrain → **Water** tab (Stream, River, Lake, Sea; `WaterSourceTool`): LMB places, LMB on a source selects
  + drag moves, RMB on a source removes, RMB on ground picks the target elevation (sea level for Sea); config rows Radius,
  Flow Rate, Depth, Target Level (Auto), Max Flow, Sea Level, Snapping edit the selected source. Every source edit is one
  undo step (`UndoStack.PushAction`). Rings + posts per source (`WaterSourceMarkers`, through the ground). Bottom-bar
  **Water** panel: Run/Pause, Speed ×1–×32 (default ×8), Evaporation, Map Edges, Show water, Fill Hollows, Clear Water,
  stats. It can stay open next to the tools. The Erosion panel's Lakes section is now "Hollows" (no Show water).
- **Map file v4**: water section (`WaterFile`): settings, sources, depth per cell as zlib'd half floats (non-empty 256² tiles).
- Measured (M1, sim on half the cores): substep 1025² fully wet **5.2 ms**, 2049² **21 ms**. Default 3.6 km map with filled
  hollows + a stream/river/lake at ×8: ~4 ms/substep, keeps up at ×8. FPS at the 450 m close-up: 44 (no water drawn, sim idle)
  → 41 (sim running) → 36 (water drawn).
- `--demo-water` checks: volume conserved (walled bowl), settles flat, stream runs downhill off the edge, level source holds
  15.000 m, unfed pond evaporates, Fill Hollows = Priority-Flood volume, a dam (terrain edit) backs water up 0.8 → 4 m,
  map file round trip, tool snap/undo/redo/remove.
- Not done / next: LOD seams between tiles of different steps (small cracks possible), `ReplaceHeights` (generator,
  erosion apply) keeps the old water (use Clear + Fill Hollows), shore sand still follows the hollows not the simulated
  water, source edits of values push one undo step per click, Windows/Linux builds, SIMD for the step.

### 🔶 M5.4: Stylized water, fixed ticks, sleeping tiles, pollution (implemented, waiting for the user to test)
The user found the water too light, shiny and see-through: shallow eroded streams looked like the ground. The look is now
stylized (Ghibli-like) and the sim got the performance items from the user's list.
- **Look** (`shaders/water.gdshader`, material `materials/water.tres`, tuned in the Godot inspector):
  - Colour from absorption, not alpha: the ground behind (screen texture, refracted by the ripples) fades into
    `water_tint` (teal-green) then `deep_color` (1 − e^(−depth × absorption), absorption 2.5/m: 30 cm is half tinted, 1 m
    92 %). `min_tint` 0.4 for any drawn water, soft toon `color_bands`. Depth = max(screen depth, sim depth), so a stream
    over sand is tinted to its banks.
  - Low sky reflection (`reflection` 0.22), toon-lit, crisp sun sparkles only on sparse spots (`glint_density`).
  - Voronoi caustics on the bed (0.05–3 m deep, near the camera).
  - Foam: a crisp shore band plus lines pulsing out, only on real water bodies (sim depth > ~0.3 m, not slope films);
    rapids foam streaked along the flow; breakers rolling to the shore over calm shallows; sparse wind-aligned whitecaps
    on deep water. Foam glows a little so it stays white.
  - Pollution tints the water murky brown and hides the bed and caustics (`pollution_color`, `pollution_full` 0.05 kg/m³).
- **Finer than the sim grid**: near tiles have 2 vertices per cell; the surface, depth and flow are sampled bilinearly
  (ignoring hidden cells), and pixels are cut where the interpolated shoreline crosses, so shorelines are round, not stair-stepped.
  Ripples are a scrolling `NoiseTexture2D` normal map dragged along the sim velocity (two-phase flow map).
- **Fixed ticks** (`WaterSim.TickSeconds` = 1 s): the worker steps whole ticks (Speed × 1 tick/s: ×8 = 8 Hz) and drops whole
  ticks when the CPU can't keep up. Within a tick the CFL substeps are split evenly. Queued edits land between ticks. After
  each tick a snapshot is published; `WaterSurface` keeps the previous and latest tick per page (textures made lazily when
  a page first gets water) and blends between them, so the water moves smoothly. 0.5 s ticks were tried: rounding up to
  whole substeps cost ~50 % more substeps; 1 s costs ~14 %.
- **Sleeping tiles** (`native/water/`): after 4 calm ticks (depth change < 0.5 mm per tick, no cell carrying more than
  0.002 m²/s) a 64² tile sleeps: flows zeroed, not stepped. Stepped = awake + stream tiles + one ring; pipes from a stepped
  tile into one that isn't are closed, so no water is lost. Wakes on ground edits, source changes, fills, or when it's in the
  ring and changes. Evaporation and decay are caught up on waking (or every 60 s). The first criterion was speed < 2 cm/s,
  but films trickling down slopes never got that slow; depth × speed lets them sleep while steady rivers stay awake.
  The CFL limit only counts stepped tiles, so a deep sleeping lake no longer shortens a river's substeps.
- **Determinism**: a run depends only on the state, the tick and queued changes: same hash twice and on 1 vs many threads
  (`--demo-water`). `-ffp-contract=off` so FMA fusion can't differ between arm64 and x86_64 builds. Outflow was already
  clamped (a cell's pipes are scaled to what it holds); `ClampHits` counts violations: 0 in every check.
- **Pollution**: pollutant mass per cell, carried by the same pipe flows (each pipe at its cell's concentration), so mass is
  conserved; `PollutionHalfLifeMin` (default 30) decays it; lost when a cell dries or leaves the map. Streams have a
  Pollution rate (kg/s; Stream tool row "Pollution"). Skipped entirely while there's none. `Terrain.GetWaterPollution`.
  Water panel shows pollutant kg and active/sleeping tiles.
- **Map file**: water section v2 (the map file stays v4): half-life setting, pollution per source, pollutant grid (zlib'd f32
  tiles). Section v1 still loads.
- **Narrow water** (measured, `--demo-water`): a 7 m wide, 1.5 m deep channel keeps all its water on 3.5 m cells but loses
  7.6 % over the banks on 14 m cells (the 28.7 km map); 14 m wide channels are fine on both. Mitigated in rendering only;
  a finer grid is the follow-up below.
- Measured: filled hollows on the default map sleep entirely (74 tiles, 0 stepped, 0 ms). Demo scene (40 m³/s mountain
  stream, border river, lake source) ~94 stepped tiles, 1.7 ms/substep. 28.7 km map, same demo: 39 stepped tiles at
  0.7 ms/substep on the 14 m grid. Fully wet worst case (nothing sleeps): ~8 % slower per substep than M5.2 (6.6 → 7.1 ms at
  1025², standalone bench). Screenshot FPS at close lake/stream views: 38–39 → 32–34 (the new shader); overview 38 → 37.
- Not done / next: ~~a finer water grid for narrow streams on big maps~~ (done in M6 phase 3f); per-theme water materials (winter: icy water); pollution diffusion and ground deposits;
  the shader's cost at close range (FPS above) if it matters on target hardware.

### 🔶 M5.5: Water from sources only, wet ground, flow arrows, flow foam (implemented, waiting for the user to test)
Fill Hollows put water in every hollow that belonged to nothing, so the user couldn't remove or tune a lake. Now every drop
comes from a source the user owns.
- **Lake sources instead of filled hollows** (`src/Water/LakeSources.cs`, engine-agnostic): each Priority-Flood lake
  (`LakeMap`, Erosion panel Min Depth/Area) gets a Lake source at its most open water (chamfer distance to the shore, deepest
  on a tie), radius ½ that distance (1.5 water cells … 150 m), level = spill height, Max Flow = max(volume / 600 s, 3 × area ×
  evaporation, 1 m³/s). Hollows that already hold a Lake/River source are skipped, so running it again adds only what's missing.
  The planned lakes are filled at once (`WaterSim.RaiseTo`, only their cells) and the sea as before. *(Since M2.1 new maps and
  old files no longer do this automatically.)* The Water panel's **Add Lake Sources** does it as one
  undo step (`TerrainToolController.AddLakeSources`; undo removes the sources and drains their lakes).
- **Deleting a Lake source drains its lake** (`cs_water_drain`): from the source's cells, every 4-connected wet cell below its
  level whose surface is within 0.3 m of the source's; rivers running out of it sit lower and are kept (they dry up). Undo puts
  the source back and raises the water to the drained surface. River/Stream/Sea removal is unchanged.
- **Wet ground paint** (`native/water/`): per water cell 0..1, rising over `PaintMinutes` (default 20 sim-min) under water
  deeper than 5 cm (half rate on dry cells next to it, so it reaches the waterline), fading over `PaintFadeHours` (default 24,
  0 = never). Sleeping tiles paint in one go every 60 s. `cs_water_read_ground` packs it with the **distance to water deeper
  than 25 cm** (chamfer; +4 m per metre above the nearest surface, like M5.1) as RG8; read ≤ 1×/s on the worker, uploaded by
  `Terrain.PushWaterGround` as `water_ground`. The shader's `t.shore` is now min(river beds from the M5.1 mask, that distance)
  and `t.wet` is new; `cs_find_water`'s shore channel no longer seeds from lakes or the sea (a hollow with no lake source stays
  dry ground). The 25 cm cut keeps beaches off thin sheets and mountain streams (tried 5 cm: a 60 m sand band down the demo stream).
- **Theme slot "Wet ground"** (`ErosionSlotKind.Wet`, `SLOT_WET`, appended last so saved themes don't shift; shader arrays 8 → 9):
  default theme sand, winter gravel; defaults edge 0.5, fade 0.4, patchy noise 0.25, max slope 42°. `ground_debug` 5 / `--view=slot:8`.
  Water panel: Wet Paint (min) and Paint Fades (h) sliders. Map file water section v3 stores the paint (zlib'd bytes per 256² tile)
  and both settings; v1/v2 still load.
- **Flow arrows** (`src/Terrain/WaterFlowArrows.cs`): a MultiMesh of flat arrows on a grid around where the camera looks,
  spacing = camera distance / 20 (2 water cells … 200 m, ≤ 3600), refreshed 5×/s from the snapshot; length and colour (blue →
  yellow → red) by speed up to 3 m/s. On while a Water tool is out, or always with the Water panel's **Flow arrows** checkbox.
- **Flow foam** (removed in M5.6) (`shaders/water.gdshader`, group Flow_foam): foam lanes along the flow (two-phase flow map, stretched ×6, in
  drifting patches) and broken crests across it moving at the water speed, from 0.1 m/s (full at 0.8) on water deeper than
  ~0.2 m. Rapids foam above 2 m/s is unchanged.
- **Hover previews** (user asked mid-work): with a Lake/River/Sea tool, the area the source would flood is tinted
  (`WaterFlood` on a worker: Priority-Flood from the map edge, cached per height edit; the hollow under the source's lowest
  cell fills to min(level, spill height); Sea floods from the border) with a label: area and volume, or orange with "spills over
  at …" when the level is above the rim, or "no hollow here: the water runs downhill". Hovering a source shows a label with its
  kind, level, depth, flow/max flow and pollution (`WaterSourceMarkers.ShowInfo`).
- `--demo-water` new checks: lake sources (one per hollow, volume = Priority-Flood, kept after 30 min with evaporation, none added
  on a second pass), drain on delete (only its lake; restore gives the volume back), wet paint + ground marks, flood preview
  (below the rim fills to the level with the analytic volume; above it spills at the rim), map file keeps the paint. All ok.
- Default map: 40 hollows → 39–40 lake sources in ~15 ms (after the 180 ms hollow search).
- Not done / next: the flood preview is hidden under existing water when its level is lower; the demo's `RunFor` batches delay
  new lakes' fill until they finish (demo only); a River's preview uses the lake rule (its water really runs on downhill).

### 🔶 M5.6: White water, far falls, flow foam removed (implemented, waiting for the user to test)
The user found fast water a white blob, the M5.5 flow foam static and bug-like, and far falls sinking into the terrain.
- **White water** (`shaders/water.gdshader`, group White_water): rapids and falls turn milky (`whitewater_color`,
  `whitewater_tint`) with white streaks stretched along the flow (`foam_streak` 6) and carried by the two-phase flow map,
  covering at most `whitewater_coverage` (0.5), never a solid sheet. Streak contrast is restored during the cross-fade.
  Thin water pouring down a fall counts by its flux (depth × speed) for the film fade and the white-water gate, so a
  cliff face shows a curtain instead of nothing.
- **Flow foam removed** (user: not wanted). The M5.5 lanes/crests and the per-cell crests tried here are gone; the
  flow reads from the white water, the breakers and the flow arrows. Lesson if it comes back: `dot(world, dir) − TIME × speed`
  with a per-pixel dir and speed shears without bound as TIME grows; draw per cell with a rigid flow and cross-fade.
- **Far falls**: past `far_bias_start` (250 m) the vertex shader pulls the water toward the camera along the view ray by
  `far_bias` (1.2 %) of the distance (same pixel, nearer depth; the fragment keeps the true position), and there the
  simulated depth stands in when the coarse ground LOD sits above the water. Coarse water tiles (step ≥ 2) take the
  highest surface around each vertex, so the chord across a fall's lip doesn't cut into the ground.
- **Close-up falls and streams** (user: falls and flowing water break up close, near first person). Three causes:
  (1) a fall's sheet is a few cm thick, so up close it loses the depth test to the full-detail terrain triangles; now all
  water is pulled `near_bias` (0.3 m) toward the camera along the view ray, on top of the far bias. (2) Seen side-on, the
  ground right behind sloped water is level with it, so the screen depth read ~0 and the sheet faded out; sloped water
  (surface slope 0.05 → 0.3) now also trusts the simulated depth. (3) The white-water streaks were rotated to each
  pixel's flow around the world origin, so small direction changes fanned them into fine lines with seams; now per
  `whitewater_cell` (8 m) cell, rigid at the cell centre's flow, cross-faded, with `textureGrad` so cell borders don't
  draw mip hairlines. Also the camera (`CityCamera.RequiredLift`) now keeps itself above the water surface: under it
  the one-sided water vanished and only the bed showed.
- **Small streams no longer break into blobs** (user: a small shallow river shows as blobs and falls). The sim water was
  continuous (a 1–5 cm sheet at ~1 m/s; checked with a depth debug colour), but the film fade (2–8 cm) and the shore
  fade (screen depth, 12 cm) hid everything except the pools. Now water carrying `stream_flux` (depth × speed, 0.001 →
  0.008 m²/s) shows anyway, coloured as if `stream_depth` (0.3 m) deep. Side effect: thin spread-out edges of bigger
  streams show too (they were always simulated). What's left: small real gaps in the simulated sheet itself.
  `--stream-flow=m³/s` sets the `--demo-water` stream's flow (default 40; try 2); `--demo-falls` has a small 2 m³/s
  stream in a shallow channel at x = 300 too.
- **Roll waves in shallow streams** (user: a staircase of crescent pools with white fronts). A real instability of
  shallow-water flow with Manning friction above Froude ~1.5 (≈2 for Chezy; see Balmforth & Mandre 2004, "Dynamics of
  roll waves"). With n = 0.03 everywhere a 3 cm sheet on a 10 % slope ran ~1 m/s, Froude ~1.9. Now shallow water is
  rougher (`kSheetManning` 0.15 in `native/water/water.cpp`, as for overland/sheet flow in hydrology, full at 5 cm,
  fading to `Manning` by 30 cm): the same sheet runs ~0.2 m/s, Froude ~0.4, and is ~2.5× deeper. All `--demo-water`
  checks still pass. Rebuild the native lib (`native/water/build.sh`).
- Not done: low water still fills a flat valley floor from bank to bank. It's the ground, not the water: nothing is
  narrower than a 3.5 m cell, so a small stream has no channel to stay in. Fix would be on the terrain side (carve or
  erode a bed under running water), not in the sim.
- `--demo-falls`: test scene for all of this. FPS at the demo stream close-up: 30 before, 32 after (noise).
- Known, not from this change: a cross-hatch on thin sheets running down slopes (water and terrain triangles crossing).
  Caustics still shear the same way (`flow × TIME`); barely visible.

### 🔶 M5.7: Water looks the same at every zoom (implemented, waiting for the user to test)
User: shallow water (streams, small rivers) changed colour and shape a lot with camera distance, and looked ugly.
- Cause: `water.gdshader` took the depth from the ground in the depth buffer. Terrain3D draws the ground coarser with
  distance (and the water mesh coarsens too), so on water a few cm deep the error was bigger than the depth: dark teal shards
  on slopes, grey sheets or no water, all changing with zoom. Now the depth is the simulated depth (bilinear), and the slanted
  path is the depth over the view angle (capped at 5×). The screen depth only guards the refraction. The far/slope/lake-shore
  workarounds for the screen depth are gone.
- Caustics follow the real simulated depth (`caustic_depth` 0.2–3 m) instead of the 0.3 m stand-in that small streams get:
  on thin sheets they looked like cracked mud.
- Checked with `--screenshot-zoom` at 30/60/120/250/500 m over the `--demo-water --stream-flow=4 --erode` stream and the falls.
- Not done: thin sheets spread over a valley floor still read as flat grey-teal (it's the sim's water, see M5.6).

### ⬜ M5.3: Water events and structures (next)
- Waterfalls: curtain mesh + mist where flux crosses a big drop; foam already appears on steep/fast water.
- Floods (hydrograph on a source, rain event), tsunami (travelling level pulse on the Sea), tides (sine on the sea),
  seasonal streams, detention/retention basins (Lake min/max), a water clean-up burst (evaporation × N).
- Dams: an obstacle height layer in the sim (`cs_water_set_obstacles`), gates later. Buildings: damage from depth/velocity.
- ~~Ground masks from the simulated water (sand along real rivers).~~ Done in M5.5 (wet paint, shore distance).

### 🔶 M6: Performance & scale (phases 0–2 done; phase 3: 3a, 3b, 3c, 3d, 3e, 3f, 3h done; next 3g)
Target (user, revised 2026-09-28): the map **stops at 28,672 m** (8192 cells × 3.5 m, an "8k" heightmap), sculptable and
buildable, on an 8 GB M1. **No 70 km background**: 28.7 km is already ~2× CS2's buildable side (4096 × 3.5 m ≈ 14.3 km),
4× its area. The effort goes into quality and performance at 28.7 km instead (phase 3). Why 28.7 km: 3.5 m is CS2's
spacing, and 8192 is the biggest power of two (whole Terrain3D regions) that fits the memory budget; 16384 (57 km) doesn't.
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
    empty background slot (was for the dropped 70 km background; unused). Reads v1 (weights → two heaviest layers). 16-bit steps: 7.6 mm per 500 m of range.
  - Heightmap size (decided): keep `HeightMap` at cells + 1 = 8193². Phase 2 drops the last row/column in the Terrain3D copy
    (8192² px = exactly 8×8 regions); edge fog and the skirt cover that 3.5 m strip. Keeps the generator, files, queries
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
    side allows: 256² up to 4k maps, 512² at 28.7 km. *(Since phase 3b the map is centred on the world origin: regions start at
    location -n/2 and the `Terrain` node moves itself to the map's corner.)*
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
- ~~**70 km background**~~ (the old phase 3) dropped 2026-09-28 (see decision log). It was: a 4097² `BackgroundMap` over 70 km, a
  coarse ring mesh replacing `TerrainSkirt`, `MaxDistance` 8–10 km, `Far` ~80 km.
- [ ] **3. Polish at 28.7 km** (replaces the background). Independent items, meant to be done **one per session**, roughly in
  this order. Tick each off with its own notes and numbers.
  - [x] **3a. Edge fog per mode** (implemented, waiting for the user to test). `Terrain.EdgeFog`: on in Game mode (and in
    the Godot editor, for theme previews), off in the Map Editor; display only, not saved. Off hides the skirt and sets
    `edge_fog_enabled` false, and the shader draws a thin line on the border instead (square corners, `edge_line_color`,
    `edge_line_pixels` 2 px wide at any distance; new SDK uniforms). Map Editor bottom bar: **Edge Fog** toggle to preview.
    `edge_fog_enabled` is a runtime param, so a theme can't turn it back on. Check: `--cam=260,260,450,30,45` with and
    without `--game`.
  - [x] **3b. Float precision check, then centre the map** (implemented, waiting for the user to test). No dynamic
    floating origin (decided, see log).
    - **Check** (before centring, 28.7 km Rolling Hills): close-ups at 15 m and 40 m of the terrain at (500, 500) and
      (28200, 28200), magnified crops of the nearest ground, and a 30 m³/s stream (`--stream-at`) seen from 25 m and 120 m
      at (28150, 28150) vs (600, 600). **Nothing visible**: texture sharpness, white-water streaks and shorelines match. The
      angular shallow-sheet shorelines show near the origin too (7 m water cells), so they aren't precision. No UV wrapping
      needed. Expected: a float at 28 km is ≈2 mm; a 1024² texture over a few metres is a few mm per texel.
    - **Centred**: `Terrain3DBridge.Origin(map)` = −(regions / 2) × region size per axis (28.7 km: −14,336 m, locations
      −8..7; 3.6 km: −1,792 m). `Terrain.SetMap` moves the node there; the Terrain3D child is `TopLevel` (it places regions
      by location). `HeightMap`, `SplatMap`, the water sim, sources and map files stay in **map metres** from the map's
      (0, 0) corner, so files are unchanged. Error at the far edge ≈2 mm → ≈1 mm.
    - New API: `Terrain.MapToWorld(x, z, y)`, `WorldToMap(world)`, `GetHeightAtMap(x, z)`. Most code already went through
      `GlobalPosition`/`Bounds`; what didn't, and was only right while the map sat at the origin: water source markers,
      the placement preview and its brush ring (fed map metres to world queries), the flow arrows and the flood preview
      (added the terrain's position again under a node that already has it), the water shader's border fade (new
      `map_origin` uniform), the clipmap LOD reach (now from `Bounds`), and the water demo's tool check.
    - **`--cam` and the HUD pivot are in map metres** (converted in `DebugOverlay`), so every `--cam=` in this file still
      frames the same view. New debug flag `--stream-at=x,z,flow` (a Stream source there, map metres).
    - Checked: `--demo-sculpt`, `--demo-paint`, `--demo-channel`, `--demo-themes`, `--demo-water` (all ok, incl. the tool's
      border snap); `--demo-camera` same as before (its three first-person checks fail headless, as noted in phase 1);
      screenshots: whole map in game mode, the water demo with the River tool (markers, arrows, preview), and the far-corner
      stream (same view as before centring). Known, not new: `--demo-mapfile` throws `ObjectDisposedException` in
      `Terrain.SetMap` when reloading into the running scene (same on the previous commit).
    - Side effect: world-space noise (grass patches, edge noise, ripples) now lines up with a different world position,
      so the same map shows a different but equivalent pattern of patches.
  - [x] **3c. Performance** (implemented, waiting for the user to test; the RAM target isn't met, see step 5) (the phase 2 "not done" list): 60 FPS at 28.7 km (46–48 now); split a stroke's region pushes
    across frames (~10 ms at region corners); overlap the Terrain3D copy with generation/load (> 2 s now); peak footprint
    3.96 GB → < 2.5 GB, leaving room for city systems. Plus: partial lake/ground re-search after edits, and sleeping
    water tiles drop their flow arrays. Plan: baseline (step 0) → sleeping flow → split pushes → window lake search →
    faster load → memory → FPS.
    - **Step 0, baseline** (M1 8 GB, 28.7 km Mountains, `/usr/bin/time -l` for the peak footprint). New tools:
      `--profile[=frames]` (vsync off; waits for the first lake search + 60 frames unless `--profile-during-search`;
      prints frame avg/p95, render CPU, process time, draws; quits), stage timings for the lake search
      (`cs_last_find_water_ms`, printed by `--demo-scale`) and for the Terrain3D copy (printed by `SetMap`).
      | | before |
      |---|---|
      | lake + ground search (full map) | 17.6–21.4 s: flood 8.2, lakes 0.6, flow 9.3, masks 2.3 |
      | Terrain3D copy | 1.9–2.2 s: images 0.76, sanitize 0.03, add_region 0.59, update_maps 0.26–0.52 |
      | generate | 2.8–3.1 s |
      | FPS (vsync off), 300 / 700 / 1,800 m | 145 / 145 / 145 (6.9 ms, looks like the 144 Hz display cap); same during the lake search |
      | peak footprint, `--demo-scale` (no rendering) | 3.5 GB |
      | peak footprint, scene (generate + copy + lake search) | 5.2–5.7 GB |
      - FPS isn't the problem at steady state. One run at 700 m dropped to 34 FPS (29 ms frames, all process time)
        while the lake search ran, with a 5.2 GB peak: memory pressure on 8 GB. The 46–48 in phase 2 was likely the same.
      - The lake search's scratch memory is the peak: filled, parent, order, area, sediment, steep, level, gully
        (4 B each) plus closed, channel, wear, deposit (1 B each) ≈ 36 B/vertex ≈ 2.4 GB at 8193² (the M5.1 note said 14).
      - `ViewportGetMeasuredRenderTimeGpu` reads 0 on Metal, so GPU time isn't measured.
    - **Step 1, sleeping tiles drop their flows** (`native/water/`). `TileData` keeps ground, depth, pollutant, paint and
      source cells (17 B/cell); pipe flows, velocity and concentration live in `TileFlow` (28 B/cell) behind a pointer
      that's null while the tile isn't moving. `BeginTick` gives every stepped tile one (zeroed); `ZeroFlows` (sleep,
      reset) frees it. Pipes only read stepped neighbours, so they never meet a null. `--demo-scale` water: 240 → **103 MB**
      (same 1,365 tiles, 700 asleep); `--demo-water` all ok (same checks, fully wet 1025² 6.6 ms/substep).
    - **Step 2, split stroke pushes: measured, not needed.** New `--demo-push` (after the first lake search): a 35² patch
      raised every frame, 180 frames mid-region then 180 on a region corner, push time per frame. 28.7 km (512² regions):
      mid 0.8–1.0 ms avg (p95 1.3), corner 1.3–2.2 ms avg (p95 2.3–2.8); 1.8 km: 0.4 / 0.7 ms. The ~10 ms in phase 2 was
      a big 216×189 batch. About one frame in 180 spikes to 5–40 ms, sometimes inside Terrain3D's `update_maps` (upload
      stall), sometimes outside it: not something spreading regions over frames would fix, so the push stays as is.
    - **Step 3, window lake search** (the ~16 s wait after edits). An edit on a map wider than 2049 vertices is searched
      in a window around it: `cs_find_water_window` (`native/erosion/`), `Lakes.FindWindow`, window = edit + 160
      vertices, kept = edit + 64 (`Terrain.WindowMargin`/`WindowKeep`), 0.3 s after the last edit. **9–36 ms at 28.7 km**
      (was 18–24 s). A full search follows in the background after 15 s without edits (cancelled by the next edit);
      new maps, lake settings, and Add Lake Sources (needs every lake exact) search in full; small maps always do.
      - What a window needs from outside comes from the last search: `LakeMap.Flow` (new, u32 per vertex: catchment as
        log2 × 2048, gully bit, flood-parent direction, cm the flood raised the cell) and `LakeMap.Level`. Sediment
        needn't be kept: a cell passes on its capacity (area × slope).
      - Flood seeds are the border cells water leaves by: the map edge, the sea, a lower cell just outside, or a lake
        whose flood parent is outside. A lake seed is taken just after its level, so a lake whose spill is inside drains
        there. The rest of the border is a wall (with every border cell a drain, a dam's lake emptied out upstream).
      - Inflow: each cell just outside passes its stored area into the window as the full search splits it (slope²
        shares, one steepest cell for gullies, the flood parent on flats: lakes and filled pits). Found by testing:
        without the parent, a river crossing a filled pit into the window was lost; with a cm-rounded fill instead of
        the "raised" test, a flat pit looked sloped.
      - `--demo-scale` checks it against a full search on the kept part: the demo stroke, the biggest river near the
        centre with no edit, and a 3 m bump across it (4.6 km² catchment): mask bytes within ±2 on 100 % of cells,
        lake levels 100 %. With 96 instead of 160 the river case left one gully at a third of its catchment (flats near
        the window border route a little differently).
      - Memory: `LakeMap.Ground` is dropped once uploaded (−268 MB); `Flow` adds 268 MB. `PushGround` compares a hash per
        region instead of the previous masks.
      - `--demo-lake-window`: after the first search, a 60 m patch raised 3 m; a window search follows (23–36 ms), then
        the background full one (~24 s of work, 36 s after the edit). `--demo-erosion`, `--demo-water` all ok.
    - **Step 4, faster copy to Terrain3D** (`Terrain3DBridge.AddRegions`). 28.7 km: **1.9–2.2 s → 0.55–0.7 s** (create
      0.14, fill 0.05, upload 0.34–0.45; `SetMap` prints the split). Regions start with Terrain3D's blank maps
      (`sanitize_maps`) and their own Images are filled in place, in parallel (`Image.SetData`, one reused buffer per
      thread; ground masks `Fill`ed with zero). `set_height_map` was the cost (~2.7 ms per 512² region, it rescans every
      pixel), then `calc_height_range` per region (now `set_height_range` from the copy's own min/max). What's left is
      Terrain3D's GPU upload (`update_maps`), which has to be on the main thread, so building the images in the menu's
      worker (the plan) would save only ~50 ms: not done. Generate stays 2.8–3.1 s. Screenshots: `--demo-paint`, 28.7 km
      Mountains at 1.5 km, both as before.
    - **Step 5, memory.** 28.7 km Mountains, scene (generate + copy + first lake search):
      | | before | after |
      |---|---|---|
      | peak footprint (`/usr/bin/time -l`) | 5.2–6.0 GB | 5.5 GB |
      | steady footprint (`footprint`, after the search) | 3.8 GB | 3.4 GB |
      | managed live after the search | ~1.06 GB + garbage | 0.81 GB (height map, lake level, flow) |
      - Lake search scratch (`native/erosion/`): wear is set as each cell is reached (no slope array), the river level
        reuses the area buffer, the flood parents are freed after the flow stage: −0.8 GB of full-map arrays. Output
        identical (masks + flow hash printed by `--demo-scale`, same before/after).
      - A blocking GC (6–22 ms) right after a full search lands on big maps: the replaced search and the uploaded masks
        (~0.8 GB) otherwise stayed in the footprint until the GC got round to them. Deferred one call, so nothing holds them.
      - Whole-region ground mask uploads reuse one buffer and one patch Image (was a new 1 MB buffer + Image per region).
        Still a blit, not `SetData`: Terrain3D's colour Images have mipmaps, and replacing the data drops them
        ("Required size for texture update (349524) does not match … (262144)").
      - **Not met: < 2.5 GB.** Steady state is 3.4 GB and ~2 GB of it is Terrain3D: a CPU copy of every map (height,
        control, colour: 3 × 256 MB) plus the GPU copies (IOAccelerator 1.24 GB with render targets). A full lake search
        still adds up to ~1.8 GB for ~20 s (flood, flow and masks over 67 M vertices). Options (not done, need a
        decision): ground masks at half resolution in a texture of our own instead of Terrain3D's colour map
        (−~0.5 GB); full searches on a 2× coarser grid at 28.7 km (−~1.3 GB peak and ~4× faster, masks upsampled);
        or keep the full search for Add Lake Sources and new maps only (windows cover edits).
    - **Step 6, FPS.** Nothing to fix in rendering: with vsync off every view measured 6.9 ms (145 FPS, the display's
      144 Hz cap) at 300 / 700 / 1,800 m, also during a lake search; `Terrain._Process` ~0.01 ms. Some runs, though,
      ran at 27–29 ms for their whole length (34–36 FPS), each time near the 5–6 GB peak on the 8 GB M1: memory
      pressure. So the frame rate on this machine depends on step 5's peak; the background full search now only runs
      after 15 s without edits and stops at the next edit. The HUD's "FPS 36" in scripted screenshot runs is the same
      thing or macOS throttling a hidden window; it's not seen in `--profile`. Check FPS in a normal session.
  - [x] **3d. Camera zoom-out** (implemented, waiting for the user to test). `MaxDistance` (1,800 m) is still the god view:
    every zoom-dependent limit (pitch, clearance, near plane, smoothing) reaches its far end there, so nothing below it
    changed. Past it the zoom continues to `ZoomOutLimit` = max(MaxDistance, `WholeMapZoom` 1.5 × map side): 43 km on
    28.7 km, 5.4 km on 3.6 km. Beyond the god view the camera may leave the map (the keep-inside clamp eases out over the
    first half of that range); the pivot stays inside. `Far` = max(12 km, 4 × distance); the near plane grows with it.
    The distance fog thins past the god view (density × (1800 / d)^1.5), so the whole map isn't lost in haze.
    Terrain3D's clipmap only reached ~28 km with its default 7 LODs (half of the 28.7 km map vanished from far):
    `Terrain3DBridge.FollowCamera` now sets `mesh_lods` (7–10) to reach min(far plane, farthest map corner), so normal
    play stays at 7. The fog skirt now runs out to 150 km so its end stays out of frame. Screenshots: 28.7 km at 43 km in
    both modes, 60 FPS (editor) / 50 (game); `--demo-camera` all ok. Known, not new: mountain tops right on the border
    poke above the fog skirt as small specks (in game mode).
  - [x] **3e. Horizon ring** (implemented, waiting for the user to test). Low hills past the border fading into haze, in
    game mode instead of the flat fog skirt; a light fog band stays on the border (user's choice).
    - **Edge styles** (`Terrain.EdgeStyle`, replaces the `EdgeFog` bool): **Line** (Map Editor default), **Fog** (the M3.1
      bank), **Horizon** (game default). Bottom bar: the **Edge** button cycles them. Flag `--edge=line|fog|horizon`.
    - **Mesh** (`TerrainSkirt` → `src/Terrain/TerrainHorizon.cs`): same inner loop (the border vertices), loops out to 6 km
      then the fog tail to 150 km; outer loops are coarser (segments ~6 % of the distance, ≤ 250 m) and zipped together.
      Two heights per vertex: Y = horizon, UV2.x = the old sinking fog floor, picked by `horizon_mix` (no rebuild to switch).
      Horizon height = border height averaged along the border over 0.6 × distance, + fbm noise (×2, ±1) × relief growing
      over 2 km, + a rise toward 6 km; relief = max(`horizon_relief` 0.35 × height range, 200 m). Under the sea where the
      border is (Sea source level, `--sea=level` to add one). Rebuilds: at most 2/s.
    - **Shader** (`shaders/terrain_horizon.gdshader`, replaces `terrain_skirt.gdshader`): lit like the terrain (same
      `light()`), ground = the theme's `horizon_flat_material`/`horizon_steep_material` (by slope) at far tiling, fading to
      the texture's average colour from 300 m to 1.5 km (the tiling showed as dots on far hills), × `horizon_tint`; sea in
      `horizon_sea_color` where below the Sea source; haze from `horizon_haze_start` (400 m) to `_end` (7 km). New SDK group
      `Horizon`; Terrain copies matching uniforms to the ring's material (runtime ones: origin/size, material arrays, sea).
    - **Border band**: `horizon_edge_fog` (0.25) with square corners (the rounded fog corners left a pale wedge), fading out
      with camera distance 3–15 km (from far out it framed the map), none over the sea (the map's water has none either).
      The water's border fade is 2 m in Horizon (was 250 m, which showed the seabed along the border).
    - Measured: 3.6 km ring 14k vertices, 14 ms; 28.7 km ~111k vertices, 54 ms. FPS at the 43 km whole-map view: 59–62.
    - Screenshots: 3.6 km default from inside (skyline of hazy hills), island + sea from above and at 500 m, 28.7 km
      Mountains whole map and at the border; Fog and Line modes unchanged. `--demo-sculpt/-paint/-channel/-themes/-camera/-water` ok.
    - Not done: building the ring off the main thread (54 ms hitch on 28.7 km border strokes, 2/s at most); the ring's sea
      doesn't animate; the ring ignores theme hooks (grass tint by height), so its colour can differ slightly from the map's
      edge; the Edge button's label doesn't follow `--edge`. Later: an unbuildable strip *inside* the heightmap
      (e.g. 1.5 km, a rule, not a grid change; could become CS-style unlockable tiles with M7).
  - [x] **3f. Finer water grid on big maps** (implemented, waiting for the user to test). 28.7 km water runs on **7 m cells**
    (4097², `WaterSim.MaxCells` 2048 → 4096; smaller maps stay on 3.5 m). User: it's a game, so looks and feel beat
    physical accuracy.
    - **Sparse storage** (`native/water/`): cells live in 64² `TileData` blocks allocated only where there's water, next
      to it, or a source; a dry, unpainted tile with no wet neighbour is freed after a tick. The ground is read from the
      heightmap by pointer (C# pins `HeightMap.Buffer`) when a tile is allocated, so there's no dense ground copy. Bulk data
      moves per tile (`cs_water_get_tile/set_tile/commit`, `raise_tile`, drained tiles), and the snapshot is read per
      changed tile (`cs_water_read_tiles`). C#: `WaterGrid` (sparse, same tiles) for save/load, drain/undo and fills;
      `WaterSim`'s snapshot is per tile (null = nothing shows). `WaterSurface` makes a page's material/tile nodes the first
      time it holds water.
    - **Coarse cell ground = halfway between the mean and the lowest vertex** (`kGroundLow` 0.5). This mattered more than
      the cell size. With the mean, a 3.5 m bed 1.5 m deep became a 0.5 m dip on 7 m cells and the stream spread 24 m wide.
      With the lowest vertex, 14 m beds leaked into neighbouring cells (21 m). Halfway keeps both at their width
      (`--demo-water` check 11, now wet-strip width + water > 7 m past the banks: 3.5 m bed → 7 m strip on 7 m cells, 14 m
      on 14 m cells; before, 24 m and 34 m). Thin terrain walls are lower in the sim by up to half their height (dams
      will be an obstacle layer, M5.3).
    - **Stays coarse** (≤ 2049², 14 m at 28.7 km): the ground marks (shore distance + wet paint texture; `MarksWidth`,
      `MarkFactor`, max over each block) and the hover flood preview. Both are dense, and 14 m is enough for them.
    - Map files: format unchanged (width/depth say which grid). A water section saved on another grid (old 14 m saves) is
      resampled on load (`WaterGrid.Resample`, nearest cell; pollutant kg scaled by the cell areas). Before, it was dropped.
    - Measured (`--demo-scale`, 8193² Mountains; stream 20 m³/s, a lake, sea at 5 % of the range, 10 sim-min): 1,365 of
      4,225 tiles in memory = **240 MB** for 119 km² of water (dense 7 m would be ~0.8 GB), 5.2 ms/substep with 310 active
      tiles, 8 substeps/tick, 10 sim-min in 21 s (×8 keeps up), sea fill 0.3 s, grids read 17 ms / load 0.33 s. Fully wet
      1025²: 6.3 ms/substep (M5.4: 7.1). `--demo-water` all ok, including new check 16 (sparse: a stream allocates its
      path, tiles freed after it dries; a pond saved on 7 m loads on 3.5 m with the same volume). FPS at the 28.7 km
      demo stream overview (900 m): 31 (14 m cells: 30); `--demo-falls` 31.
    - `--water-cells=n` caps the water grid (2048 = the old 14 m on 28.7 km) to compare looks.
    - Not done: ~~sleeping tiles could drop their flow arrays~~ (done in 3c step 1: 240 → 103 MB); the
      ground marks' coarse distance pass still scans the whole 2049² grid once a second.
  - [ ] **3g. Look tuning at 28.7 km**: snow and rock read blotchy (noise sizes tuned for 2 km maps).
  - [x] **3h. A 14.3 km size** (4096 cells, CS2's buildable side) in `MapSize.All` (implemented, waiting for the user to
    test). 256² Terrain3D regions (16 a side), water on 3.5 m cells (4097², the `MaxCells` cap). Generate ~1.4 s (Rolling Hills).
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
- M2.1 (user's call): **no water is placed automatically**. New and generated maps start dry, even with a sea shape; old
  files without saved depths aren't given Lake sources. The map maker places every source (the Sea tool defaults to the
  generator's sea level; Add Lake Sources is a button). The Channel tool shapes ground only and adds no source.
- M2.1: channels are a **sculpt tool dragged freehand with a profile**, not a spline object (user's choice); canals as a
  built network (cost, demolish, fill from connected water) belong with roads later.
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
  *The 70 km background was dropped; see the 2026-09-28 entry.*
- M6 (2026-09-28, user's call): **the map stops at 28.7 km; no 70 km background.** 28.7 km is already ~2× CS2's buildable
  side. Instead: polish at 28.7 km (M6 phase 3a–3h), a cheap horizon ring rather than real terrain beyond the border.
  - **No dynamic floating origin.** A float at 28.7 km is ≈2 mm precise (≈1 mm centred), sub-pixel at the 15 m closest zoom;
    Terrain3D can't be moved around anyway. Instead centre the map once (static shift) and fix any world-space UV issues
    locally, after a far-corner screenshot test (phase 3b).
  - **Terrain stays a uniform 3.5 m grid**; no adaptive density under small brushes (Terrain3D has one vertex spacing, and every
    system indexes cells uniformly). Fine detail comes from meshes/decals (roads, walls) and the shader. The brush minimum
    (`TerrainToolController.MinRadius` 8 m, > 2 cells) already covers too-small brushes. "Finer where it matters" goes into
    the water grid (phase 3f), not the terrain. A sparse detail-offset layer stays an idea until roads/buildings exist.
  - **Edge fog stays** as the map's edge in game mode; hidden (with a preview toggle) in the Map Editor (phase 3a).
  - Map size 28.7 km isn't a design number: 3.5 m (CS2's spacing) × 8192 (biggest power of two in the memory budget).
    Sizes in between (e.g. 20 km) would leave Terrain3D regions part-empty or need another cell size.
- M3.2: Random rotation rolls once per click, not per tick: a new angle every tick blurs a stationary brush into a round blob.
- M6 phase 2: Terrain3D regions are 256²/512² (smallest that fits 16 per side), not 1024², because each edit re-uploads whole
  regions. (The relative snow line decided here was removed in M3.4: snow is paint-only.)
- M3.4: ground rules are a data-driven stack in one shared look file (not per map), edited in an in-app Materials panel;
  the shader is specialised (baked) per look for speed. No automatic snow. *Superseded by M3.5.*
- M3.5: looks are **themes made in Godot**, not edited in-app (user's call: the Materials tab didn't look good, and mods/DLC
  should be able to ship whole looks such as winter). A theme ships a *look-only* shader that includes our SDK core (not a
  whole shader, so engine changes don't break themes). Material slots are free per theme; only erosion/water features are
  fixed named slots, since a shader can't compute those masks. Theme is **per map**. Previews are baked in the editor, not
  rendered at runtime. Mod loading comes next (M3.6).
- M6 phase 1: new maps use 3.5 m cells (CS2's spacing), replacing the 2 m decision above; sizes are powers of two (1.8–28.7 km).
  Old 2 m maps still load at their own cell size. Paint is two layers per vertex plus coverage (Terrain3D's control format);
  map files store 16-bit heights, like CS2 and heightmap exports.
- M5.1: ground texturing by water is **derived from the current heights** (flow routing + stream power), not recorded from the
  erosion run, so it follows sculpting, needs no file/undo changes and works on un-eroded maps. Stored in Terrain3D's colour map.
- M5.4: water ticks at a **fixed 1 s of game time** (user's list: fixed timestep, interpolate for rendering, determinism);
  Speed changes ticks per second, never the tick. Calm tiles **sleep** (discharge-based, not speed-based). Pollution rides the
  pipe flows (mass-conserving) rather than being advected semi-Lagrangian. Narrow water: measure + mitigate in rendering
  (user's choice); the finer grid is a later milestone. Water look lives in a Godot material (`materials/water.tres`).
- M5.2: water is **simulated** (virtual pipes, C++ on the CPU, user's choice over a GPU compute shader: instant queries,
  easier dams/gameplay). The static lakes were replaced (user's choice); Fill Hollows seeded the sim from the Priority-Flood
  levels (M5.5: replaced by Lake sources, so all water belongs to a source the user can delete). Sources follow the CS2 Water Features mod (Stream, River, Lake, Sea). Core first (M5.2), events/structures in M5.3.
- M5.0: lakes are **computed, not generated**: every depression fills to its spill height (Priority-Flood), so water always
  follows the ground. The M4.2 noise channels/basins were removed (user's choice). Erosion is a separate, undoable step run
  from its own panel, not part of the generator. C++ as a plain C library via P/Invoke-style function pointers rather than a
  GDExtension (no C# bindings, needs godot-cpp/scons, copies arrays). Shallow hollows are drained by cutting outlets
  (breaching) at the end of erosion; without it noise terrain turns 17–30% of the map into ponds and valley lakes.
- 2026-09-28: **optional visual effects get a graphics-settings switch** (user). Features are tried in the experiment,
  then exposed in the game's Graphics settings so players can turn them off. Every new optional effect is built
  switchable from the start (uniform or shader variant, off still looks fine). List: "Graphics settings" above M5.
- 2026-09-29 (M6 phase 3b): the map is **centred on the world origin** (static, on whole Terrain3D regions); gameplay data
  (heights, paint, water, sources, files) stays in map metres from the map's corner, and `Terrain.MapToWorld`/`WorldToMap`
  convert. `--cam` and the HUD use map metres. The precision check at 28 km showed nothing, so no UV wrapping.
- 2026-09-28 (M6 phase 3f): **looks and feel over physical accuracy** (user: "this is a game not a water sim"). Sim
  changes are judged by close-up screenshots and what players notice, e.g. water cell ground halfway between the
  mean and the lowest vertex, chosen because streams stay in their beds.
- 2026-09-29 (M3.7, M6 3e): **rain is look only for now** (global wetness darkens/shines the ground; sim water from rain is
  M5.3) and **weather isn't saved** with the map. **Horizon ring keeps a light fog band** on the border in game mode (user's
  choice over a seamless border); the old fog bank stays as the Fog edge style.
