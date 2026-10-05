# Zoning ROADMAP

The player paints zones onto freeform parcels along roads, and buildings grow on them. The plan, its reasons and
every agreed choice are in `PLAN.md`. This file tracks the milestones.

## Decisions
See `PLAN.md` §2–7. Summary: zone cards (style + use + density + optional form) in the build tray, freeform parcels
(no cell grid) split from the road graph's blocks, buildings as `.tres` content, simulation data free of Godot types.

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

### ⬜ Z0.5: Storyboard (drafted 2026-10-06, waiting for the user's review)
`docs/zoning-storyboard.html` (published: https://claude.ai/artifact/CgokqgRtPifutiSmgfcejG): one interactive frame
per case (straight, curve, corner, T, cul-de-sac, slope, depth handle, zone cards with brush/fill/erase, road moved
under parcels), drawn by a small JS version of the split rules, plus the open questions. Agreed before any parcel
code; once agreed it is the spec for Z1–Z3.

### ⬜ Z1: Blocks and frontage bands
From the road graph, debug draw, rebuilt on road edits. Sets up the `experiments/zoning` Godot project on the
packages.

### ⬜ Z2: Parcel split
Mixed widths, perpendicular cuts on curves, corners, slivers, determinism.

### ⬜ Z3: Zone tools
Brush, fill, erase, depth handle; zone cards from `.tres`; zones inherited across road edits.

### ⬜ Z4: Buildings from content
Placeholder boxes per style and form, fitting, setbacks, yards, pads with plinths, variation.

### ⬜ Z5: Zone cards in the tray
Style + use + density + form, `Low · Res` pill; two test styles (Eastern Europe, US Suburban) with box sets.

### ⬜ Z6: Growth
Demand sliders, spawn, construction, replacement on re-zone.

### ⬜ Z7: Control
Lock, swap variant, height limit, parcel edits. (Place growable later; the data is ready for it.)

### Later
Mixed use, levels, modular buildings, industry, performance at scale.
