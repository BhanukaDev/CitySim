# Splines Experiment: Roadmap

Working document for the spline addon. **Keep it updated**: tick items off, add findings, and note decisions when a
milestone lands. A new session reads this file, then `DESIGN.md` (the spec) and `README.md`. **`docs/spline-controls.html` (the
storyboard) is the target for how things look and behave**; where this file or DESIGN.md disagrees with it, the
storyboard wins and the docs get fixed.

## Goal

A **generic** spline addon that the road, rail and canal experiments (and fences, walls, and later power lines and
pipes) all build on. It covers drawing, curves, snapping, junctions, editing, validation, and the data display the player sees
while building. This experiment is its testbed. It uses placeholder profiles and flat-ribbon visuals, and has no road
art or traffic. The target feel is Cities: Skylines plus its best mods (Precision Engineering, Move It, Node Controller,
Parallel Road Tool, Network Anarchy). It should be easy to get a nice rounded road, and never feel like drawing software.

## Working order

**Feature first, then performance.** Each milestone gets the behaviour right and checked, with simple data structures
(lists, linear searches, full rebuilds). Spatial indexes, incremental rebuilds, jobs and native code wait for the
performance milestone (S11), unless something is unusably slow on a normal test map.

## Tech decisions (settled)

- Same stack as terrain: Godot 4.7.2 .NET, C# on .NET 9. It runs on `citysim_terrain` (symlinked as `addons/`).
- **Core is engine-agnostic**: `System.Numerics` only, no Godot types, and checkable headless.
- **Alignments are PI polylines with a radius and spiral per corner** (lines/arcs/clothoids derived), not Béziers.
  The reasons are in `DESIGN.md` → Alignment.
- **Profiles hold every network difference.** Addon code never names a network type.
- Once S2 works, the addon moves to `packages/citysim_splines/` and is symlinked into this experiment as
  `addons/citysim_splines`, the same way the terrain package is. Until then it lives in `src/`, with `src/Splines/Core/`
  and `src/Splines/Godot/` already split.
- Namespaces: `CitySim.Splines` (Core), `CitySim.Splines.Godot`.

## Controls

See `DESIGN.md` → Player controls. There are two known clashes with the terrain tools to settle later, not now:
- Alt+wheel is brush strength in terrain, while Alt+click is a hard corner here. They're different gestures, so both can stay.
- Ctrl+mouse rotates the terrain brush, while Ctrl held is angle snap here. Tools are never active at the same time, so this is fine.

If the spline scheme proves better, align the terrain brush keys to it afterwards.

## How to build / verify

```sh
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build && $G --headless --path . --import
$G --headless --path . --quit-after 200 -- --demo-geometry   # S1+: Core self-checks, prints "Demo geometry: all ok"
$G --headless --path . --quit-after 200 -- --demo-draw       # S2+: draws + builds each test profile, prints "Demo draw: all ok"
$G --headless --path . --quit-after 200 -- --demo-snap       # S3+: snap/guide priority self-checks, prints "Demo snap: all ok"
$G --headless --path . --quit-after 200 -- --demo-junctions  # S4+: graph, junctions, validation, prints "Demo junctions: all ok"
$G --headless --path . --quit-after 200 -- --demo-edit-splines  # S5+: Edit tool graph ops, prints "Demo edit-splines: all ok"
$G --path . -- --flat --screenshot=out.png --cam=1000,1000,300,50,30
$G --path . -- --test-pad[=2000]   # levels a sand-painted square (metres) at the map centre; "Splines: Play" uses it
# S3+: rebuild one storyboard frame and screenshot it, to compare with docs/spline-controls.html side by side
$G --path . -- --flat --storyboard=corner --screenshot=out.png --cam=560,500,340,89,0   # --storyboard=list for names
```

Each milestone adds a `--demo-<name>` self-check (headless, prints `all ok` or the failures) and, where visual, a
scripted `--screenshot` scene. See the milestones.

## Milestones

### ✅ S0: Skeleton
- `src/Splines/Core/` (`CitySim.Splines`) and `src/Splines/Godot/` (`CitySim.Splines.Godot`).
- `ProfileRules` (plain record, every `DESIGN.md` field) + enums (`JunctionKind`, `VerticalMode`, `ShapingMode`,
  `EdgeMode`, `[Flags] SnapProviders`, `DrawMode`). `SplineProfile` (`[GlobalClass]` resource) → `ToRules()`.
