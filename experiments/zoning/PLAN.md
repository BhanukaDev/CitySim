# Zoning: Plan (agreed 2026-10-06, no code yet)

The player paints land use along roads and buildings grow on their own, as in Cities: Skylines. They never place a
specific growable building (with one exception, see Player control). This file is the plan agreed so far, written before
`ROADMAP.md`, `DESIGN.md` and a storyboard exist. All choices are settled; the user picked between options on
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
| **Form** | The kind of building within the density | Spawner | Detached, Semi, Row (Low); Row, Walk-up, Perimeter block (Medium) |
| **Style** | What it looks like | Spawner, visuals | Eastern Europe, US Suburban, UK, Nordic |

- A zone card is a `.tres` content file (made in Godot, dropped in by mods/DLC) that names a style, use, density and
  optionally a form. A card without a form mixes the style's forms by weight. A card with one (**EU Row Houses**) grows
  only that form. That's how the player picks detached vs rows vs flats.
- The simulation reads only Use × Density (and later Level). Style and form are content, so a new region pack adds
  cards and buildings, not new rules.
- Tray tabs group the cards by use (Residential, Commercial, Office, Mixed), so a tab doesn't fill up as packs are
  added. (Industry is left out for now and fits the same model later.)
- **Agreed:** 3 densities (Low, Medium, High) plus Mixed.
- Re-zoning a parcel with another card (another style or density) replaces its building over time. That's how
  "redevelop the suburb" works. A style-only brush (re-style without re-zoning) can come later.

**Agreed (2026-10-05): 3 building levels.** Each level raises capacity (households, jobs) and consumption. They're
not the focus now: the data has a `Level` field, and the visuals and rules come later.

## 3. Land: freeform parcels (agreed 2026-10-05)

There's no cell grid. Land is split into **parcels**: polygons that follow the roads, their curves and the land, as
Manor Lords' burgage plots and real cadastres do. The parcel is the unit of zoning: a zone is painted onto parcels,
and one parcel grows one building.

### How parcels are made
1. **Blocks.** The road network's faces (the areas enclosed by roads, from the splines graph) are the blocks, inset by
   each road's half-width plus pavement. Open land beside a road without a closed block gets a strip
   `MaxParcelDepth` deep along that side.
2. **Frontage bands.** Each block edge facing a road gets a band of the zone's depth, cut where bands meet (on the
   angle bisector, like a straight skeleton). Nothing is left between two roads' zones as dead overlap.
3. **Split along the frontage.** Each band is cut into parcels by lines **perpendicular to the road at that point**,
   so on a curve the parcels fan out like real plots. Widths are **mixed**, drawn from the style's building widths, not
   split into equal plots.
4. **Corners.** Where two bands meet at a block corner, one corner parcel faces both streets (corner buildings and
   corner shops).
5. **Interior and slivers.** A deep block's core becomes back gardens (parcels deepen) or an interior courtyard
   (perimeter blocks). Thin triangles become gardens, trees or parking, never bare grass.
6. **Determinism.** The same roads and settings give the same parcels (seeded), so a save stores only the inputs plus
   the player's edits.

### What the player does
- **Zone brush / fill:** paint the selected zone card over an area. Every parcel the brush covers takes it, and Fill takes the
  whole block. The parcels highlight as you paint.
- **Depth:** a handle per road side for how deep the parcels go (one-sided zoning = 0).
- **Parcel edits (optional, map-editor first):** drag a dividing line, split or merge parcels (Manor Lords-style
  control). Edits are stored as overrides and survive a re-split when they still fit.
- **Road changes:** moving or deleting a road rebuilds the parcels of the affected blocks. New parcels inherit zone and
  style from the old ones by area overlap. A building whose parcel is gone, or whose zone or style changed, is
  replaced over time, not instantly.

### Buildings on parcels
- A building's footprint must fit inside the parcel, with its front on the frontage line (or set back by the style's
  setback). The rest of the parcel is the building's yard (garden, driveway, parking), drawn from the style.
