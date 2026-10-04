# citysim_splines

The generic spline addon: drawing, editing, snapping, the network graph, junction footprints and validation for any
network type (roads, rails, canals, fences, walls). It knows **profiles**, never network names. Everything that differs
between network types is a `SplineProfile` field or a hook. The spec is `experiments/splines/DESIGN.md`; the testbed and
its milestones are in `experiments/splines/` (read its `ROADMAP.md`).

Needs the terrain package (`citysim_terrain`) in the same project.

## Add it to a project

1. Symlink it in (paths from `experiments/<name>/`): `ln -s ../../../packages/citysim_splines addons/citysim_splines`.
   MSBuild compiles its C# through the symlink.
2. Scene: a `SplineNetwork` (its `Terrain` set), a `SplineDrawTool` and a `SplineEditTool` (`Terrain`, `CityCamera`,
   `Network`, and `HostNode`: your node that implements `ISplineToolHost`).
3. Register a profile per network type with `SplineNetwork.RegisterProfile`.

## Layout

- `src/Core/`: engine-agnostic (`System.Numerics` only). `ProfileRules`, enums, `IGround`, `DrawSession`,
  `GridLayout`, `Geometry/` (`Alignment`, `Curve`, segments, `FreehandFit`, `RibbonGeometry`), `Snapping/`,
  `Graph/` (`SplineGraph`, `Junctions`, `Validation`).
- `src/Godot/`: `SplineProfile` (the `.tres` per network type), `SplineNetwork` (the built graph, undo, issues,
  footprints, visuals), `SplineDrawTool`, `SplineEditTool`, `RibbonRenderer` (draw previews and the placeholder
  flat-ribbon network), `SplineOverlay`, `SplineOptionsBar` (the testbed's bar), `SplineIssueList`, `TerrainGround`.

## Hooks

- **`ISplineToolHost`**: what the tools read from your UI: the picked `Profile` (null = Draw idles), `Tool`, `Mode`,
  `EnabledSnaps`, `Anarchy`, `GridBlocks`, `GridFit`, a `ModeChanged` event, and `SetTool` / `SetAnarchy` /
  `SetGridBlocks` for the tools' own keys (Ctrl+A, `[ ]`). The splines testbed's `SplinesTestbed` and the roads
  experiment's `RoadToolHost` are examples.
- **`INetworkVisual`**: your meshes for the built network. Set `SplineNetwork.Visual` and its `SetNetwork(graph,
  footprints, issues, hidden)` is called after every change, undo, redo and Edit trial, in place of the flat ribbons.
  Draw each edge between `Junctions.CutBacks(edge, footprints)`, and fill each `JunctionFootprint` (its `Outline` runs
  round the corridor edge: each arm's sides, its cut end and the curbs). Two-arm hard corners have
  `Junctions.BendFill`. Leave `hidden` edges out. Draw previews stay flat ribbons. The roads experiment's `RoadVisual`
  is an example.
- Still to come (S10): batched events, costs, `CustomData` policy, save/load.