- Test profiles in `profiles/`: `street`, `avenue`, `highway`, `rail`, `canal`, `fence` (values from the `DESIGN.md`
  table and the storyboard's table; avenue = 24 m, default R 60, min R 40, junctions ≥ 45°).
- `SplineOptionsBar`: profile picker + mode strip (1 Draw · 2 Curve · 3 Freehand · 4 Grid). `src/SplinesTestbed.cs`
  loads every `res://profiles/*.tres` and shows the bar.
- Findings / choices:
  - Resource units are player-facing: `MaxGradePercent` (negative = follows the ground), `GroundSmoothing` negative =
    level (∞ in `ProfileRules`), slopes as run per rise (2 = 1:2).
  - `ParallelPresets` is a string on the resource for now (`"twin track:-4.5,4.5; wide:-8,8"`), parsed in `ToRules`.
  - `ConnectsTo` empty = joins only its own profile.

### ✅ S1: Alignment geometry (Core)
- `src/Splines/Core/Geometry/`: `Pi`, `Alignment` (PIs → curve), `Curve` (stations + queries), `Segment`
  (`LineSegment`, `ArcSegment`), `SplineMath`.
- Queries: `Length`, `Sample(s)` (position, tangent, signed curvature), `SampleEvery(spacing)` (keeps segment ends),
  `ClosestPoint` (station + signed offset), `Offset(d)`, `Intersect` (line/line, line/arc, arc/arc), `MinRadius(s0, s1)`.
- `--demo-geometry`: 46 checks (90° corner at R 20, clamped corners, hard corner, arc offsets in/out and collapsed,
  hard-corner offsets, line/arc and arc/arc hits, closest point). All ok.
- Conventions (keep these everywhere):
  - Plan coordinates are `Vector2(x, z)` in metres; heights come from the vertical profile (S8).
  - **Left** of travel = `(t.z, −t.x)`: north when heading east, seen from above. Offset `+d` = left.
  - Curvature is signed, **+ = turning left**. An `ArcSegment`'s `Sweep` is + for a right turn (atan2 angles grow
    clockwise seen from above, since z points south).
- Clamp rule: tangent length ≤ half of each neighbouring leg, or the whole leg when the neighbour is an end PI.
  Two clamped corners on a short leg meet with no straight between them. A reversal (≈180°) builds as a hard corner.
- Offsets: an arc whose offset radius goes ≤ 0 is dropped; straights that no longer meet (hard corner, dropped arc)
  are trimmed/extended to where their lines cross.
- Not yet: `Spiral` is stored on `Pi` but unused until S7. Validation (clamped → Warn) landed in S4.

### ✅ S2: Draw tool (mode 1) on terrain
- `IGround` (Core interface: `Raycast`, `GetHeight`) + `TerrainGround` (Godot impl over `citysim_terrain`'s
  `Terrain.Raycast`/`GetHeightAtMap`). Kept to those two members — the `HeightsChanged` re-conform hook is S8.
