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
- Next: stop lines and crosswalks at junctions (then lane arrows), textures and normal maps, LOD in the
  performance milestone.

### ⬜ Later
- Upgrades content type and tab (looks + traffic), Intersections, Parking.
- Mod loading from `.pck` files, a modder guide, more categories (Zones next).
