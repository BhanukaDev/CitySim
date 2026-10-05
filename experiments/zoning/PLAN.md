# Zoning: Plan (agreed 2026-10-06, no code yet)

The player paints land use along roads and buildings grow on their own, as in Cities: Skylines. They never place a
specific growable building (with one exception, see Player control). This file is the agreed plan. The agreed
storyboard `docs/zoning-grid.html` is the spec for the land and painting (§3). Earlier choices were made on
https://claude.ai/artifact/Uec32oZaL55XkhjrRG71Tb (zone cards per style, a card per form, no place-growable tool yet).

## 1. What players say about zoning in other games

### What they like
- **Painting zones and watching a city grow** (CS1, CS2, SimCity). It's the core loop and nobody wants to lose it.
- **Regional looks.** CS1's district styles and the *Building Themes* mod were among the most used. CS2 shipped
  North American and European themes, then 9 free region packs (France, Germany, UK, Eastern Europe, Netherlands,
  Japan, China, USA Northeast, USA Southwest).
- **More density steps than low/high.** CS2 split housing into low, medium row, medium, high, mixed and low-rent, and
  players asked for "a more gradual increase in building size and height".
- **Mixed use.** CS2's shops-below/flats-above zone was well received. Metropolis 1998 has mixed and business-specific
  zones. CS1 players used the *Custom Zone Mixer* mod for this.
- **Organic layouts.** Manor Lords' burgage plots (plots follow the road and the contours, with no rigid grid) are the
  most praised land system in recent city builders.
- **Buildings that visibly change.** CS1 buildings changed model when they levelled up. CS2's mostly don't, and players
  miss it.

### What they complain about
- **Repetition (CS2).** Every building fills the whole zoned depth, the frontage is split into equal narrow plots,
  and each model fits only one plot size. A 6-deep strip grows the same 6-deep houses again and again.
- **Gaps and dead cells.** Curves, corners, odd angles and blocks where two roads' zones meet leave unused grass, even
  on square grids. Corner lots face the wrong way, and corner buildings are missing.
- **Slopes.** CS2 growables cut ugly terrain steps on gentle hills. CS1 handled slopes better.
- **Fixed zone depth and no control over it.** Deeper zones and per-side depth were among the most wanted CS1
  features. The mods *Zoning Adjuster* and *Zone It!* add one-sided zoning, setbacks, depth 0–4 and zoning on any road.
- **Demand that forces sprawl.** In CS2, single-family demand never drops and dense centres don't pay off. Players
  want to choose detached, rows or flats themselves, and want walkable European-style cores.
- **Lack of control.** The most installed mods are *Plop the Growables* (place a growable anywhere, never replaced),
  *Find It*, *Ploppable RICO* (large buildings as zoned buildings), *Move It* and *Anarchy*. Players also want to lock
  a building so it stops levelling or changing.
- **Size limits.** CS1 growables stop at 4×4 cells, which is why RICO exists.

### What they ask for
- Height and density limits per district.
- Corner shops and corner buildings.
- Zone depth per road and per side, and one-sided zoning.
- Choosing a building's variant and locking it in place.
- Redeveloping a suburb into a denser layout without bulldozing it all.
- Zoning along paths and quays.

## 2. Zones: cards like road types (agreed 2026-10-05)

A zone is picked from a **card in the build tray**, like a road type. Each card is one style + use + density (+ form),
for example **EU Low Residential**, **US Suburban Low Residential** or **EU Row Houses**. A pill on the card shows the
plain category it counts as (`Low · Res`), so the player can tell what any style's card is.

| Field | What it is | Read by | Examples |
|---|---|---|---|
| **Use** | What the land is for | Simulation | Residential, Commercial, Office, Mixed (shops + flats) |
| **Density** | How much is built | Simulation | Low, Medium, High |
| **Form** | The kind of building within the density, with its shared plot and building rules | Spawner, plots | Detached, Semi, Row (Low); Row, Walk-up, Perimeter block (Medium) |
| **Style** | What it looks like (models, materials) | Spawner, visuals | Eastern Europe, US Suburban, UK, Nordic |

- A zone card is a `.tres` content file (made in Godot, dropped in by mods/DLC) that names a style, use, density and
  optionally a form. A card without a form grows the zone's default forms (by weight). A card with one (**EU Row Houses**) grows
  only that form. That's how the player picks detached vs rows vs flats.