- `DrawSession` (Core): the in-progress PI list, a small undo/redo stack scoped to the current draw only (RMB /
  Ctrl+Z pop the last PI; ROADMAP's "one undo stack for add/remove" is this, not the graph-command undo of S4,
  which doesn't exist yet), and the pending corner radius (Shift+wheel / `[` `]`, clamped to `MinRadius`).
- `SplineDrawTool` (Godot `Node`, mirrors `TerrainToolController`'s input-dispatch shape): click places a PI,
  double-click/Enter finishes, RMB undoes or cancels, Esc cancels. Alt+click asks for a hard corner; if the
  profile disallows it (`AllowHardCorners == false`) a red hint flashes on the cursor tag and a normal corner is
  placed instead, per DESIGN.md.
- `RibbonRenderer` + `RibbonGeometry` (Core slices, Godot `SurfaceTool`→`ArrayMesh`): blue preview ribbon while
  drawing, profile-coloured built ribbon on finish, edges draped on the ground via `IGround.GetHeight`. Full
  rebuild each frame for the preview, once for a built spline — no incremental updates until S11.
- `DrawCursorTag`: length, heading, turn angle at the last corner, pending radius, next to the mouse.
- No graph yet (S4): built splines are a plain in-memory `(Profile, Alignment)` list on `SplineDrawTool`, each
  with one permanent ribbon mesh — not persisted, not connected to any future graph.
- `--demo-draw`: drives `SplineDrawTool`'s API directly (no simulated input) for each of the 6 test profiles,
  checks each one built, prints `Demo draw: all ok`. Composes with `--screenshot=`/`--cam=` as usual.
- Snapping (Ctrl angle-steps, node/edge/guide snaps — S3), Anarchy, and grade%/speed readout (S7/S8) are not
  implemented; `Alignment.Rebuild()`'s existing tangent-length clamp is the only limit enforced.
- Not yet done: the addon hasn't moved to `packages/citysim_splines/` — do that once this has been tried in
  Godot and looks right (see tech decisions).

### ✅ S3: Snapping, guides and draw feedback (redone to the storyboard)
The first pass (2026-09-30) had the snapping logic but not the storyboard's look or rules. Redone the same day to match
`docs/spline-controls.html` frame by frame.
- `Core/Snapping/SnapEngine.cs` (pure, stateless), priority per DESIGN.md → Snapping and guides:
  - node → **perpendicular foot** (from the leg's start, a T at exactly 90.0°) → edge;
  - **direction lock** next: Ctrl's absolute 15°/5° steps, else a soft square/diagonal/straight-on angle against the
    **road the draw started on or the previous leg**, whichever is closer;
  - with a lock, a guide only picks where along it the point lands (`extension · ∡ 90°`), else a length/equal-length
    snap; with no lock, guide crossing → single guide → length. Guides never pull a leg off its angle.
  - Guides: extension, node alignment (square to the node's edge + the leg's reference directions), parallel (any
    number of lots up to 10, tag `parallel · 40 m gap (5 lots)`, caught at half the catch distance), perpendicular.
    Lit guides are trimmed to run from their source to just past the snap.
  - Equal length matches the previous leg or a nearby built leg (PI to PI).
  - `SnapResult` is structured (angle lock, lit guides with their source/foot/bracket data, length steps, matched leg,
    the road tangent under a node/edge snap) so the overlay can draw from it.
- `SplineOverlay` (Godot) replaced `GuideRenderer` + `DrawCursorTag`, styled after CS2's road tool: thick white
  dashed legs and guides, white ribbon outline, ground discs/rings, an angle arc + `∡` pill at every corner, a `↔`
  length pill mid-leg, mouse-hint pills by the cursor (DESIGN.md → Feedback → Overlay visual language).
  `RibbonRenderer`: light-blue ghost preview, **amber halo with a warning**, built ribbons with a centre dash.
- `SplineDrawTool` records the start road's heading when the first click snaps to a node/edge; Shift+wheel / `[` `]`
  now resize the **live** corner (`DrawSession.SetPendingRadius`), as in storyboard step 2; `Total … m` flashes after a
  finish. `Alignment.Corner(i)` / `AnyClamped` give the corner geometry for drawing.
- Options bar: readable toggle names; toggles the profile doesn't offer are greyed out. Profiles gained
  `SnapUnitName` ("lot", fence "post").
- `--demo-snap`: all the old cases plus angle-then-guide (exact 90.0°), square to edge, straight on, road vs leg
  reference, perpendicular foot, 5 lots, equal length to a built leg. All ok.
- `--storyboard=<frame>` (`src/Demos/StoryboardDemo.cs`): click-start, corner, hard-corner, finish, extension,
  angle-ctrl, length-node, node-align, parallel, parallel-arc, perpendicular, equal-length, crossing, rail-clamped.
  Each screenshot was checked against its HTML frame.
- Not in S3 (the storyboard frames show them, but they need the graph or later math): the real T-junction and split
  when a draw starts/ends on a road (S4; for now it only snaps there), the rail speed readout and spirals (S7).
- Still to do: play-test in the Godot editor (does it feel sticky, not fighty, at normal zooms?).

### ✅ S4: Graph and junctions
- `Core/Graph/SplineGraph`: nodes and edges (each edge owns one alignment, its `ProfileRules` and a `CustomData`
  slot). `AddSpline` joins an end to a node or splits the edge it lands on, and splits both at every crossing, where the
  two profiles `Connects` (each accepts the other: own id or `ConnectsTo`; `JunctionKind.None` never joins).
  `RemoveEdge` merges the two edges left at a node when they have the same profile and run straight through (±1°).
  `Clone()` is cheap (alignments are shared, never changed in place): the draw preview and the undo history use it.
- `Core/Geometry/AlignmentOps`: `SplitAt` (on a straight: an end PI; inside an arc: two PIs where the tangent at the cut
  meets the legs, so the halves are the same arc), `Reversed`, `Join` (folds a split arc back into one PI, so split →
  merge round-trips). Corners next to a cut are pinned to their built radius so a clamped one doesn't spring back.
  `Alignment.CornerStations(i)` and `Curve.SelfIntersect()` were added for this.
- `Core/Graph/Junctions`: kind at a node (Turnout if any arm is, then Node, then Join), arm gaps, labels
  (`T-junction · 90°`, `4-way · 90°`, `turnout · R 300 m`, `join`), the **footprint** of a Node junction with 3+ arms
  (curb arcs between neighbouring arms at the narrower arm's `DefaultRadius`, arm cut-backs, an outline polygon),
  turnout violations, and `TurnoutGhost` (the nearest legal turnout: leaves along the line at `MinRadius`, the arc
  starting at the switch; a square target is moved ahead until it fits).
- `Core/Graph/Validation`: `Issue{Severity, Code, Message, Where, EdgeId, NodeId}`. A corner built below `MinRadius`
  → Invalid (`R 191 m, min 300 m · Ctrl+A allows`), whether asked for or clamped; a clamp above it is no issue, junction angle below the minimum → Warn (`22°, min 30° ·
  Ctrl+A allows`), square branch at a turnout → Invalid (`90° not allowed`), crossing an edge it doesn't connect to →
  Invalid (`crosses street · not connected`), corridor overlap (incl. drawn back over a road from a shared node) →
  Invalid, crosses itself → Invalid, too short for its junctions → Warn.
- Godot: `SplineNetwork` (scene node) owns the graph, a snapshot undo/redo stack (one step per action), issues,
  footprints and the built visuals. `RibbonRenderer.SetNetwork` draws edges cut back at junctions, footprints filled
  flat, and amber/red halos under edges and nodes with issues.
- `SplineDrawTool`: every frame the draw is tried on a clone of the graph; the preview shows the junctions it makes
  (dashed ring + tag), its issues (tags, amber arc in a too-sharp junction, preview halo and outline amber/red) and an
  issue list (bottom left). **Invalid refuses the finish** (red `Can't build: …` flash, the draw stays open) unless
  **Anarchy** (Ctrl+A or the options-bar toggle), which also lets Shift+wheel go below `MinRadius`. A square branch off
  a turnout profile shows the legal turnout as a ghost (`turnout 1:9 · R 300 m`); LMB takes it. With no draw in
  progress, Ctrl+Z / Ctrl+Y undo/redo on the graph and **Del** deletes the edge under the cursor (a stopgap until the
  S5 Edit tool). Snapping now reads the graph; extension guides only leave dead ends.
- Test profiles: street ↔ avenue connect (`ConnectsTo`).
- `--demo-junctions` (Core): T (split, label, cut-backs 22 m, curb centre), 4-way street × avenue, 22° amber, rail
  square branch refused + legal turnout ghost, crossing through an existing junction, canal refused by ConnectsTo,
  overlap vs a 0 m gap, clamped radius, split → merge on a straight and on an arc, a corner node kept. All ok.
  `--demo-draw` adds the tool flows: refused → built with Anarchy (stays red), undo/redo, T then delete → merged.
- `--storyboard=junction-cross | junction-sharp | junction-turnout | junction-canal`, each checked against its frame.
- Fixed on the way (pre-existing, headless only): the overlay's tag clamp threw in a tiny viewport, and degenerate
  ground discs logged failed triangulations every frame.
- Known limits (fine for now, revisit if they bite):
  - Splitting inside an arc can clamp the corner before/after it tighter if that corner already used half its leg
    (the half-leg clamp rule from S1).
  - Overlap between edges that share a node is only caught when they leave it along the same line; a turnout's
    branch is exempt.
  - A self-crossing is a junction now (draw-chain follow-up); only a profile that doesn't join itself shows it red.
  - The turnout ghost is offered for the first leg only; snapping still offers guides from profiles that can't connect.
  - `ConnectsTo` takes profile ids only (no tags yet).
  - Curb radius is the narrower arm's `DefaultRadius` (per the storyboard), so street curbs are 16 m. A separate
    `CurbRadius` profile field is easy if that looks too big.
- Still to do: play-test in Godot (T, 4-way, the rail turnout ghost, Anarchy, Del → merge, undo).
- **Follow-up (2026-09-30): continuing a dead end** (`docs/dead-end-joins.html`, option C). Snapping onto a node with
  one arm used to leave two butted ribbons (a notch outside, overlap inside), since 2-arm nodes had no shape or tag.
  - `SplineGraph.DeadEndAt` + `AddSpline` take a same-profile dead-end edge into the drawn spline (start and/or end);
    the node becomes a PI with the drawn end's radius / hard flag (a scripted end with radius 0 gets `DefaultRadius`),
    the old corners are pinned (`AlignmentOps.Pinned`). `AddResult` gained `Alignment` (the whole added road) and
    `Continued` (the edges taken). A draw back onto the same edge's other end closes a loop on one node.
  - `Junctions.BendFill`: the outside of a 2-arm bend that stays a node (two profiles, or left by a delete), drawn by
    `RibbonRenderer`.
  - Draw tool: `continue · <profile>` snap tag; the ghost is the whole road it becomes and the old edge is hidden
    (`SplineNetwork.Hide`); the overlay gets the old road's leg (half of it when it ends in a corner, to clamp the
    same) so the joint has its `∡` and radius pills; `DrawSession.StartIsCorner` makes the start the live corner for
    Shift+wheel. Straight-on corners get no pills. A click on a dead end after the first point places it and finishes
    (`LMB Place and finish`); a refused finish leaves the draw open with the point placed.
  - `--demo-junctions` (Continue: L, below MinRadius, clamp, old corner kept, hard joint, both ends, loop, T stem,
    other profile + bend fill), `--demo-draw` (continue with Shift+wheel, undo), `--storyboard=continue |
    continue-built | continue-both | continue-mix`. `length-node`'s mid-road node is now a T (two straight
    same-profile streets end to end are one road).
  - Open: in the user's screenshot, a road start sat near another road's end without joining. Check whether the node
    snap missed there.
- **Follow-up (2026-09-30): each click builds its leg** (from the user). The Draw mode's preview is only the leg from
  the last point to the cursor (Curve mode will preview two points). `DrawSession` is now a chain of placed points
  (`Placed`, `LegTo`, point undo/redo); a click builds `[last, click]` through `SplineGraph.AddSpline` as one undo
  step, and since the last point is a dead end of the road just built, the next leg *continues* it (the dead-end
  follow-up above), so its corner rounds live with every corner rule. Refused legs place nothing. RMB/Esc stop (built
  legs stay), double-click/Enter end the chain, Ctrl+Z mid-draw takes back a leg. A click on a dead end finishes.
  Junction tags aren't repeated for a junction the chain passes back through; `Total` is the chain's drawn length.
  The storyboard frames now show the built legs solid and only the last leg as a ghost; the HTML storyboard still
  shows the whole draw as a ghost (update it when next touched). `--demo-draw` covers build-per-click, continue,
  Ctrl+Z mid-draw and finish.
  - Fix after play-test (`docs/draw-chain.html`): the whole continued road was drawn as the ghost, and a loop across
    the chain's own road was refused as "crosses itself". Now `AddResult.SolidUntil`/`SolidFrom` mark the unchanged old
    road (up to where the joint's corner starts); `RibbonRenderer.SetPreview` draws it solid in the profile colour and
    only the rest as the ghost, and the overlay outlines and dashes only the new part. `AddSpline` turns every
    self-crossing (and a leg ending on its own road) into a junction for a profile that joins itself; a loop edge's
    two ends meeting at its node no longer count as a self-crossing. Checked in `--demo-junctions` (loop over own
    road → 4-way, ends on own road → T, one spline across itself → 4-way, solid station), `--demo-draw` (loop by
    clicks), `--storyboard=chain | chain-loop`.
  - Fix after play-test (`docs/joint-angle-arcs.html`): angle arcs were missing at three joints. A leg starting or
    ending on a dead end it doesn't continue (another profile, say a street off an avenue) now shows the angle between
    the two roads (`OverlayFrame.StartArm`/`EndArm`); a leg drawn from open ground onto a road's side shows the T's
    arc at its end, on the smaller-angle side like a branch's start (`EndHeading`). Road-reference arcs get short white
    arms along both directions. A straight joint (180°) shows one rectangle standing on the line and
    `∡ 180° · straight` instead of nothing. `--storyboard=joint-angle | joint-straight | t-into | continue-straight`.

- **Fix (2026-09-30): junctions on curves** (from the user: "road curves don't render right in junctions"). The
  footprint treated each arm as a straight line along its direction at the node, so on a curve the curbs, cut ends and
  outline missed the real ribbons (notches, wedges, plates sticking out). `Junctions.Footprint` now follows each arm's
  curve: the curb centre is where the two facing sides, pushed out by the curb radius, cross (1 m polylines, nearest
  crossing to the node), a cut-back is a **station along the arm**, and the outline runs along the curved sides.
  The outline isn't star-shaped any more, so `RibbonRenderer` triangulates it (`Geometry2D.TriangulatePolygon`, fan
  as a fallback). A curb that doesn't fit within the cut-back cap is left out and adds no cut-back (before, both arms
  were cut to the cap). Straight junctions are unchanged. `--demo-junctions` (Curve: curbs touch the real sides, the
  outline has each cut corner), `--storyboard=junction-curved`.
- **Fix (2026-09-30): short arms and sharp angles** (user's avenue screenshot). A curb that didn't fit an arm's cap was
  dropped with no cut-back, so stubs butted in square and sharp pairs overlapped. Now the curb shrinks to the largest
  radius that fits (down to a sharp corner), and an arm whose far end has no footprint may be cut back 90 % of its edge
  (45 % only when both ends are junctions). Also: a corner that exactly fills its leg (a split inside an arc) no longer
  counts as clamped from float error (`R 40 m (wants 40)` and its amber halo). `--storyboard=junction-stubs`.
- **Fix (2026-10-02): squeezed junctions and width transitions** (user's screenshot: a street end lying across an
  avenue in a cluster of close junctions, and an avenue running on into a street with a hard step).
  - A pair of arms too sharp and short for even a sharp corner within the caps (a 30° street, 30 m long, off an
    avenue) found no curb crossing and was skipped, so the arm stayed **uncut** and its end ran across the other road.
    Now the sides are followed on past a short arm's end and back through the node to where they really cross: a
    squeezed pair is cut back there (clamped to the caps, cut ends joined straight), and on the wide side the outline
    runs on past the node to where the street's side leaves the avenue (a sharp corner, no wedge). Near-parallel arms
    are cut as far as they can go; a wide gap with no crossing at all still needs nothing.
  - Footprints are drawn just above the ribbons (`FootprintLift`), so an arm the caps can't fully clear is covered
    instead of z-fighting.
  - `Junctions.IsTransition`: two arms of different widths at a node (avenue → street) get a footprint that tapers
    the wider arm down to the narrower one over 2.5 × the width difference (30 m for 24 → 12, within its cap), eased
    at both ends, in the wider arm's colour. A bend at such a node gets its `BendFill` at the narrower width.
  - `--demo-junctions` (Squeezed, Transition), `--storyboard=junction-cluster` (rebuild of the screenshot) |
    `junction-squeeze` | `transition`.
  - Still open: the cluster's 36° street pairs are refused as `overlaps avenue`. That's correct for the rules, but
    the sliver islands they leave are just what that geometry gives.
  - Fix after play-test (from the user: "this is not a junction, continuous road"; the centre line stopped at the
    taper and a bent avenue → street joint was a hard kink with a notch). `DeadEndAt` now takes a dead end of any
    profile the drawn one `Connects` to, so drawing a street on from an avenue's end continues it like one road: the
    joint becomes a corner with every corner rule (drawn radius, Shift+wheel, clamps), then the avenue is split back
    off where that corner starts (at most half of it goes to the corner) and kept as its own edge (`AddResult.Kept`).
    The street carries the whole curve, the two meet straight on, and the taper sits on the avenue's straight. The
    preview draws the kept avenue solid in its colour; the snap tag reads `continue · avenue → street`. A transition
    footprint is `Continuous`: `RibbonRenderer` runs the centre line through it to the node, and every centre line is
    now spaced evenly with half a gap at each end, so dashes read as one line across the joint. `--demo-junctions`
    (continue other profile, at the start and at the end), `--storyboard=continue-mix | continue-mix-draw | transition`.
- **Fix (2026-09-30): clamped corners under `MinRadius`** (user's screenshot). A clamped corner was only a Warn even
  when the radius it got was under the minimum, so it built without Anarchy; continuing that road later pinned the
  corner to its built radius, so the same corner then came up as a *new* Invalid and blocked an unrelated draw. Now a
  corner below `MinRadius` is Invalid however it got there, and a clamp above it is no issue (no amber, no `wants`).
  Shift+wheel / `[` `]` stop at the largest radius the live corner fits (`Alignment.MaxRadius`), Anarchy or not.
  `--demo-draw` (wheel capped at the fit, squeezed corner refused then built with Anarchy), `--demo-junctions`,
  `--storyboard=rail-clamped` (now red: R 191 m, min 300 m).

### ⬜ S5: Edit tool
Two passes, with a play-test between them (from the user, 2026-10-02).

**S5a (built 2026-10-02, waiting for the user's play-test):**
- `M` switches Draw ↔ Edit (`SplinesTestbed.Tool`, an `M Edit` button beside the mode strip); `1`–`4` pick a draw
  mode and switch back to Draw. Leaving Draw ends a chain in progress.
- `SplineEditTool`: a click selects **one edge** (between two nodes), Shift+click adds or removes one, a click on
  empty ground or Esc clears it (Esc again goes back to Draw). Junctions split roads into edges, so deleting part of
  a road is select + Delete.
- Drag a selected edge's corner point, or any node: connected edges follow and keep their radii. The point snaps
  (node, edge, guides; Space off), Alt slides a corner point onto its neighbours' line (straighten). Drag a radius
  knob (the ring in the middle of each arc): the same limits as drawing (`MinRadius` unless Anarchy, up to what fits).
- Each frame of a drag is tried on a copy of the graph and drawn in place of the built one
  (`SplineNetwork.ShowTrial`), the old shape a faint outline, an accent arrow from where the point was, and a tag:
  the change in length (`−35 m`) or `R 50 → 140 m` (amber at the most that fits, red under the minimum).
- On release it's **joined like a draw** (`SplineGraph.Reconnect`, from the user): the edited edges are taken out
  and added again with their nodes kept, so a crossing with a connecting profile becomes a junction, an end dropped
  on a node or road joins it, and a dead end dropped on a connecting dead end continues it (same profile merges,
  another profile splits back). Re-adding never continues anything else (`AddSpline(continueAt: Ends.None)`),
  otherwise a dragged junction's arms would merge into each other. An Invalid result is refused unless Anarchy: it
  springs back with `Can't move: …`. New junctions flash their tags. One undo step; the selection follows the
  edited edges to their new ids. Delete removes the selection.
- Core: `SplineGraph.Edit.cs` (`MoveNode`, `MovePi`, `SetRadius`, `Reconnect`, `KnobRadius`, `OntoLine`). The
  shared mouse/ground/projection code moved from the Draw tool to `SplineToolView`.
- Checked: `--demo-edit-splines`; `--storyboard=edit-drag | edit-knob` (storyboard frames 1 and 2),
  `edit-join` (a dead end dragged across an avenue: a 4-way) and `edit-refused` (across a canal: springs back).
- For the play-test: the Draw tool still deletes the edge under the cursor with Delete. Keep it, or leave Delete to
  Edit?

**S5b (next):**
- RMB on a PI / node: the radial menu, Smooth · Hard · Straighten · Delete (actions that don't apply greyed out).
  `--storyboard=edit-radial` (frame 3).
- Shift+drag box select, and dragging the selection to move it (edges with both ends selected move rigidly, the
  rest stretch).

### ⬜ S6: Curve, Freehand and Grid modes
- Curve: 3 clicks → one PI with the largest fitting radius.
- Freehand: drag → RDP simplify → PIs with fitted radii.
- Grid: 3 clicks → a block of edges and junctions, sized to `SnapLength`.

### ⬜ S7: Transition spirals and speed
- Clothoid in/out at each arc (profile `SpiralLength`), clamped with the arc.
- Speed readout from the tightest radius (`SpeedFromRadius`) in the cursor tag and the amber clamp tag
  (`R 191 m` · `≈ 97 km/h`, the storyboard's rail frame), and a curvature strip in the HUD for the selected edge.

### ⬜ S8: Vertical profile and terrain shaping
- Stations (`Ground` / `Absolute` / `Offset`), PgUp/PgDn steps, grade in the tag, `MaxGrade` check.
- `Ground` height line: ground sampled along the centre, smoothed, limited to `MaxGrade`.
- Terrain shaping (`DESIGN.md` → Terrain shaping):
  - profile `Section` template with `CutSlope` / `FillSlope`, `GroundSmoothing`, `Edge` (Slope / Wall / Auto),
    `MaxCutFill`, and `Shaping` on/off plus a **Shape ground** toggle
  - applied once on build and on Edit drag release, never per frame, with **no preview**
  - the same undo step as the spline
  - junction footprints flattened as plates
  - ground stays shaped on delete
  - a cut/fill tag in the HUD
- Core math on `IHeightEdit`. The Godot side passes `terrain.BeginEdit().Heights`.
- Re-conform `Ground` stations on terrain `HeightsChanged` (from other edits; shaping by the spline itself doesn't
  loop).
- `CorridorOf(edge)` for consumers.
- `--demo-shape`: a road over a hill (cut) and a dip (fill) keeps its section flat and meets the ground at the set
  slope; a canal cut is 3 m deep; one undo restores both the spline and the ground; delete leaves the ground; a
  fence doesn't touch it. Screenshot the road and the canal.

### ⬜ S9: Parallel
- `P`: 1–N offset copies, each with its own profile. Wheel changes the offset, Alt flips the side. Profile
  `ParallelPresets`. Offsets use the exact arc offset from S1.

### ⬜ S10: Consumer API, events, save/load
- Batched events, `ISplineVisual`, `ISplineCost`, `ISplineDataPolicy`, `CustomData`, and a versioned graph file.
- A package `README.md` that is the API (like `citysim_terrain`). Then start `experiments/roads/`.

### ⬜ S11: Performance and scale
Only after the features. Things to expect:
- a spatial hash for snapping, picking and intersection
- incremental visual rebuilds per dirty edge
- sampling LOD by zoom
- background validation
- guide search limited to a spatial query instead of every nearby edge
- terrain shaping on long corridors (only the dirty part, off the main thread)
- a benchmark flag (`--demo-scale`: 10k edges, snap/pick/split timings)

### Later (not scheduled)
- Roundabout and prefab junction placement (Roundabout Builder / Smart Intersection Builder)
- Bridges and tunnels from the vertical gap
- Replace/upgrade tool
- Copy/paste and saved layouts (Move It export)
- Node Controller style per-node shape overrides

## Open questions
- Should Curve mode's bend point be the PI (current plan) or a point the arc passes through?
- Should the grid mode rotate to the terrain or to the first edge only?

## Research notes
Sources and what players like or miss are in the storyboard's last section (`docs/spline-controls.html`). The short
version:
- CS2 near-miss snaps (179.6°) and snapping that fights curves are the main complaints.
- Precision Engineering readouts, Move It editing without bulldozing, and parallel networks are what players rely on.
- Transport Fever 2's live speed-from-radius readout is the model for rails.
- Guides: CS2 guideline snapping breaks zoning grids when left on, so players disable it. Ours are low priority,
  exact, only shown when aligned, and toggled per type.
- Ground under roads:
  - CS1 roads build "masses of ground" instead of climbing hills (Network Anarchy's Ground mode is the workaround).
  - CS2 reshapes too much and breaks zoning. Its retaining walls are buggy at the ends.
  - Transport Fever 2 averages bumps into a straight line, with embankments, bridges and tunnels, and has a gentle
    and a steep fallback slope.
  - Hence `GroundSmoothing` per profile (streets hug, rails smooth), `Edge` = Slope / Wall / Auto, and `MaxCutFill`
    warnings.

## Decision log
- 2026-09-29: One Draw tool, polyline with auto-rounded corners, is the default mode. Curve, Freehand and Grid are
  extra modes. Alignments are PI + radius + spiral, not Béziers. Profiles carry all network rules. The addon is
  generic, and roads/rails/canals are separate consumer experiments. Features first, performance in S11.
- 2026-09-29: Guides are extension, node alignment, parallel (lot steps, follows arcs), perpendicular, equal
  length, and guide crossings. They are shown only when aligned, at most two.
- 2026-09-29: Terrain shaping is built into the addon, driven by a per-profile section template with cut/fill
  slopes. It is applied on build and on edit release, with **no preview**, because a per-frame terrain edit is
  too costly. It is one undo step with the spline, and the ground stays shaped when a spline is deleted.
- 2026-09-29: S2's undo is scoped to the current in-progress draw only (add/remove a PI); undoing a *finished*
  spline needs the graph-command stack from S4 and isn't implemented yet. `IGround` stays at two members
  (`Raycast`, `GetHeight`) until S8 needs `HeightsChanged`. "Built" splines in S2 are a plain rendering list, not
  graph data — replaced wholesale once S4 lands.
- 2026-09-30: `SnapEngine` treats an alignment's two ends as its "nodes" pre-S4; there's no separate junction
  concept yet.
- 2026-09-30 (S3 redo, from the user): the storyboard is the target. **Angles rank above guides**: a lock fixes the
  direction and a guide only picks the point along it. The soft angle is measured against **both** the start road and
  the previous leg (closer wins), replacing the earlier "previous leg only". Angle tags show the angle between the
  roads with a drawn **∡** and what it means (`∡ 90° · square to edge`). Feedback is drawn in screen space, in the
  storyboard's colours. Amber on a clamped preview corner moved into S3; junction splitting stays in S4. Lot wording
  is profile data (`SnapUnitName`), so the addon stays generic.
- 2026-09-30: parallel guides reach 10 lots and are caught at half the catch distance, since with a guide every 8 m a
  full catch lit one almost everywhere near a road when zoomed out. A "leg must run alongside" rule was tried and
  dropped: the storyboard's arc frame meets the guide at an angle.
- 2026-09-30: the in-game overlay follows **CS2's road tool look** (user's reference screenshot): thick white dashes,
  arcs drawn between the lines with angle pills beside them, length pills at the middle of each leg, dark rounded
  pills, mouse hints by the cursor. The storyboard still sets what is shown; its boxed mono tags were only a sketch.
- 2026-09-30 (S4): **Severity doesn't depend on Anarchy.** Warn builds; Invalid is refused unless Anarchy is on, and
  what Anarchy builds stays red (the storyboard's "shows red", Network Anarchy's behaviour). A too-sharp junction
  angle is Warn, so it always builds. DESIGN.md's "Anarchy downgrades Invalid to Warn" is reworded to match.
- 2026-09-30 (from the user): **no "wants" radius.** A radius never asks for more than fits (Shift+wheel stops at
  the fit), a corner clamped below `MinRadius` is refused like one drawn that tight, and a clamp above it is fine.
  This replaces the storyboard's amber `R 191 m (wants 500)` rail frame, which is now red.
- 2026-09-30 (S4): `ConnectsTo` is mutual (both profiles must accept each other); crossings that don't connect are
  left unsplit and reported Invalid (bridges/tunnels later). A node's kind is the strictest of its arms (Turnout >
  Node > Join); footprints only for Node junctions with 3+ arms. Merging happens on delete only, never on add.
- 2026-09-30 (S4 follow-up, from the user): **a same-profile dead end is continued, not joined**. The node becomes
  a corner of one edge and every drawing rule applies to it, with no special case for sharp angles (a 25° joint rounds
  and clamps like a drawn 25° corner). This is the one case where edges merge on add. Different profiles keep the node
  with a bend fill; width tapers come later (superseded 2026-10-02, at the end).
- 2026-09-30 (from the user): **Draw mode builds each leg on click**; double-click only ends the chain. One point is
  ever in the preview in Draw mode (two in Curve mode later). RMB stops instead of removing a point; Ctrl+Z takes
  back legs.
- 2026-09-30 (S4): undo is whole-graph snapshots in `SplineNetwork` (simple, and cheap because alignments are
  shared); S8's terrain shaping will join the same step. Revisit in S11 if memory matters.
- 2026-10-02 (S5, from the user): **Edit selects one edge** (between two nodes; Shift adds), and **an edit joins
  like a draw** on release (crossings become junctions, dropped ends join). S5 comes in two passes (S5a, S5b).
- 2026-10-02 (from the user): **a road running on into another profile is one continuous road, not a junction.** A
  connecting profile's dead end is continued (rounded joint, every corner rule), then split back off where the corner
  starts so each part keeps its profile; the wider one tapers on its straight and the centre line runs through.
