# Splines Experiment

The testbed for the **generic spline addon** that roads, rails, canals, fences and walls all build on (each gets its own
consumer experiment later). Runs on the shared terrain package (`packages/citysim_terrain`, symlinked as
`addons/citysim_terrain`; API in its README). So far: the addon skeleton (profiles, options bar), the Core
alignment geometry, the Draw tool, snapping, guides and draw feedback matching the storyboard, and the graph with
junctions, validation, Anarchy and undo (S0-S4). See the roadmap.

- `ROADMAP.md`: status, milestones (S0–S11), build/verify commands, decisions. **Read first.**
- `DESIGN.md`: the spec (controls, data model, profiles, snapping, junctions, hooks).
- `docs/spline-controls.html`: the storyboard of the controls (open in a browser). **The target** for how the tools
  look and behave.

## Run

Needs the package's native libraries and textures (see the package README; the terrain experiment's
`tools/fetch_textures.sh` does the textures). Then:

```sh
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build && $G --headless --path . --import
$G --path .                                         # a generated map; or -- --load=path.csmap / --flat / --preset=island
$G --path . -- --screenshot=out.png --cam=1300,700,120,25,30
$G --headless --path . --quit-after 200 -- --demo-edit       # edit API check: prints "Demo edit: all ok"
$G --headless --path . --quit-after 200 -- --demo-geometry   # S1 Core geometry: prints "Demo geometry: all ok"
$G --headless --path . --quit-after 200 -- --demo-draw       # S2 Draw tool: prints "Demo draw: all ok"
$G --headless --path . --quit-after 200 -- --demo-snap       # S3 snapping: prints "Demo snap: all ok"
$G --headless --path . --quit-after 200 -- --demo-junctions  # S4 graph + junctions: prints "Demo junctions: all ok"
$G --path . -- --flat --storyboard=corner --screenshot=out.png --cam=560,500,340,89,0   # one storyboard frame
```

Controls: WASD move · Q/E rotate · R/F tilt · Z/X or mouse wheel zoom. Drawing: see DESIGN.md → Player controls;
Ctrl+A toggles Anarchy, and with no draw in progress Ctrl+Z / Ctrl+Y undo/redo and Del deletes the edge under the
cursor.

## Layout

- `src/App.cs`: start-up wiring (`TerrainCommandLine.Use()`: the map comes from the command line)
- `src/Splines/Core/`: the addon's engine-agnostic part (`System.Numerics` only): `ProfileRules`, enums,
  `IGround`, `DrawSession`, `Geometry/` (`Alignment`, `AlignmentOps`, `Curve`, segments, `RibbonGeometry`),
  `Snapping/` (`SnapEngine`, `SnapTypes`), `Graph/` (`SplineGraph`, `Junctions`, `Validation`). Moves to
  `packages/citysim_splines/` once S2 has been tried in Godot.
- `src/Splines/Godot/`: the addon's Godot part: `SplineProfile` resource, `SplineOptionsBar`, `TerrainGround`
  (`IGround` over `citysim_terrain`), `SplineNetwork` (the built graph, undo, issues, visuals), `SplineDrawTool` (the
  Draw tool), `RibbonRenderer` (preview ghost, built ribbons, footprints, halos), `SplineOverlay` (screen-space
  guides, rings, ∡ marks, knobs and tags), `SplineIssueList`
- `profiles/`: test profiles (`street`, `avenue`, `highway`, `rail`, `canal`, `fence`). Placeholders, testbed only.
- `src/SplinesTestbed.cs`: loads the profiles, shows the options bar, `SelectProfile` for scripted demos
- `src/EditDemo.cs`: `--demo-edit`, the package's `Terrain.BeginEdit` / undo / events check
- `src/Demos/GeometryDemo.cs`: `--demo-geometry`
- `src/Demos/DrawDemo.cs`: `--demo-draw`
- `src/Demos/SnapDemo.cs`: `--demo-snap`
- `src/Demos/JunctionDemo.cs`: `--demo-junctions`
- `src/Demos/StoryboardDemo.cs`: `--storyboard=<frame>`, rebuilds a storyboard frame for a screenshot
- `scenes/Main.tscn`: sun, environment, `Terrain`, `CityCamera`, `ScreenshotCapture`, the demos, `SplinesTestbed`,
  `SplineNetwork`, `SplineDrawTool`
