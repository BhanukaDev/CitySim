# Roads ROADMAP

Consumer experiment for the splines addon: the game's build UI first, then drawing real roads. UI design came from
the research and wireframe at https://claude.ai/artifact/9btto4m8pVJzZD3jNTCbzP (Roads toolbar, v3).

## Decisions

- Road tabs list **base roads**: lanes plus the sidewalks and median they come with. Upgrades change how a road looks
  (trees, grass/tree median, lights, paving, kerbs), never its layout. Traffic items live in the same Upgrades tab.
- No prices, upkeep, speed or capacity in the UI until something simulates them.
- Content is `.tres` resources read from folders (`ContentLibrary`), so mods add or replace roads without code.
- Details show on hover (the card's tooltip). The options panel is always open next to the tray.
- Only options the splines Draw tool already has: modes 1–4, grid blocks and fit, snap groups, Anarchy.

## How to build / verify

```sh
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build
$G --headless --path . --import                                   # after adding icons or content
$G --headless --path . -- --flat --demo-content                   # lists content, prints "Demo content: all ok"
$G --path . -- --flat --ui=open:roads,tab:small,pick:two_lane,hover:two_lane --screenshot=screenshots/ui_small.png
$G --path . -- --flat --ui=open:roads,pick:two_lane,search:lane,mode:grid --screenshot=screenshots/ui_search.png
$G --headless --path . --quit-after 300 -- --flat --demo-road        # builds a test network, prints "Demo road: all ok"
$G --path . -- --flat --demo-road --cam=650,560,420,70,0 --screenshot=screenshots/road_top.png        # overview
$G --path . -- --flat --demo-road --cam=700,500,30,35,35 --screenshot=screenshots/road_junction.png   # the 4-way, low
$G --path . -- --flat --demo-road --cam=830,515,45,45,0 --screenshot=screenshots/road_skew.png        # the 60° T
$G --headless --path . --quit-after 400 -- --flat --demo-shape       # hill, dip, hillside T, undo, sculpt; "Demo shape: all ok"
$G --path . -- --flat --demo-shape --cam=600,500,70,22,60 --screenshot=screenshots/shape_cut.png       # cut through the hill
$G --path . -- --flat --demo-shape --cam=800,500,90,15,0 --screenshot=screenshots/shape_fill.png       # embankment over the dip
$G --path . -- --flat --demo-shape --cam=640,490,25,35,200 --screenshot=screenshots/shape_junction.png # T on the hillside
$G --path . -- --flat --demo-road --road-age=0.6 --cam=700,505,10,75,20 --screenshot=screenshots/road_age.png  # cracks close up
$G --headless --path . -- --flat --bake-road-textures              # only after changing RoadTextureBaker; then --import
$G --path . -- --flat --bake-road-thumbnails[=<id>]                # card pictures (roads and road tools), after a road or its look changes; imports itself
$G --headless --path . --quit-after 300 -- --flat --demo-crossings   # auto rules + Crossings tool clicks + undo; "Demo crossings: all ok"
$G --path . -- --flat --demo-crossings --crossing-cursor=760,503 --cam=640,500,190,70,0 --screenshot=screenshots/crossings_overview.png
$G --path . -- --flat --demo-crossings --crossing-cursor=699.5,501 --cam=700,500,35,45,25 --screenshot=screenshots/crossings_midblock.png
$G --path . -- --flat --demo-crossings --crossing-cursor=708,501 --cam=700,500,35,45,25 --screenshot=screenshots/crossings_refused.png
$G --path . -- --flat --ui=open:roads,pick:two_lane --demo-slope --cam=440,560,180,35,30 --screenshot=screenshots/slope_preview.png  # slope pills, red grade
```

`--ui=` parts, applied in order: `open:<category>`, `tab:<tab>`, `pick:<item>`, `search:<text>`,
`mode:straight|curve|freehand|grid`, `anarchy`, `hover:<item>` (shows the hover card above the item's card).
The terrain flags (`--flat`, `--load=`, `--cam=`, `--screenshot=`) come from the terrain package.

## Road checklist

All 33 road types are in `content/roads/types/`, but only the ones ticked here are shown in game. The rest have
`Hidden = true` in their `.tres` file. To bring one in, delete that line, test it in Godot, then tick it here.
Tabs with no visible roads are hidden too.

**Small**
- [ ] Two-lane road (`two_lane`): visible, being tested
- [ ] Three-lane asymmetric road (`three_lane_asymmetric`)
- [ ] One-lane one-way road (`one_way_one_lane`)
- [ ] Two-lane one-way road (`one_way_two_lane`)
- [ ] Three-lane one-way road (`one_way_three_lane`)
- [ ] Gravel road (`gravel`)
- [ ] Gravel one-way road (`gravel_one_way`)
- [ ] Alley (`alley`)
- [ ] One-way alley (`alley_one_way`)

**Medium**
- [ ] Four-lane road (`four_lane`)
- [ ] Four-lane divided road (`four_lane_divided`)
- [ ] Four-lane asymmetric road (`four_lane_asymmetric`)
- [ ] Five-lane asymmetric road (`five_lane_asymmetric`)
- [ ] Four-lane one-way road (`one_way_four_lane`)
- [ ] Five-lane one-way road (`one_way_five_lane`)

**Large**
- [ ] Six-lane road (`six_lane`)
- [ ] Six-lane divided road (`six_lane_divided`)
- [ ] Eight-lane divided road (`eight_lane_divided`)
- [ ] Six-lane asymmetric road (`six_lane_asymmetric`)
- [ ] Seven-lane asymmetric road (`seven_lane_asymmetric`)
- [ ] Seven-lane asymmetric road, 5 + 2 (`seven_lane_asymmetric_wide`)
- [ ] Six-lane one-way road (`one_way_six_lane`)
- [ ] Seven-lane one-way road (`one_way_seven_lane`)

**Highways**
- [ ] Two-lane two-way highway (`highway_two_lane_two_way`)
- [ ] Three-lane two-way highway (`highway_three_lane_two_way`)
- [ ] Four-lane two-way highway (`highway_four_lane_two_way`)
- [ ] Four-lane asymmetric highway (`highway_four_lane_asymmetric`)
- [ ] Five-lane asymmetric highway (`highway_five_lane_asymmetric`)
- [ ] One-lane one-way highway (`highway_one_lane`)
- [ ] Two-lane one-way highway (`highway_two_lane`)
- [ ] Three-lane one-way highway (`highway_three_lane`)
- [ ] Four-lane one-way highway (`highway_four_lane`)
- [ ] Five-lane one-way highway (`highway_five_lane`)

## Milestones

### ⬜ R1: Build UI with roads from data (built 2026-10-05, waiting for the user's play-test)
- `BuildCategory` / `BuildTab` / `BuildItem` resources and `ContentLibrary` (base + `user://mods/<mod>/`, replace by id,
  warnings for broken links). Checked with a throwaway mod: a new road and a replaced base road both loaded.
- 1 category, 4 tabs (Small, Medium, Large, Highways), 33 base roads in `content/`, taken from the CS2 road list
  (https://cs2.paradoxwikis.com/Roads): plain, one-way, divided, asymmetric (`ForwardLanes`), gravel (`Surface`), alleys,
  two-way and one-way highways. Left out until the feature exists: parking roads (Parking), pedestrian streets,
  quays and bridges.
- Bottom bar, tray (tabs show names only, search over all tabs with "in <tab>" labels, horizontal card scroll), hover card,
  roads options panel (modes, grid blocks/fit, snapping master + 4 groups, Anarchy). Keys: 1–4, Ctrl+A, `/`, Esc.
- Options are UI state only (`RoadToolOptions`); nothing draws yet.

### ⬜ R2: The small two-lane road (built 2026-10-05, waiting for the user's play-test)
Dimensions from the research doc "Small Two-Lane Road: Reference Dimensions"
(https://claude.ai/code/artifact/bc8f288d-a2ff-4311-860f-a9642857851f): 16 m = sidewalk 3 | strip 2 | lane 3 | lane 3 |
strip 2 | sidewalk 3, kerb 0.15 m, crown 2 %, corner kerb radius 4 m, European white markings (0.12 m, centre 3 : 6).
- The splines addon is now `packages/citysim_splines/` (symlinked as `addons/citysim_splines`). `RoadToolHost`
  (`ISplineToolHost`) drives its Draw and Edit tools from the tray pick and the options panel (`M` = Edit).
  `RoadProfiles` makes a `SplineProfile` per road type (the addon's kerb radius is at the back of the sidewalk, so
  it's 4 m minus the sidewalk).
- `RoadType.StripWidth`: room between the outer lane and the kerb (gutter now, parking or a bike lane as a later
  upgrade, same road width). Only `two_lane` sets it so far.
- `RoadStyle` (`content/roads/styles/default.tres`): kerb, crown, gutter and marking numbers, and the materials
  (`content/roads/materials/`, plain colours for now).
- `RoadSection` (plain C#): bands and painted lines from a `RoadDef`, generic over lanes, strips, medians, sidewalks.
- `RoadVisual` (`INetworkVisual`): segments extruded from the section (real kerb geometry, skirt into the ground,
  crown fading out 4 m before a junction), painted lines as flat strips 12 mm up, end faces at dead ends. Junctions:
  the footprint outline inset by the sidewalk (mouths kept open), giving flat asphalt, a gutter and kerb face round
  the corner, kerb stone and sidewalk. Hard corners: a fan. Issue halos under anything with a problem.
- Every road type draws with this; the others have no strips yet, and medians are a raised kerbed band. All but
  `two_lane` are hidden (`BuildItem.Hidden`) and come back one at a time through the road checklist above.
- Next: stop lines and crosswalks at junctions (then lane arrows), LOD in the performance milestone. Textures and
  normal maps: done in R4.

### ⬜ R3: Roads on hills (built 2026-10-05, waiting for the user's play-test)
- Roads don't roll: each road has a stored height line and is level across; junctions are level at their node's height.
- Per road type, set in its `.tres` (Elevation group, not in the game's UI): `MaxGrade` (%, default 10),
  `GroundSmoothing` (m along the road the ground is averaged over, default 30, only to take out bumps), `SideSlope`
  (bank run per rise, default 2.5 ≈ 22°), `MaxCut` / `MaxFill` (m, default 4).
- **Nodes go exactly where they're placed**, at the ground there (user decision, 2026-10-05). Ends too far apart in height
  for `MaxGrade` (`grade`), or a road that would cut deeper or be raised higher than `MaxCut` / `MaxFill` (`cut`, `fill`),
  are red in the preview and refused unless Anarchy is on.
- The ground is shaped to the road on build and on Edit release: level under it plus a 3.5 m apron, then banks at the
  side slope until they meet the natural ground (nothing on flat ground). Same undo step as the road. Delete leaves the
  ground as it is.
- Roads never move with the ground: a later terrain edit round a road is shaped back once the stroke ends (its own
  terrain undo step). The roads experiment has no terrain tools yet, so `--demo-shape` stands in for one.
- Built in the splines addon as the first part of its S8 (`Vertical`, `GroundShaping`); see its README.
- The road mesh sits 12 cm above the shaped ground (was 4 cm): the terrain renderer rounds a level junction into the
  slope beside it a few centimetres high and showed through the gutters.
- Not yet: retaining walls (later, by request), bridges/tunnels, a Shape ground toggle, a cut/fill readout while drawing.
- While drawing, each leg's pill shows its slope in the drawing direction (`↔ 120 m · ↗ 12 %`), red past `MaxGrade`.
  Ends too far apart in height get one even ramp, so the grade shown (and in the red issue) is the grade they need.
  Tag symbols are Tabler icons (`packages/citysim_splines/icons/`): length, angle, slope up/down, cut, fill.
- The draw length tag is just the length now (the `· 10 × 8 m` step count is gone; whole steps still light it up).
- For the play-test: do the grade and banks feel right on real hills? Are 10 % and 4 m cut / fill right for the two-lane road?
- Junctions on a slope: the road eases from the level junction to its grade over `JunctionCurve` (Elevation group,
  default 20 m) instead of breaking hard at the junction's edge (the "bumps"). It's the splines addon's
  `ProfileRules.JunctionCurve`: the allowed grade rises from 0 at the cut-back to `MaxGrade` over it, so the line
  leaves the plate on a parabola. Longer is smoother but needs more cut / fill near the junction (24 m pushed the
  demo's hillside T to 4.1 m of fill, red).

### ⬜ R4: Road materials (built 2026-10-05, waiting for the user's play-test)
- Real-looking surfaces for asphalt, sidewalk, kerb, gutter and paint: three shaders in `content/roads/shaders/`
  (`road_asphalt`, `road_concrete`, `road_paint`, sharing `road_common.gdshaderinc`), with the `.tres` materials as
  `ShaderMaterial`s whose parameters are the knobs.
- Asphalt: weathered grey binder with ~1.5 cm stones and a normal map, patches, **wheel tracks polished lighter and an
  oil stripe down the middle of every lane**, oil spots near it, scuffing across junctions. The tracks follow a lane
  coordinate the mesh carries (`UV2.x`, `RoadSection.LaneCoord`), so any lane count works; strips and gutters stay clean.
- Concrete: grain, stains, small dark spots, joints along the road (sidewalk 1.5 m, kerb stones 1 m, gutter 3 m) with
  a tone per slab and the odd cracked slab. Junction paving has no joints (no road direction there).
- Paint: frayed sides and dash ends, worn-through patches (alpha scissor, so the asphalt shows), stones showing
  through, dirt.
- **Cracks come from the road's age** (`RoadVisual.AgeOf`, 0..1, vertex `COLOR.r`): the game raises it as roads get
  old or damaged (disasters), never the player. Every road has a few light cracks from the start (the asphalt
  material's `base_age`, 0.45); age adds on from there to most of the road at 1. The age also
  greys the asphalt and wears the paint. A junction takes its oldest arm. `--road-age=` previews it.
- Traffic wear is per vertex too (`UV2.y`, 1 for every road until traffic is simulated).
- Performance (the map is 28 × 28 km): noise is baked once into three tiling 1024² textures (BPTC, mipmapped, about
  4 MB of VRAM together), not computed per pixel, and sampled in world metres so it's seamless across roads and
  junctions. Stones, normals and fraying fade out by 150 m, cracks by 350 m, and those texture reads are skipped
  beyond that. Wheel tracks and joints fade to their average once they're under a pixel, so distant roads don't
  shimmer. All per-road data is in the vertices, so every road shares one material per surface: still a handful of
  draw calls for the whole network. Chunking the network mesh and LOD stay in the performance milestone.
- For the play-test: do the greys look right in daylight? Are the wear stripes too strong or too weak up close? Do
  the cracks at `--road-age=0.3`, `0.6` and `1` feel right?
- **Junction wear**: tyre wear ribbons over every junction along the paths cars take (`RoadVisual.Tracks`): straight on
  from every lane, right turns kerbside to kerbside, left turns inside to inside, as cubic curves square to the cuts.
  They carry the lanes' wheel tracks and oil stripe on across the junction (`road_wear.gdshader`, blended over the
  asphalt as `SurfaceKind.Wear`), so wear no longer stops hard at the junction's edge, and turns add black rubber. Each
  ribbon's strength is its lane's traffic share (straight 55 %, right 25 %, left 20 %, normalised per lane). The
  junction's blanket scuffing is down to 0.15.
- **Queuing stains**: the lanes leading into a junction get oil and drips down their middle for ~45 m behind the stop
  line (per direction, in vertex `COLOR.gba`).
- **Junction markings** (Vienna Convention protocol on road markings): a zebra crossing across each mouth with
  sidewalks (bars 0.5 m, gaps 0.5 m, 3 m long, 0.5 m from the junction), a 0.3 m stop line 1 m behind it across the
  lanes coming in, every line ending at the stop line, and the centre line and lines between lanes coming in solid for
  the last 15 m. All numbers in `RoadStyle` (Markings group). Paint UVs fixed, so the sides of lines fray now, and a wide
  line follows the crown.
- Straight is the default draw mode. Closing the Roads tray turns the road tools off (Draw gets no road, Edit goes back
  to Draw, `M` only works with the tray open); before, the picked road kept drawing after the tray closed.
- **Colours**: asphalt to measured values (worn ≈ albedo 0.12, sRGB ~90, slightly warm), concrete a touch warmer; the
  terrain package's default theme less lime (grass, dry grass, grass & dirt and dirt tints, and its height tints).

### ✅ Road thumbnails (built 2026-10-05, play-tested 2026-10-05)
- Dev tool, not game code: `--bake-road-thumbnails[=<id>]` (`RoadThumbnailBaker`) renders every road type in
  `content/roads/types/` (hidden ones too) as a straight piece of real road (`RoadVisual`, the style's materials) on
  flat grass, three-quarter view, framed to the road's width, 240 × 140 (2× the card picture). The PNG goes to
  `content/roads/thumbnails/<id>.png` and the road's `.tres` gets `Icon` pointing at it (a text edit; a road with a
  hand-made Icon keeps it). Needs a window; runs `--import` itself at the end, because a road type linked to a PNG
  Godot hasn't imported yet doesn't load.
- Re-bake after changing a road's layout, the road style or its materials. Gravel shows as plain grey because its
  material still is.
- For the play-test: is the angle/zoom right? Should the thumbnail show a junction (crossing, stop line) instead of
  a straight piece?

### ⬜ R5: Crossings tool (built 2026-10-05, waiting for the user's play-test)
- Roads → **Services** tab (after a divider, next to Small) → **Crossings**: a `RoadTool` card (`content/roads/tools/`),
  not a road. While it's picked, Draw idles and `M` does nothing; the options panel shows its mouse hints instead of the
  draw options.
- Every arm of a node is a crossing place (where the junction crossing already went). Three states per arm
  (`CrossingMode`): **Auto** (untouched), **Yes** (LMB), **No** (RMB). Yes and No stay; there's no way back to Auto
  (user decision, 2026-10-05) other than Undo.
- **LMB anywhere along a road** splits it with a node there and sets a crossing on it, centred on the mouse (3 m kept
  clear of the road's ends for stop lines). Auto never puts crossings along a road. RMB on that crossing takes it and its
  node away again, and the road is one edge again. A crossing along a road paints the zebra, a stop line on each side and
  solid centre lines on the approach (user decision), like a junction mouth.
- Auto rules (`Crossings.Resolve`): a crossing on every arm of a junction; none at a dead end, a road's own node or a
  change of road width (these had a stop line and crossing before; now only if set to Yes). An auto crossing closer than
  `RoadStyle.CrossingMinGap` (25 m, zebra to zebra) along the road to another crossing is dropped: a forced one stays, else
  the busier junction's. A crossing that doesn't fit between two junctions isn't drawn (forced ones too: there's no room
  to paint it); a junction arm keeps its stop line either way. Only paved roads with sidewalks get crossings.
- Stored in the splines graph as per-end data (`GraphEdge.DataStart` / `DataEnd`, new in the addon), so it goes through
  undo/redo, splits, merges and Edit drags with its end.
- **Select, then edit** (play-test feedback): LMB on a junction (or a crossing's node) selects it; only the selected
  node's arms are outlined and take LMB / RMB. Hovering a node shows a dashed ring and "LMB select junction". LMB along a
  road away from nodes adds a crossing and selects its node. Esc or a click on nothing clears the selection. The overlay
  draws one node, so it costs the same on any size of network (the mouse pick still checks every edge: S11 spatial index).
- Overlay on the selected node: white outline = auto crossing, dashed = auto arm with none, green = Yes, amber = Yes
  without room, red with an X = No; blue ghost = a new crossing along the road. Hover tag says the arm's state.
- **No kink on slopes** (play-test feedback): splitting a road for a crossing used to give each piece a new height line,
  which bent the road at the node (1.09 m on the demo's 10 % hill). The splines addon's `SplitEdge` now hands the old
  line on (the node takes its height there, each piece its part) and `TryMerge` joins two fitting lines back, so the road
  doesn't move (0.5 cm, resampling). Not the junction ease-in: that would put a level spot at the crossing with humps
  either side. Editing one side later still makes that piece a new line.
- **Can't place, can't** (play-test feedback): LMB tries the change on a copy of the network first. It's refused (red
  ghost, a tag saying why, the click does nothing) when it would raise an issue the road didn't have (a short piece
  between two crossings showed as a red "overlaps" halo that stayed after closing the tool), when the zebra wouldn't
  fit, when it would push a crossing you set off its road, or when it's within 12 m of another crossing that stays.
  Setting Yes on a junction arm with no room is refused the same way. Automatic crossings still give way by their rules.
- **Wider crossings**: the dashed place on the other side of a crossing along a road is a real arm. LMB there ("LMB
  widen crossing") puts a second zebra back to back: one 6 m crossing.
- Options panel text cut down to four mouse hints; the description is one line.
- Card picture: a real render (a two-lane road with a crossing, closer in than the road cards), made by `--bake-road-thumbnails[=crossings]`, which now
  also renders the cards in `content/roads/tools/`.
- For the play-test: is 25 m the right "too close"? Should Auto also skip crossings on very short side roads, or on
  junctions of small roads only? Should the tool show every arm's place, not just junctions' (now: on hover)?

### ⬜ Later
- Upgrades content type and tab (looks + traffic), Intersections, Parking.
- Mod loading from `.pck` files, a modder guide, more categories (Zones next).
