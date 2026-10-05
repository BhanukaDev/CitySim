# Zoning ROADMAP

The player paints zones onto a cell grid that bends with the roads (CS-style cells, not square), and buildings grow
on plots made of painted cells. The plan, its reasons and every agreed choice are in `PLAN.md`. This file tracks the
milestones.

**The storyboard is the spec:** `docs/zoning-grid.html` (published: https://claude.ai/artifact/5tdTraaJATXb796XSVgqBS).
Open it in a browser; each frame is interactive and its JS (`gridChains`, `makePlots`) is a working reference for
the grid maths. Where this file or `PLAN.md` disagrees with it, the storyboard wins. Check every milestone frame by
frame: rebuild the frame in Godot (`--storyboard=<frame>`), screenshot it from the top, compare side by side.

## Decisions
See `PLAN.md` §2–7. Summary: zone cards (style + use + density + optional form) in the build tray; a freeform cell
grid per road side (8 m cells, columns square to the road, rows parallel, corner patches at 60°–150°); the player
paints cells (depth included); the card's form groups painted cells into plots; a style is only the look;
simulation data free of Godot types. Procedural (Townscaper-style) buildings come later, on the same cells.

## Starting the code (for the next session)
1. Read `PLAN.md` §3 and open `docs/zoning-grid.html`. Read `packages/citysim_splines/README.md` (graph, junction
   footprints) and `packages/citysim_roads/README.md` (road types and widths).
2. Set up `experiments/zoning` as a Godot 4.7 .NET project like `experiments/roads`: `project.godot`, a `.csproj`
   (assembly `ZoningExp`), `scenes/Main.tscn`, `src/App.cs`, and the same five symlinks in `addons/`
   (`citysim_terrain`, `terrain_3d`, `citysim_splines`, `citysim_roads`, `citysim_build_ui`). Copy what the roads
   experiment's `App.cs` does to get terrain, roads and the build UI on screen, then add zoning on top.
3. Keep the grid maths Godot-free (`System.Numerics`, like `HeightMap`) so it ports to the main game: a `ZoneGrid`
   built from frontage runs, with `Cell` (key, polygon, run or patch, column, row, block), `CornerPatch` and `Plot`.
4. Blocks need the road graph's **faces** (closed loops of road sides). If `SplineGraph` doesn't offer them, add a
   generic faces query to the splines package (no road names in addon code) and re-check `experiments/splines` and
   `experiments/roads`.
5. Verify each step as the other experiments do: `dotnet build`, a headless `--demo-*` check that prints "all ok",
   then the `--storyboard=<frame>` screenshot compared with the HTML frame. Don't commit until the user has tried it.

```
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build
$G --headless --path . --import
$G --headless --path . --quit-after 300 -- --flat --demo-grid        # straight, curve, tight bend, four-lane: "all ok"
$G --path . -- --flat --storyboard=straight --cam=560,500,195,90,0 --screenshot=screenshots/grid_straight_frame.png
$G --path . -- --flat --storyboard=curve --cam=560,500,195,90,0 --screenshot=screenshots/grid_curve_frame.png   # --radius=80
```
`--storyboard=<frame>` puts the frame's (0, 0) at map (400, 400), so `--cam=560,500,...` is the middle of its 320 × 200 m
view. The HTML frames can be shot with headless Chrome for the side-by-side:
```
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --window-size=1240,1100 \
  --virtual-time-budget=3000 --screenshot=out.png "file://$PWD/docs/zoning-grid.html#curve"
```

## Milestones

### ✅ Z0: Build UI and roads into packages (2026-10-06, play-tested by the user)
- `packages/citysim_build_ui/`: bar, tray, cards, hover card, `UiTheme`, icons, `BuildCategory` / `BuildTab` /
  `BuildItem` / `ContentLibrary`. `packages/citysim_roads/`: road types, styles, rendering, crossings, lane links, the
  road and terrain tools, their options panels and all of their content. Moved with `git mv` (history kept), not
  rewritten. Symlinked into `experiments/roads/addons/`, which keeps the scene and the demos.
- What changed to make the split:
  - `GameHud` no longer knows the roads and terrain panels. A feature adds its panel per category with
    `AddOptionsPanel(categoryId, panel)`; the panel implements `IOptionsPanel` (`SetItem`, `HandleKey`, which takes
    the roads keys 1–4 and Ctrl+A). `RoadsHud` (roads package) gives the old calls as extension methods:
    `hud.RoadOptions()`, `hud.TerrainOptions()`, `hud.RoadsOpen()`, `hud.TerrainOpen()`, `hud.PickedRoadTool()`.
  - `ContentLibrary` no longer knows `RoadStyle`: any resource implementing `IContent` is indexed by type and id
    (`library.Get<T>(id)`); `library.Style(id)` is an extension in the roads package. It reads every
    `res://addons/<package>/content/` and then the project's `res://content/`, all as "Base".
  - `res://` paths in content, shaders, `.import` files, code and the scene now point into `addons/citysim_roads/` and
    `addons/citysim_build_ui/`.
- The build UI package needs `citysim_splines` (its `KeyGlyphs` draws the mouse glyphs in key hints).
- Checked: `dotnet build` (same 6 warnings as before), every headless roads demo (`--demo-content`, `-road`,
  `-road=four_lane`, `-mixed`, `-terrain`, `-shape`, `-crossings`, `-lane-links`, `-cluster`, `-bend-t`,
  `-bend-t=four_lane`) and the windowed `--demo-grid-bend` and `--demo-continue`: all ok, same as before the move.
  Content loads the same (2 categories, 7 tabs, 10 items). Screenshots before and after: the road junction is
  pixel-identical, the Roads UI identical to within anti-aliasing.
- Play-tested 2026-10-06: the roads and terrain trays in `experiments/roads` behave as before.

### ✅ Z0.5: Storyboard (agreed 2026-10-06)
- **`docs/zoning-grid.html`** (https://claude.ai/artifact/5tdTraaJATXb796XSVgqBS), agreed by the user: frames
  `straight`, `curve`, `corner`, `t`, `cul`, `block`, `paint`, `moved` (the ids are the URL hash, e.g.
  `zoning-grid.html#corner`). Controls per frame: how much is zoned, form, style, grid depth (rows), cell size, plus
  radius / crossing angle / stem angle / road move.
- Open questions left at their defaults: cell 8 m, rows 5 (max 8 per road type), corner patches 60°–150°, plots
  need the front cell. Revisit in the play-test.
- How we got here: a free-parcel storyboard first (`docs/zoning-storyboard.html`,
  https://claude.ai/artifact/CgokqgRtPifutiSmgfcejG), then paint-chunks, shared form rules (kept), and "organic"
  noise from the research (dropped: it only made the same layout wavy). The user then chose the CS-style grid that
  follows the road. The old page's slope frame still describes the pad and plinth rules.

### 🟡 Z1: The grid on straight roads and curves (built 2026-10-06, waiting for the user's play-test)
New `experiments/zoning` project on the packages (see "Starting the code"). Frontage runs from each road side, strips
of cells (even columns measured at mid-depth, rows from mitred offsets), debug draw of the cell lines on the terrain,
rebuilt when a road changes. Rows capped on tight bends. Checked against frames `straight` and `curve`.
- Project: `project.godot` (`CitySim Zoning`), `ZoningExp.csproj`, `scenes/Main.tscn` (the roads scene without its
  demos, plus `ZoneGridView` and `ZoningDemo`), the five `addons/` symlinks. Draw roads in the Roads tray as usual.
- `src/Grid/` (no Godot types): `ZoneGrid` (`FrontageRun` → `ZoneStrip`s of `ZoneCell`s, key `run:column:row`, a
  port of the storyboard's `prepRun` / `cellPoly`), `RoadFrontage` (each edge's two sides at half the profile's width,
  between its junction cut-backs, a point every 2 m). `src/ZoneGridView.cs`: rebuilds on `SplineNetwork.Changed` (so
  on undo and redo too) and draws a faint fill and outline per cell on the ground. Shown only while the Roads tray
  is open (and Zones, from Z3), as in CS; `AlwaysShow` for the storyboard shots.
- Roads package: `RoadType.ZoneRows` / `RoadDef.ZoneRows` (0–8, default 5; the highways have 0).
- Change from the storyboard: a run's end columns are square to the road at the end point (the storyboard used the
  last segment's normal, half a segment's turn off on a curve; found by the band-area check).
- Checked: `--demo-grid` all ok (straight: rectangles of `len / round(len / 8)` × 8 m, front row on the frontage;
  R130 and R40 curves: cells cover the band to 0.5 %, none folded, back cells wider outside and narrower inside,
  columns `round(mid-depth length / 8)`; R40 inside keeps 3 rows; four-lane frontage at its half width; rebuilt on
  change and undo; a second build is identical). Curve frame at R130: 470 cells (57×5 outside, 37×5 inside), the same
  as the HTML. Screenshots `grid_straight_frame.png`, `grid_curve_frame.png`. Roads demos still all ok.
- Not yet (Z2): where roads meet, each edge side is still its own run, so strips overlap at junctions and corners.
  Kinks inside an edge (hard corners) aren't split yet either, so they cap the rows.

### ⬜ Z2: Corners, junctions and blocks
Block faces from the road graph; corner patches (60°–150°, parallelogram cells, the wedge column next to a skewed
patch); sharp corners and kinks cut on the meeting line, small cells dropped; T-junctions; cul-de-sac bulbs; in
closed blocks each side stops half-way. Determinism (same roads, same grid). Checked against `corner`, `t`, `cul`,
`block`.

### ⬜ Z3: Painting and zone cards
Zones build category and tray (cards from `.tres`: style + use + density + optional form, `Low · Res` pill), the
options panel (Brush / Fill / Erase, keys 1–3, `[ ]` size), painted cells shown on the terrain, hover highlight.
Painted cells carried across road edits by centre. Checked against `paint` and `moved`.

### ⬜ Z4: Plots and placeholder buildings
Forms as shared `.tres` config (plot widths in cells, setback, building depth range, corner rule), styles as looks.
Plots from painted cells (front cell needed, shallowest column), corner plots, placeholder boxes per form and style,
pads with plinths on slopes. Two test styles (EU, US) with boxes of different shapes.

### ⬜ Z5: Growth
Demand sliders per Use × Density, spawn with a short construction phase, replacement on re-zone and after road
edits (kept / replaced over time / demolished, as in `moved`).

### ⬜ Z6: Control
Lock, swap variant, height limit. (Place growable later; the data is ready for it.)

### Later
Procedural buildings on the cells (Townscaper-style modules per style, its own experiment first), complexes and
footpaths in block interiors, mixed use, levels, industry, performance at scale.
