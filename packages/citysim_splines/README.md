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
  `Graph/` (`SplineGraph`, `Junctions`, `Validation`, `Vertical`, `GroundShaping`).
- `icons/`: Tabler icons the overlay draws in place of symbols in tag text (∡ ↔ ↗ ↘ ⤓ ⤒; `SplineOverlay.Icons`). Core
  writes the symbols, so its wording stays engine-free. Also the mouse and keycap icons (`mouse-*`, `key-*`) that
  `KeyGlyphs` draws for an input string (`"Shift+wheel"`, `"RMB"`, `"[ ] · Shift+[ ]"`): the overlay's mouse hints
  use it, and consumers can too (`KeyGlyphs.Draw` on any `CanvasItem`, `KeyGlyphs.Append` into a `RichTextLabel`).
- `src/Godot/`: `SplineProfile` (the `.tres` per network type), `SplineNetwork` (the built graph, undo, issues,
  footprints, visuals), `SplineDrawTool`, `SplineEditTool`, `RibbonRenderer` (draw previews and the placeholder
  flat-ribbon network), `SplineOverlay`, `KeyGlyphs`, `SplineOptionsBar` (the testbed's bar), `SplineIssueList`, `TerrainGround`,
  `TerrainHeightGrid`.

## Heights and ground shaping

After every change `SplineNetwork` gives each node a `Height` and each edge an `EdgeHeights` line (`Vertical.Conform`):
the ground along the centre, averaged over the profile's `GroundSmoothing`, held level over each junction's cut-back,
eased from level to its grade over `JunctionCurve` metres past it (a vertical curve: the allowed grade rises from 0),
limited to `MaxGrade` and rounded at crests and sags. A new or moved node takes the ground exactly where it was put; it
is never moved to make a grade fit. Splitting an edge at a new node (`SplitEdge`) hands its line on to the pieces, and
`TryMerge` joins two fitting lines back, so a node of the consumer's own on a road (a crossing) never bends it. Lines stay until their edge, nodes or
junctions change: **splines never move with the ground**.

Profiles with `Shaping = Section` then shape the ground round what changed (`GroundShaping`): level under the corridor
and one grid cell beside it, then `CutSlope` / `FillSlope` (run per rise) back to the natural ground, and a level disc
round each junction. It's one terrain edit, undone with the spline change. When anything else edits the ground (a terrain
tool, a script), the ground round the splines there is shaped back once the stroke ends, as its own terrain undo step.
`Validation` marks red (Invalid): `grade` where an edge's ends are too far apart in height for `MaxGrade`, and `cut` /
`fill` where its line runs deeper into or higher above the ground than `MaxCut` / `MaxFill` (measured when the line was
made). The tools call `SplineNetwork.Conform(trial)` before validating a trial so previews show these, and the Draw tool's leg
pills show each leg's slope. Ends too far apart in height for `MaxGrade` get one even ramp between them. Not yet: the profile's
`Section` template (canal channels), retaining walls (`Edge`), the cut/fill tag, a Shape ground toggle.

## Hooks

- **`ISplineToolHost`**: what the tools read from your UI: the picked `Profile` (null = Draw idles), `Tool`, `Mode`,
  `EnabledSnaps`, `Anarchy`, `GridBlocks`, `GridFit`, a `ModeChanged` event, and `SetTool` / `SetAnarchy` /
  `SetGridBlocks` for the tools' own keys (Ctrl+A, `[ ]`). The splines testbed's `SplinesTestbed` and the roads
  experiment's `RoadToolHost` are examples.
- **`INetworkVisual`**: your meshes for the built network. Set `SplineNetwork.Visual` and its `SetNetwork(graph,
  footprints, issues, hidden)` is called after every change, undo, redo and Edit trial, in place of the flat ribbons.
  Draw each edge between `Junctions.CutBacks(edge, footprints)`, and fill each `JunctionFootprint` (its `Outline` runs
  round the corridor edge: each arm's sides, its cut end and the curbs). A 2-arm Node-kind corner of one width is a
  footprint too, with `Bend` set (`Junctions.IsBend`: a kerb inside, the outside that kerb pushed out by the road's
  width); other 2-arm hard corners (Join / Turnout kinds) have `Junctions.BendFill`. Junctions too close to fit apart
  (`JunctionClusters.Find(graph, footprints)`: overlapping or squeezed footprints) are best drawn as one area: skip
  their footprints and inner edges and draw each `JunctionCluster` instead. Leave `hidden` edges out. Draw previews stay flat ribbons. The roads experiment's `RoadVisual`
  is an example. Build on the stored heights, not the ground: `edge.Heights.At(s)` (level across, so no roll) and
  `node.Height` for junctions. Either is null only before the network has conformed that graph.
- **Per-end data**: `GraphEdge.DataStart` / `DataEnd` hold the consumer's own data for each end of an edge (the roads
  experiment keeps each arm's crossing setting there). Set with `SplineGraph.SetEndData` inside `SplineNetwork.Apply`, so
  it's undone with the graph. The graph never reads it, but keeps it with its end like the kerb radii (split, merge,
  continue, edit), and never merges away a node where an end carries some, so a consumer can keep a node of its own on a
  straight road.
- Still to come (S10): batched events, costs, `CustomData` policy, save/load.
