# CitySim Roads

The game's build UI (bottom bar, tray, options panel), the road types it offers, and the roads themselves: drawn with
the splines addon (`packages/citysim_splines`, symlinked as `addons/citysim_splines`) and rendered as real road meshes
(kerbs, sidewalks, markings, junctions). See ROADMAP.

## Content is data

Nothing in the tray is hard-coded. At start-up `ContentLibrary` reads resource files from:

- `res://content/**`: the base game.
- `user://mods/<mod>/**`: one folder per mod, read in name order after the base game. On macOS `user://` is
  `~/Library/Application Support/Godot/app_userdata/CitySim Roads/`.

Four kinds of file, each a `.tres` you can make and edit in the Godot inspector (New Resource → pick the type):

| Type | One per | Key fields |
|---|---|---|
| `BuildCategory` | bottom-bar button | `Id`, `DisplayName`, `Icon` (white SVG), `Order` |
| `BuildTab` | tab in a category's tray | `Id`, `Category` (a category id), `DisplayName`, `Order`, `DividerBefore` |
| `RoadType` (a `BuildItem`) | road card | `Id`, `DisplayName`, `Description`, `Tab` (a tab id), `Order`, `Icon` (optional), `Lanes`, `OneWay`, `ForwardLanes` (asymmetric split, 0 = even), `Surface`, `Sidewalks`, `Median`, lane/strip/sidewalk/median widths (`StripWidth`: each side, between the outer lane and the kerb) |
| `RoadStyle` | look of every road | `Id` (`default`), kerb height and top width, crown, gutter width, skirt depth, line width, centre and lane dash : gap, materials (asphalt, gravel, gutter, kerb, sidewalk, paint) |

Rules:

- Ids are unique per type. A later file with the same id replaces the earlier one, so a mod can change a base road.
  The log prints `Content: <mod> replaces item "<id>" from Base`.
- A tab with an unknown category, or an item with an unknown tab, is skipped with a warning.
- A category only shows on the bar once one of its tabs has an item.
- A road with no `Icon` gets a thumbnail drawn from its layout (`RoadThumbnail`).
- A road type holds the **base layout** only: lanes, sidewalks and median. Looks (trees, grass median, lights,
  paving) will be upgrades, a separate content type.

Road materials (`content/roads/materials/`) are `ShaderMaterial`s over three shaders in `content/roads/shaders/`
(`road_asphalt`, `road_concrete` for sidewalk, kerb and gutter, `road_paint`); colours, wear, crack and joint
settings are shader parameters, so a mod tunes them in the inspector or swaps in its own shader. They read
`content/roads/textures/` (tiling noise made by `--bake-road-textures`; a mod can replace them with photo textures).

To add a road: copy a file in `content/roads/types/`, change `Id` and the fields, done. `--demo-content` lists what
loaded and checks it.

## Code

- `src/Content/`: `BuildCategory`, `BuildTab`, `BuildItem` (base for every card type) and `ContentLibrary`.
- `src/Roads/`: `RoadType` (resource) → `ToDef()` → `RoadDef` (plain C#, no Godot types, for the simulation);
  `RoadThumbnail`; `RoadStyle`; `RoadProfiles` (a splines `SplineProfile` per road type); `RoadToolHost` (the splines
  tools' host: tray pick + options panel; `M` toggles Draw / Edit).
- `src/Roads/Geometry/RoadSection.cs`: the cross-section (bands, painted lines, outline) from a `RoadDef`, plain C#.
- `src/Roads/Rendering/`: `RoadVisual` (the splines addon's `INetworkVisual`: segments, markings, dead ends, halos;
  `.Junctions.cs`: footprints and hard corners) and `RoadMesh` (triangles per surface kind).
- `src/UI/`: `GameHud` (wires it up), `BuildBar`, `BuildTray`, `ItemCard`, `DetailCard` (hover card),
  `RoadOptionsPanel`, `UiTheme`.
- `src/Demos/UiDemo.cs`: `--demo-content` and `--ui=` for screenshots.
- `src/Demos/RoadDemo.cs`: `--demo-road[=<road id>]`, a test network (dead ends, T, 4-way, 60° T, curve) and checks.

Icons: `assets/icons/`, Tabler Icons (MIT), see `assets/icons/LICENSE.md`.