- The simulation reads only Use × Density (and later Level). Style and form are content, so a new region pack adds
  cards and buildings, not new rules.
- **Agreed (2026-10-06): a style is the look; the rules are shared.** Plot widths, depth, setback, corner rules,
  irregularity and footprint ranges belong to the **form** (one shared config per form, e.g. Detached, Row,
  Walk-up) and every style uses it. A style supplies the models and materials and may override a field where it
  really needs to (for example a deeper US setback), but most styles override nothing. So EU and UK Low Residential
  get the same plots with different buildings, and repainting a stretch with the same form in another style keeps its
  plots and only swaps the buildings over time.
- Tray tabs group the cards by use (Residential, Commercial, Office, Mixed), so a tab doesn't fill up as packs are
  added. (Industry is left out for now and fits the same model later.)
- **Agreed:** 3 densities (Low, Medium, High) plus Mixed.
- Re-zoning cells with another card (another style or density) replaces its building over time. That's how
  "redevelop the suburb" works. A style-only brush (re-style without re-zoning) can come later.

**Agreed (2026-10-05): 3 building levels.** Each level raises capacity (households, jobs) and consumption. They're
not the focus now: the data has a `Level` field, and the visuals and rules come later.

## 3. Land: a freeform cell grid (agreed 2026-10-06)

Zoning works on a **cell grid like Cities: Skylines, but the grid bends with the road** instead of being square.
The agreed storyboard is `docs/zoning-grid.html` (published: https://claude.ai/artifact/5tdTraaJATXb796XSVgqBS).
**It is the spec**: where this file and the storyboard disagree, the storyboard wins, and each milestone is checked
frame by frame against it.

### The grid
1. **Frontage runs.** Each road side's frontage line (the outer edge of the pavement, half-width + pavement from the
   centre line) is a run. Runs meet at block corners. Closed blocks are the road graph's faces; open land beside a
   road just has runs with free ends.
2. **Strips of cells.** Each run gets a strip of cells: **columns** cut square to the road at that point, **rows** as
   offset lines parallel to it (offsets use mitred vertex normals). Columns are spread evenly: `n = round(length /
   cell)` columns of equal width, measured **half-way back** (mid-depth), so curves fan out on the outside and
   narrow on the inside and both stay usable. No leftover slivers.
3. **Cell size 8 m** (as in CS), one size for the whole game. **Rows: default 5 (40 m), max set per road type
   (1–8).** On a tight bend the inside gets fewer rows instead of folding over itself.
4. **Corner patches.** Where two runs meet at a block corner between **60° and 150°**, the region that belongs to
   both bands gets its own small grid with lines **parallel to each road**: squares at 90°, parallelograms when
   skewed. Each corner cell faces both streets. The patch is the parallelogram from the kerb corner `P` to `Q`, where
   the two bands' back lines cross: `Q − P = −tA·a + tB·b`, with `round(a / cell) × round(b / cell)` cells. A band
   next to a skewed patch has one wedge-shaped last column, the only irregular cell. CS overlaps grids at corners and
   leaves dead cells; this doesn't.
5. **Sharp corners and kinks.** Under 60°, over 150°, or where a road kinks (a cul-de-sac stem meeting its bulb),
   the strips are cut on the line from the kerb corner to `Q`. Cells left with under a quarter of their area are
   dropped (later: trees).
6. **Blocks.** In a closed block each side's grid stops half-way to the far side (fewer rows on a narrow block).
   Whatever no cell reaches is the block interior (green for now; later complexes can use it).
7. **Determinism.** The grid is a pure function of the roads and the settings. A save stores only the roads and the
   painted cells.

### What the player does
- **Paint cells** with the picked zone card, as in CS: **Brush** (every cell it touches; cells highlight as you
  hover), **Fill** (the whole block), **Erase**. Keys 1–3, `[ ]` brush size. **Depth is painted too**: paint 2 rows for
  shallow plots, all rows for deep ones, one side for one-sided zoning. No separate depth handle.