- Depth is **up to** the parcel depth (CS1's rule, not CS2's), so a shallow house leaves a garden instead of a deep copy.
- Wedge-shaped parcels (curves) use the inner rectangle. Later, styles can supply **modular** buildings (row houses,
  perimeter blocks made of pieces that follow the frontage), which fixes curves and repetition for European blocks.
- **Slopes:** each parcel levels a pad through the terrain edit API (the same undo as roads). A building has a
  `MaxSlope` and a **plinth** (a basement or foundation mesh) to hide the step, so it doesn't cut the terrain into
  terraces. Parcels that are too steep stay as gardens, or grow a style's hillside variant.

### Data
- Parcels are polygons in `System.Numerics` with no Godot types (like `HeightMap`). Each holds its block, its
  frontage edge(s), its zone, style, building and lock flag. The simulation reads parcels, not meshes.
- **Agreed (2026-10-05): mostly rectangular, organic where the land asks for it.** On straight roads and regular
  blocks, parcels come out rectangular (players like them). On curves, odd angles, block corners and around obstacles
  they take organic shapes, never forced into rectangles that leave gaps. Each style has an **irregularity** setting
  (0 = tidy rectangles, like a US suburb; higher = uneven widths and slightly skewed side lines, like an old European
  town).
- Default sizes, set per road type and style: depth Low 30–45 m, Medium 25–40 m, High 30–60 m. Widths come from the
  style's buildings (Low ~12–25 m, rows ~5–8 m).

## 4. Buildings as content

A `.tres` file per building (or per set), like roads:
- `Style`, `Use`, `Density`, `Form`, `Level`.
- Footprint: width and depth, frontage needed, and a **range** where the model allows it (stretchable row
  houses).
- `MaxSlope`, corner-capable, plinth mesh.
- Variations (materials, colours, mirroring) plus a spawn weight. Spawning avoids the same model next door.
- Footprints in metres (no cells), with a front side and a setback.
- Simulation numbers (households, jobs) kept separate, in Godot-free data like `HeightMap`.

## 5. Player control (the things people install mods for)

- **Zone tools:** brush, block fill and erase on parcels, depth per side.
- **Lock:** a building stops levelling and is never replaced (the *Plop the Growables* need).
- **Swap variant:** cycle a building to another model that fits the same parcel (*Asset Variation Changer*).
- **Place a growable (not now, agreed 2026-10-05):** pick a specific building for a parcel (Find It + Plop). Not built
  yet, but the data supports it from the start: a parcel can hold a `PinnedBuilding` that the spawner never replaces.
- **Limits per style area:** maximum height and density (district height limits).
- Large buildings (RICO's job): parcels can merge, so High density has no size cap.

## 6. Growth (kept minimal in the experiment)

Feel over accuracy. The experiment needs only:
- A demand value per Use × Density (debug sliders, no economy yet).
- A spawn rate: empty parcels pick a building, play a short construction phase, then become occupied.
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

## 8. Draft milestones

- **Z0:** move to packages: build UI and roads into packages, with `experiments/roads` unchanged in behaviour.
- **Z0.5:** storyboard (HTML, one frame per control and case: straight, curve, corner, T, cul-de-sac, slope, depth
  handle, zone cards, road moved under parcels). Agreed before parcel code, as with splines.
- **Z1:** blocks and frontage bands from the road graph, debug draw, rebuilt on road edits.
- **Z2:** parcel split: mixed widths, perpendicular cuts on curves, corners, slivers, determinism.
- **Z3:** zone tools (brush, fill, erase, depth handle) and zone cards from `.tres`, inheriting across road
  edits.
- **Z4:** buildings from content: placeholder boxes per style and form, fitting, setbacks, yards, pads with plinths,
  variation.
- **Z5:** zone cards in the tray (style + use + density + form, `Low · Res` pill), two test styles ("Eastern Europe"
  and "US Suburban") with box sets of different shapes.
- **Z6:** growth: demand sliders, spawn, construction, replacement on re-zone.
- **Z7:** control: lock, swap variant, height limit, parcel edits. (Place growable later; the data is ready for it.)
- Later: Mixed use, levels, modular buildings, industry, performance at scale.

## References for parcel generation
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
