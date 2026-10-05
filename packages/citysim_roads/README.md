# citysim_roads

The road types, the roads themselves and the terrain tools, as a package every game project shares. Roads are drawn
with the splines addon and rendered as real road meshes (kerbs, sidewalks, markings, junctions); the Roads and Terrain
categories plug into the build UI (`packages/citysim_build_ui`). Moved out of `experiments/roads` in zoning's Z0
(2026-10-06); that experiment is still where roads are built and checked, so see its ROADMAP.

## Adding it to a project

It needs all four other packages. Symlink them into the project's `addons/` (from `experiments/<name>/addons/`):

```sh
ln -s ../../../packages/citysim_terrain addons/citysim_terrain
ln -s ../../../packages/terrain_3d addons/terrain_3d
ln -s ../../../packages/citysim_splines addons/citysim_splines
ln -s ../../../packages/citysim_build_ui addons/citysim_build_ui
ln -s ../../../packages/citysim_roads addons/citysim_roads
```

Then put the nodes in the main scene in this order (`experiments/roads/scenes/Main.tscn` is the reference): `Terrain`,
`CityCamera`, `GameHud`, `SplineNetwork`, `RoadToolHost` (`Hud`, `Network`), the splines `SplineDrawTool` and
`SplineEditTool` (`HostNode` = the `RoadToolHost`), `CrossingTool`, `LaneLinkTool` and `TerrainToolController`. The
Roads and Terrain options panels add themselves to the HUD (`RoadsHud`: `hud.RoadOptions()`, `hud.RoadsOpen()`,
`hud.PickedRoadTool()`, the same for terrain).

## Content is data

Nothing in the tray is hard-coded. At start-up `ContentLibrary` reads resource files from:

- `res://addons/<package>/content/**`, then `res://content/**`: the base game. This package's content is in
  `content/` here (`res://addons/citysim_roads/content/`).
- `user://mods/<mod>/**`: one folder per mod, read in name order after the base game. On macOS `user://` is
  `~/Library/Application Support/Godot/app_userdata/CitySim Roads/`.

Four kinds of file, each a `.tres` you can make and edit in the Godot inspector (New Resource → pick the type):

| Type | One per | Key fields |
|---|---|---|
| `BuildCategory` | bottom-bar button | `Id`, `DisplayName`, `Icon` (white SVG), `Order` |
| `BuildTab` | tab in a category's tray | `Id`, `Category` (a category id), `DisplayName`, `Order`, `DividerBefore` |
| `RoadType` (a `BuildItem`) | road card | `Id`, `DisplayName`, `Description`, `Tab` (a tab id), `Order`, `Icon` (optional), `Lanes`, `OneWay`, `ForwardLanes` (asymmetric split, 0 = even), `Surface`, `Sidewalks`, `Median`, lane/strip/sidewalk/median widths (`StripWidth`: each side, between the outer lane and the kerb), `ZoneRows` (zoning cells each side, 0–8; highways 0) |
| `RoadTool` (a `BuildItem`) | tool card (Crossings, Lane Links) | `Id`, `DisplayName`, `Description`, `Tab`, `Icon`, `Tool` (which tool the game runs: `crossings` or `lane_links`), `Usage` (mouse hints for the options panel) |
| `RoadStyle` (an `IContent`) | look of every road | `Id` (`default`), kerb height and top width, crown, gutter width, skirt depth, line width, centre and lane dash : gap, materials (asphalt, gravel, gutter, kerb, sidewalk, paint) |

Rules:

- Ids are unique per type. A later file with the same id replaces the earlier one, so a mod can change a base road.
  The log prints `Content: <mod> replaces item "<id>" from Base`.
- A tab with an unknown category, or an item with an unknown tab, is skipped with a warning.
- A category only shows on the bar once one of its tabs has an item.
- Base roads' `Icon`s are renders of the real road, made by the dev command `--bake-road-thumbnails` into
  `content/roads/thumbnails/` (see ROADMAP). A road with no `Icon` (a mod's) gets a picture drawn from its layout
  (`RoadThumbnail`); a hand-made `Icon` is never replaced by the bake.
- A road type holds the **base layout** only: lanes, sidewalks and median. Looks (trees, grass median, lights,
  paving) will be upgrades, a separate content type.

Road materials (`content/roads/materials/`) are `ShaderMaterial`s over three shaders in `content/roads/shaders/`
(`road_asphalt`, `road_concrete` for sidewalk, kerb and gutter, `road_paint`); colours, wear, crack and joint
settings are shader parameters, so a mod tunes them in the inspector or swaps in its own shader. They read
`content/roads/textures/` (tiling noise made by `--bake-road-textures`; a mod can replace them with photo textures).

To add a road: copy a file in `content/roads/types/`, change `Id` and the fields, done. `--demo-content` lists what
loaded and checks it.

## Code

Paths are in this package. The content model (`BuildCategory`, `BuildTab`, `BuildItem`, `ContentLibrary`) and the HUD
are in `citysim_build_ui`.

- `src/Roads/`: `RoadType` (resource) → `ToDef()` → `RoadDef` (plain C#, no Godot types, for the simulation);
  `RoadTool` (a tool card); `Crossings` (plain C#: each arm's `CrossingMode`, kept on the edge end as a `RoadEnd`, and
  the rules that place crossings and stop lines); `CrossingTool` (the Crossings tool and its overlay); `LaneLinks` (plain C#: the road's rule for which lane goes
  where across a junction, and the player's links kept on `RoadEnd`); `LaneLinkTool` (the Lane Links tool and its overlay);
  `RoadThumbnail`; `RoadStyle`; `RoadProfiles` (a splines `SplineProfile` per road type); `RoadToolHost` (the splines
  tools' host: tray pick + options panel; `M` toggles Draw / Edit).
- `src/Roads/Geometry/RoadSection.cs`: the cross-section (bands, painted lines, outline) from a `RoadDef`, plain C#.
- `src/Roads/Rendering/`: `RoadVisual` (the splines addon's `INetworkVisual`: segments, markings, dead ends, halos;
  `.Junctions.cs`: footprints and hard corners), `RoadMesh` (triangles per surface kind), and the dev bakers
  `RoadTextureBaker` and `RoadThumbnailBaker`.
- `src/Terraform/`: `TerrainTool` (a tool card), `TerrainToolController` (the Terrain tray's tools), `TerrainThumbnailBaker`.
- `src/UI/`: `RoadOptionsPanel`, `TerrainOptionsPanel` (each an `IOptionsPanel`) and `RoadsHud` (the HUD extensions
  that add them).
- The demos (`--demo-road`, `--demo-content`, `--ui=`, ...) stay in `experiments/roads/src/Demos/`.

Icons come from the build UI package (`res://addons/citysim_build_ui/icons/`).