- **Plots come from painted cells.** Neighbouring front-connected columns with the same card form a stretch; the
  card's **form** groups its columns into plots by its plot widths in cells (Detached 2–3, Row 1, Walk-up 2–3,
  Shops 1–2, Tower 4–5…), seeded from where the stretch starts. A leftover narrower than the smallest plot joins the
  last one. A plot needs its **front cell** painted (road access) and is as deep as its **shallowest painted
  column**. In a corner patch, painting the corner cell makes one corner plot from the painted rectangle it starts.
- **Road changes:** the moved road's grid is rebuilt; every other road's cells stay as they are. Each new cell takes
  the card painted under its **centre** in the old grid (old road land is unpainted). A building still standing on
  cells painted with the same card stays; otherwise it's replaced over time; anything now on the road is demolished
  with the edit (same undo).
- Re-zoning cells with another card replaces their building over time. Repainting with the same form in another
  style keeps the plots and only swaps the buildings.

### Buildings on plots (for now)
- Placeholder boxes per form and style: a footprint fitted inside the plot square to the road at its middle, front on
  the frontage line plus the form's setback, depth **up to** the plot depth (CS1's rule), the rest is yard.
- **Slopes:** each plot levels a pad through the terrain edit API at the footprint's uphill edge, with a **plinth**
  (basement) on the downhill side up to 2.2 m; steeper plots grow a stepped hillside variant or stay garden (see the
  slope frame of the old `docs/zoning-storyboard.html`).
- **Later: procedural buildings on the cells** (Townscaper-style: filled/empty cells, modules picked by their
  corners and deformed to fit each cell, a style is a module kit). Not now.

### Data
- Cells, strips, patches and plots are `System.Numerics` polygons with no Godot types (like `HeightMap`). The
  simulation reads plots (zone, form, style, level), not meshes.
- A cell's key is `run:column:row` or `patch:i:j`; keys change when a road changes, which is why painted cells move
  across by centre, not by key.

### Defaults to revisit in the play-test
Cell 8 m; rows 5 (max 8); corner patches 60°–150°; plots need the front cell. These were the storyboard's open
questions and were left at their defaults (2026-10-06).

### Superseded (kept for the record)
Before the grid we tried free parcels (2026-10-05), then 4 m chunks with style-made plots, then "organic" noise
(drifting, reverse-S and ragged plot lines from burgmap and CityEngine). The user found the noise just made the same
layout wavy and ugly, and chose the CS-style grid that follows the road instead. What carried over: plot rules belong
to the form, a style is the look, the player paints and never draws plots, corners face both streets, and the slope
rules. `docs/zoning-storyboard.html` is that earlier storyboard.

## 4. Buildings as content

A `.tres` file per building (or per set), like roads:
- `Style`, `Use`, `Density`, `Form`, `Level`.
- Footprint: width and depth, frontage needed, and a **range** where the model allows it (stretchable row
  houses).
- `MaxSlope`, corner-capable, plinth mesh.
- Variations (materials, colours, mirroring) plus a spawn weight. Spawning avoids the same model next door.
- Footprints in metres, with a front side and a setback, fitted to plots made of cells (procedural buildings on the
  cells come later and may replace whole models).
- Simulation numbers (households, jobs) kept separate, in Godot-free data like `HeightMap`.

## 5. Player control (the things people install mods for)

- **Zone tools:** brush, block fill and erase on cells; depth is painted.
- **Lock:** a building stops levelling and is never replaced (the *Plop the Growables* need).
- **Swap variant:** cycle a building to another model that fits the same plot (*Asset Variation Changer*).
- **Place a growable (not now, agreed 2026-10-05):** pick a specific building for a plot (Find It + Plop). Not built
  yet, but the data supports it from the start: a plot can hold a `PinnedBuilding` that the spawner never replaces.
- **Limits per style area:** maximum height and density (district height limits).
- Large buildings (RICO's job): a form's plots can be many cells wide, so High density has no size cap.

## 6. Growth (kept minimal in the experiment)

Feel over accuracy. The experiment needs only:
- A demand value per Use × Density (debug sliders, no economy yet).
- A spawn rate: empty plots pick a building, play a short construction phase, then become occupied.
- Optional: level-ups that swap the model.

The real economy, citizens, land value and abandonment come with the simulation later.

## 7. Where it lives (agreed 2026-10-05)

Two new packages, moved out of `experiments/roads` (moved, not rewritten):
- `packages/citysim_build_ui/`: the build bar, tray, cards, options panel shell, `UiTheme`, `GameHud`, and the
  content library (`BuildCategory`, `BuildTab`, `BuildItem`, `ContentLibrary`). Zoning adds a `zones.tres` build
  category and its own options panel. No UI from scratch.
- `packages/citysim_roads/`: road types, styles, rendering, junctions, crossings and lane links, with the road and
  terrain tools.
- `experiments/roads` keeps working on top of both packages (all its `--demo-*` checks still pass), and
  `experiments/zoning/` uses terrain + splines + roads + build UI.

## 8. Milestones
See `ROADMAP.md` (rewritten for the grid on 2026-10-06).

## References for parcel generation (background; the grid replaced the parcel approach)
- burgmap, pre-modern settlement generator (frontage runs, skeleton strips, ±6° tilt and ±12° field noise, stochastic
  depth, sliver merging, burgage-cycle infill): https://github.com/dunkean/burgmap (`web/URBAN_GEOMETRY.md`)
- CityEngine block parameters (irregularity, corner angle/width, lotAreaMin, shallowLotFrac):
  https://doc.arcgis.com/en/cityengine/latest/help/help-layers-block-parameters.htm
- Lot subdivision write-up (OBB parcelling, area/access/aspect/frontage rules):
  https://martindevans.me/game-development/2015/12/27/Procedural-Generation-For-Dummies-Lots/
- Reverse-S fields: https://hlamap.org.uk/types/1/agriculture-and-settlement/medievalpost-medieval-reverse-s-shaped-fields
- Burgage plots in Scottish medieval towns (measured widths, later splits and merges):
  https://www.researchgate.net/publication/370248143_The_archaeology_of_burgage_plots_in_Scottish_medieval_towns_a_review
- Vanegas et al., "Procedural Generation of Parcels in Urban Modeling" (Eurographics 2012): OBB and straight-skeleton
  block subdivision, the method behind CityEngine's lot splitting.
- CityEngine block parameters (lot area min/max, irregularity, corner alignment): https://doc.arcgis.com/en/cityengine/latest/help/help-block-parameters.htm

## Sources
- CS2 wiki, Zoning: https://cs2.paradoxwikis.com/Zoning
- CS2 wiki, Region packs: https://cs2.paradoxwikis.com/Region_packs
- CS2 feature highlight, Zones & Signature Buildings: https://www.paradoxinteractive.com/games/cities-skylines-ii/features/zones-signature-buildings
- "Zoning and Repetition – these are legitimate problems" (CS2 forum): https://steamcommunity.com/app/949230/discussions/0/530969911486369911/
- Zoned areas not filling (gaps): https://steamcommunity.com/app/949230/discussions/0/797839675763059733/
- Zoned buildings don't change when they level up: https://steamcommunity.com/app/949230/discussions/0/3877095833485093369/
- CS2 2025 wishlist thread: https://steamcommunity.com/app/949230/discussions/0/601892377795270473/
- Paradox forum suggestions (demand & zoning): https://forum.paradoxplaza.com/forum/threads/skylines-2-suggestion-map-editor-city-start-progression-demand-zoning.1628343/
- Building on slopes (CS2): https://steamcommunity.com/app/949230/discussions/0/3877095833479834862/
- Zoning Adjuster: https://steamcommunity.com/sharedfiles/filedetails/?id=2389414419
- Zone It!: https://steamcommunity.com/sharedfiles/filedetails/?id=1783307723
- Deeper zones (Simtropolis): https://community.simtropolis.com/forums/topic/69880-deeper-zones-well-why-not/
- Building Themes: https://steamcommunity.com/sharedfiles/filedetails/?id=466158459
- Custom Zone Mixer 2: https://steamcommunity.com/sharedfiles/filedetails/?id=2055972178
- RICO vs Plop the Growables: https://steamcommunity.com/app/255710/discussions/0/2479690531123604328/
- Manor Lords and organic growth: https://www.switchbladegaming.com/strategy-games/city-builder-evolution-2026/
- Metropolis 1998 (Steam): https://store.steampowered.com/app/2287430/Metropolis_1998/
- SimCity 2013 density from roads: https://en.wikipedia.org/wiki/SimCity_(2013_video_game)
- SimCity 4 diagonal lots: https://community.simtropolis.com/omnibus/simcity-4/tutorials/a-brief-introduction-to-diagonal-city-building-r286/
