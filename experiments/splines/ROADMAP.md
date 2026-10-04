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
$G --headless --path . --quit-after 200 -- --demo-modes      # S6+: Curve, Freehand, Grid, prints "Demo modes: all ok"
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
  - Curb radius was the narrower arm's `DefaultRadius` (16 m streets): too big. Replaced by `KerbRadius` (2026-10-03,
    see S5 → Kerb handles).
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

### ✅ S5: Edit tool (S5a + S5b, play-tested 2026-10-02)
Two passes, with a play-test between them (from the user, 2026-10-02).

**S5a (built 2026-10-02):**
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
- Answered (2026-10-02): no delete on hover. The Draw tool's hover-Delete is removed; deleting is select (Edit tool
  click or box), then Delete.

**S5b (built 2026-10-02):**
- **Radial menu**: RMB on a corner point (or its knob) or any node opens it round the point: Smooth (top), Hard
  (right), Straight (bottom), Delete (left, red). Press-slide-release picks an action, or a plain right-click leaves
  it open for a click; Esc, RMB or a click off it closes it. The hovered action is **tried live** like a drag (the
  result drawn in place, the old shape faint, its issues listed); an Invalid result is refused unless Anarchy
  (`Can't smooth: …`). One undo step each, joined like a draw. Actions that don't apply are greyed:
  - Corner point: **Smooth** = the largest radius that fits (greyed when it's already there); **Hard** = hard corner
    (only if the profile allows it, as when drawing); **Straight** = onto its neighbours' line, the point kept;
    **Delete** = takes the point out, the two legs become one.
  - Node: **Smooth** rounds a joint of two edges (a kink left by deleting a T's third arm, or a profile change):
    same profile → one edge with a corner at the largest radius that fits; another profile → the rounded joint
    split back off, like a draw onto a dead end (`SplineGraph.SmoothNode`). **Straight** moves a joint onto the
    line through its neighbours. **Hard** is always greyed (a joint already is). **Delete** removes every edge at the
    node (`RemoveNode`). Smooth/Straight are greyed at dead ends and junctions of 3+.
- **Selection** now holds nodes too: a click on a node selects it (Shift toggles), shown with an accent ring.
- **Box select**: a drag on empty ground draws a box (screen space) and takes the nodes inside it and the edges
  **wholly** inside it (accent outlines while dragging); Shift adds to the selection.
- **Move the selection**: drag a selected edge's body or a selected node. Every selected node and both ends of every
  selected edge move; an edge with both ends moving moves rigidly, one with one end moving stretches
  (`SplineGraph.MoveGroup`). Held by a node it snaps like a node drag; held by an edge's body it doesn't snap.
  Dragging an unselected edge's body selects it first (Shift adds), so a road can be moved in one gesture. Joined
  like a draw on release; the selection is found again where it moved to.
- Delete removes the selected edges and every edge at a selected node.
- Core: `SetHard`, `RemovePi`, `MoveGroup`, `SmoothNode`, `StraightenedNode`, `JointTurn`, `RemoveNode`.
- Checked: `--demo-edit-splines` (new cases: Smooth/Hard/Straight/Delete on a corner and on a node, mixed-profile
  smooth, group move rigid + stretch + dropped end joins); `--storyboard=edit-radial` (frame 3, Smooth hovered),
  `edit-radial-node` (a kinked joint, Hard greyed), `edit-smoothed` (Smooth chosen: one rounded street),
  `edit-box` (box over an avenue and two streets), `edit-move` (a street and its junction moved: the street rigid,
  the avenue stretching).
- Answered (2026-10-02): Smooth stays "largest that fits"; a box takes only edges wholly inside.

**Follow-up (2026-10-03): kerb handles** (`docs/kerb-handles.html`; v2 below is current; waiting for play-test).
- Junction kerbs were too big (the narrower arm's `DefaultRadius`, 16 m streets / 60 m avenues). New profile fields
  `KerbRadius` / `MinKerbRadius` / `MaxKerbRadius`: street 6 / 2 / 16, avenue 10 / 4 / 30, canal 8 / 4 / 20 (Turnout and
  Join profiles have no kerbs). The footprint uses the narrower arm's `KerbRadius`.
- **Kerb handles**: a selected Node junction shows a handle on each arm's centre line where its kerbs start, with a pale
  track for its range. Drag along the arm: out = bigger, in = tighter (0.5 m steps); tag `kerbs R 6 · 6 → 6–14 · 6–14 m`,
  amber at the end of the track (`most that fits` / `max R 16 m` / `max flare` / `tightest`), red below the minimum
  with Anarchy. Double-click resets one; the junction's radial menu shows **Reset kerbs** in Smooth's slot. RMB on a
  handle opens the junction's menu. One undo step each. No hover actions.
- Model A as agreed: one handle per arm, each kerb's other end stays put, so kerbs go lopsided (a rational-quadratic
  conic tangent to both sides; equal ends = the exact arc). **Changed from the storyboard while building**: a
  lopsided kerb's tightest point gets tighter as it flares (geometry: any curve in that corner with unequal ends is
  tighter than the short end's arc), so a "tightest radius" tag fell from 6 to 2 as the handle went *out*. Kerbs are
  now rated by the arc each end would make (`R 6–14`), the limits apply to both ends, and a far end may be at most
  4 × the near end (`Junctions.MaxFlare`). The storyboard page was updated to match.
- Core: `GraphEdge.KerbStart`/`KerbEnd` (null = profile default; carried through `SplitEdge`, `TryMerge`, `AddSpline`
  via `kerbs:`, `Reconnect`, `RemoveStretches`, `SmoothNode`, the other-profile dead-end split), `SetKerb`,
  `ResetKerbs`; `Junctions.Fit` (the old footprint body) applies the handles, `Junctions.KerbHandles(g, node, anarchy)`
  gives each handle's station, range and limit; `Curb` gained `MaxRadius`, `Control`/`Weight` (conic), `Set`, `Rules`.
  Validation `kerb-min` (Invalid) for a handle-set kerb below the minimum (a kerb squeezed by space is no issue).
- Edit tool: `SplineEditTool.Kerbs.cs`, `HandleKind.Kerb`; overlay `KerbMark` + `KerbGhosts` (old kerbs faint while dragging).
- Checked: `--demo-junctions` (T/X cut-backs now 12/18; new Kerbs case: a handle per arm, range 8–22 m on a street T
  stem (R 2–16), Anarchy to the flare limit 7.5 m, lopsided R 6–14, matched handles round R 10, below min invalid,
  reset, kept through a junction move, a split and a merge); all other demos ok. `--storyboard=kerb-select |
  kerb-drag | kerb-cross | kerb-menu` (kerb-cross prints the stored value through undo/redo).
- Open: tightening a whole junction means moving each arm (each kerb follows both its arms). If that's tedious in
  play, Shift+drag could move every handle of the junction together.
- **Fix (2026-10-03, user's play-test: "when roads are at angle sliding not working")**: the handle stored a station
  and put both kerbs beside its arm there, but an arm between an acute and an obtuse kerb has them starting far apart
  (45 m vs 3 m at 30°), so the obtuse one hit the flare limit at once and the handle was locked; on a 5-way a handle
  could also sit outside its own range and jump. The handle now stores the **end radius** (`KerbStart`/`KerbEnd` are
  radii): each kerb starts at `X + R / tan(half angle)` for its own corner (`Layout.Legs`, `KerbHandle.StationOf` /
  `RadiusAt`), the handle sits at the outermost start, and its range is searched in radius (0.1 m) and snapped to
  0.5 m when dragging. A set kerb is rated with the same legs, so a curved arm can't refuse what the handle allowed.
- **Fix: obtuse kerbs missing** (found while testing): with 6 m kerbs, the kerb in a wide obtuse corner (a street
  leaving an avenue at 135°) touches the avenue's side behind the node, which the curb search (sides from the node
  out) never reached: the corner went sharp. The sides now run back behind the node by the other road's width, and
  the clearance test measures against the road's line there.
- Checked: `--demo-junctions` KerbShapes (T at 30–150°, street × avenue at 45/60/90°, 5-way, 6-way, a crossing on a
  curve: a kerb in every corner, every handle a range of at least 4 m of radius, and both ends of it build with no
  issues and the cut where the handle says); `--storyboard=kerb-angled | kerb-angled-built | kerb-5way` drag handles
  through the Edit tool and print the result (60° arm: R 6 → 13, 20.8 → 32.9 m; 5-way diagonal R 6 → 10).

- **v2 (2026-10-03, storyboard v2 agreed): kerb knobs + road handles.** The user asked for per-kerb control alongside
  the per-arm one. Each kerb now has a radius per end, stored per edge end and side (`KerbEnds(Left, Right)`, looking
  out from the node; Right = the kerb toward the next arm clockwise). Two controls on a selected junction:
  - **Kerb knob** (`Junctions.KerbKnobs`, `KerbKnob`, `HandleKind.KerbKnob`): in each kerb's middle; dragged across the
    corner it makes the kerb round at the radius whose arc's middle is under the cursor (`At` / `RadiusAt` along the
    corner's bisector), range min..max and what fits on both roads. A lopsided kerb goes round again when grabbed.
  - **Road handle** (`Junctions.KerbHandles`, now a factor): scales this road's end of both its kerbs together
    (`KerbSide`s), so a difference set with knobs is kept (agreed); range from min/max, 4 × flare against each kerb's
    other end, and fit, solved directly (no search). Snapped so its outer kerb's radius is a whole 0.5 m.
  - Overlay: `KerbMark.Knob` (dark disc with a white ring, accent when held). Hints `Drag Kerb radius` / `Drag This
    road's kerbs`. Double-click resets one; RMB on either opens the junction's menu (Reset kerbs).
  - Checked: `--demo-junctions` (Kerbs: knob per kerb, R 2..16, in the kerb's middle; a knob makes its kerb round R 10
    and leaves the other at 6; the stem's road handle then sees 10 and 6 and ×1.5 gives 15 and 9; per-side values kept
    through move/split/merge. KerbShapes: every knob and road handle on every shape has ≥ 4 m of radius and builds at
    both ends of its range: 139 knob checks). `--storyboard=kerb-knob` (knob held at R 12) and `kerb-knob-road` (knob
    built at 12, then the stem's road handle held: `kerbs R 12 · 6 → 12–16 · 6–8 m · max R 16 m`). All demos ok.

### ⬜ S6: Curve, Freehand and Grid modes (built 2026-10-03, waiting for the user's play-test)
All three live in `SplineDrawTool` (partial files `.Curve.cs`, `.Freehand.cs`, `.Grid.cs`) and share Draw's
snapping, trials, refusal, flashes and undo. Changing mode ends the chain (`SplinesTestbed.ModeChanged`).
- **Curve** (2): start, bend, end. The bend is the PI (the storyboard's control point), rounded at **the largest
  radius that fits** (`R 178 m · fit` on the pill). With ends either side a corner can use the whole of the
  shorter leg, so the arc reaches the nearer click and the longer leg keeps a straight tail. Shift+wheel / `[` `]`
  bring the bend in (back to "fit" at the top). A fit under `MinRadius` is red and refused unless Anarchy. The third
  click builds the leg, then **the chain goes on** (from the user): two more clicks make the next arc, continuing the
  road with a rounded joint. Ctrl+Z drops a placed bend, else the last curve. `DrawSession` holds the bend per leg.
- **Freehand** (3): hold LMB and drag; samples every 2 m, start and end snapped. `FreehandFit` (Core): smooth the
  stroke (hand wobble out), Ramer–Douglas–Peucker at `max(2 m, Width/2)`, merge a flick at either end, a Kåsa
  least-squares circle per bend for its radius, the PI pushed out by `R·(sec(Δ/2) − 1)` so the arc passes through the
  stroke, and bends with no room for `MinRadius` dropped (tightest first). Tried live like a Draw leg; built on
  release. Overlay: the raw stroke dotted, a dot per bend, `61 samples → 6 points`. RMB cancels.
- **Grid** (4): corner, width (direction and rotation from the first edge, snaps and Ctrl steps apply), depth
  (either side). **Block size per axis** in lots of clear space between the roads, so zoning fills it with no part
  cells (from the user, after the research below): default 8 × 8 lots; `[` `]` / Shift+wheel along, Shift+`[` `]` /
  Ctrl+Shift+wheel across, or the `Block W × D lots` spin boxes in the options bar (shown in Grid). Centre lines are
  lots·`SnapLength` + `Width` apart. `GridLayout` (Core) makes full-length straight row and column lines, exactly
  square; they're added with `Ends.None` in one `Network.Apply`, so crossings make the 4-ways and Ts and the corners
  stay square. From the second click the whole grid is a trial graph shown with `Network.ShowTrial` (the Edit tool
  no longer clears every trial when idle, only its own).
- Checked: `--demo-modes` (curve at the fit and not clamped; chained curves are one road with the second bend at
  half the joint leg; undo of a bend and of a curve; a rail curve under its minimum refused, then red with Anarchy; a
  noisy S stroke → ≤ 6 points within tolerance, no bend under min; a straight stroke → 2 points; a 3 × 2 grid → 17
  edges, 4 corners, 6 Ts, 2 four-ways, nothing red, one undo; a grid across a street joins it at each row; 12 × 8
  lots spacing). Storyboard frames `--storyboard=mode-curve | mode-freehand | mode-grid`. The other demos still pass.
- **Fix after play-test (2026-10-03): circles in Curve mode** (from the user: "curve mode should let people create
  circles"). Chained curves kinked: the next bend went anywhere, and even on the tangent a chained arc got only half
  its leg. Now the bend that continues a road is held on its tangent (`SnapQuery.TangentLock`, tag `tangent`, the ray
  drawn as a guide; Space frees it), a corner next to a **straight-through PI** may use the whole leg (`Alignment`,
  mirrored in `BendFit`), and where the tangent meets the extension out of the chain's own start the bend snaps
  there (`close loop · tangent`), so ending on the start closes a seamless loop. Checked: `--demo-modes` (chained
  bend now R 100 = the whole leg, joint straight through; four quarters → one loop edge, each bend R 100 unclamped,
  length 2πR, the node's two arms opposite, no issues; a Space-held bend still makes a turning joint). The other
  demos pass unchanged. Storyboard frames `--storyboard=mode-curve-close | mode-curve-circle`.
  Then (from the user): **road points**. Each corner's point on the road (`Alignment.RoadPoint`: the PI where the
  road passes through it, a joint or a sharp corner, else its arc's middle, where the knob sits) shows as a white dot
  in every mode, Draw and Edit, as a guide, and snaps as a node (`snap: node`), so a road can connect there (the edge
  splits into a T). And in Edit, a click on a road with corners selects the **stretch** between two road points
  (`Stretch`, `SplineEditTool.Stretch.cs`), not the whole edge: outlined with its two corners' handles; Delete takes it
  out (`SplineGraph.RemoveStretches`, the rest kept, a loop joined back), a drag moves its leg (`MoveGroup` with
  stretches), RMB on the road opens the radial menu for it. A box still takes whole edges. Checked: `--demo-modes` (a
  road started by a circle's joint, and by an arc's middle, makes a 3-arm node there), `--demo-edit-splines` (a
  circle is 8 stretches; a quarter deleted leaves one 3/4 road cut at its joints; a middle stretch moved keeps its
  radii and ends; an end stretch moves its node only). Storyboard `edit-stretch | edit-stretch-deleted |
  edit-stretch-move`.
- **Follow-up (2026-10-03): corner junctions + overlay restyle** (`docs/corner-junctions.html` v2, agreed; waiting for
  play-test). From the user: joining a road at a bend's dot cut the arc in half (a T on a curve, 4.7 m off the street's
  line, two extra dots), and a square T at a bend needed delete-and-redraw.
  - The dot is now a **slider** (DESIGN.md → Junctions → Corner junctions): `SnapQuery.BendSliders` (Draw, Curve,
    Freehand; not Grid or the Edit tool) makes `SnapEngine` hold the cursor on the track from the dot to the PI and
    return a `BendSlide` (radius, point). `Alignment.BendPoint` / `BendRadiusAt` / `BendAxis` / `IsArc` give the track.
  - `SplineGraph.SplitBend` (pinned other corners, the bend at the slide's radius or hard at 0, split at the arc's
    middle) and `ReshapeBend` (no split, for the hover preview). `SplineDrawTool.Bends.cs` applies them to the trial
    and inside the build's `Network.Apply` (one undo step): the start's bend for the first leg, the cursor's for the
    end. The hover preview is `Network.ShowTrial(reshaped, hidden: true)`; `Network.Hide` no longer redraws the built
    graph over a trial. The overlay's dots come from the reshaped graph.
  - `SplineGraph.ShowsRoadPoint`: a road point within a road width of a 3+ arm node at the edge's end is hidden (the
    halves of a cut bend); snap candidates carry `HiddenCorners`. A circle's quarter into a junction keeps its dot.
  - Overlay restyle to match the storyboard pages: no dark outline under lines, guides, arcs, rings or marks (only a
    thin rim on node discs), thinner legs (3 px) and guides (2 px, 80 %), filled pills (accent snap, amber, red) in
    monospace. `ShadowLine` → `ScreenLine`, `Shadow` → `Rim`.
  - Checked: `--demo-modes` (CornerJunctions: corner point → square T with no corner left, one undo restores R 16;
    R 12 → a node on the tightened bend, both halves R 12, no dots of their own; R 6 refused; a leg ending on the
    corner point makes the T). Every other demo still `all ok`. `--storyboard=corner-slide | corner-red | corner-tee |
    corner-y | corner-tee-built` (screenshots at `--cam=598,462,70,89,0`).
- **Fix (2026-10-04): chords where arms run on** (user's play-test: a block corner straightened with the radial menu
  left odd shapes at the two Ts; a T on a circle had the circle's far side drawn straight). Two neighbouring arms with
  no curb between them (straight through, or the outside of a gap over 180°) were joined in the footprint outline by a
  straight line between their cut ends: a chord across a curve, and a chamfer across the outside of a wide gap. The
  outline now runs along both arms' sides in to the node and round its outside (`Junctions.RoundOutside`, shared
  with `BendFill`). `--demo-junctions` (Runs on: T on a circle, a 225° gap), `--storyboard=edit-straight-corner |
  circle-arms`.
- For the play-test: are 8 × 8 lots the right default block? Is a freehand stroke kept close enough (6 points for
  the storyboard's S, against its 5)?

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
- None open. (S6 settled the last two from the storyboard: Curve's bend is the PI, and a grid takes the first
  edge's rotation.)

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
- Grids (S6): the top complaint about CS2's grid tool is no control over block size ("set 4x8 and it just makes the
  grid"). Grids also break into ½ or ¼ cell gaps, and angles drift by fractions of a degree while still reading 90°.
  The Advanced Road Tools mod's grid tool takes a rectangle from 2 points, plus rows and columns. The community rule
  is 4-deep zoning each side, so 8 cells of clear space between roads, with the length free. Hence blocks in whole
  lots of clear space, set per axis, and exact square geometry.
  [Steam: Roads and Grids](https://steamcommunity.com/app/949230/discussions/0/3877095833479730505/) ·
  [Practical Engineering: Efficient Grids](https://steamcommunity.com/sharedfiles/filedetails/?id=3062339423) ·
  [Advanced Road Tools](https://mods.paradoxplaza.com/mods/102147/Windows)

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
- 2026-10-02 (S5b): the radial menu's Smooth is **the largest radius that fits** (on a corner and on a joint), so
  one action gives the gentlest curve and the knob can bring it back in. Box select takes nodes inside and edges
  **wholly** inside, so a box round a junction can move it with its arms stretching. A group move snaps only when
  held by a node. Confirmed by the user, and **no delete on hover**: Delete only acts on a selection.
- 2026-10-03 (S6, from the user): **Curve chains on** (the end starts the next curve, the joint rounded), and its bend
  is the PI at the largest radius that fits. **Grid blocks are set per axis** in lots of clear space between roads
  (default 8 × 8), after the research on CS2's grid complaints. Freehand drops bends that can't take the profile's
  minimum radius rather than building them red.
- 2026-10-03 (S6 play-test, from the user): **Curve mode chains on the tangent**, CS-style: a bend continuing a road
  is held on its tangent (Space frees it), and a bend snaps to close a loop smoothly, so a circle is 3–4 curves.
  Draw mode isn't expected to make circles. Road points (each corner's point on the road) show as dots in every mode
  and snap as nodes; in Edit a click selects the stretch between two of them (delete, move, radial menu), a box
  still whole edges.
- **Fix after play-test (2026-10-03): moved loops and box select** (from the user: a loop moved in Edit came apart at
  its closing corner; box select "not working").
  - `AddSpline` gave a closed spline's two ends a new node each, so re-adding a loop (any Edit move) left two dead ends
    on one spot: a notch, not connected. A closed spline's end now takes its start node.
  - A draw closed back onto its own start (Draw chain) left that joint a square kink, unlike its other corners. It's
    now rounded with the drawn end's radius (kept a kink when hard) and the node moves to the middle of the first leg
    (`SplineGraph.RoundLoopJoint`; Curve-mode circles are already straight through and are left alone). Known limit:
    the preview just before the closing click still draws that corner square.
  - Box select took only edges wholly inside, so a box over part of a road with corners (a Draw chain is one edge)
    took nothing. It now also takes the **stretches** wholly inside on a road that isn't (accent outlines while
    dragging), matching a click.
  - Checked: `--demo-junctions` (loop joint rounded, node mid-leg, moved loop still one edge on one node); all demos
    pass. `--storyboard=loop-closing | loop-built | loop-moved | box-input` (`box-input` drives real mouse events and
    prints what it took; pair it with `--cam=560,500,340,89,0`).
- **Fix after play-test (2026-10-03): a P's loose end dragged onto its own stem** (from the user: "a really ugly
  junction that is broken").
  - Dropped just below the P's top corner, the junction cut the short arm up to that corner back 64 m, right round
    the corner onto the top road, and joined the cut ends straight: a big slab with the corner gone. A curb pushed
    out 22 m from a street side runs into a R 16 corner and folds back on itself, so the only crossing it found was
    beyond the corner. Now (`Junctions.Footprint`) a curb is fitted only up to the end of the arm's first corner ahead
    of the node (`ArmPath.CurbLimit`; a corner the node sits inside doesn't count, so junctions on curves are as
    before), and only where its circle clears both roads; otherwise it shrinks to fit. Of the existing frames only
    `junction-curved` changed: one street curb that touched past its own corner is a little smaller.
  - Dropped beside the stem with snapping off, the end lay on its own road without joining and nothing said so.
    `Validation` now flags a dead end lying on its own road (further along than twice the width) as Invalid
    `overlaps itself`, so the drop springs back (or draws red with Anarchy).
  - Checked: `--demo-junctions` (P: T on its own road, up arm cut short of its corner, 2 curbs; beside the stem:
    overlaps itself); all demos pass. `--storyboard=p-loose | p-joined | p-corner | p-beside`.
- **Fix after play-test (2026-10-03): no snapping onto a road's own body in Edit** (from the user: "when I try to
  connect same continuous road to itself, it doesn't show me those guides and snappings").
  - A node drag left every edge it changes out of the snap sources, so the whole road the dragged end belongs to
    gave no edge snap, guides or square T, and the end landed loose (then the junction or overlap above). Now
    (`SplineEditTool.DragCandidates`) the part of that road that stays put (up to where the corner next to the
    dragged end starts) is a snap source, and a dead end held by its node passes its road's points as the draw so
    far (`DragLeg`), so the drag gets the draw's square foot, angle locks against the previous leg and lengths.
  - `SnapEngine`: the perpendicular foot came only from the road's closest point to the leg's start, which on a road
    wrapping round it is the wrong leg. Every straight now offers its own foot, and the foot is caught like the edge
    (anywhere across the road, within the catch along it), so it beats the plain edge snap when the cursor is on the
    road a few metres off the centre line.
  - Checked: `--demo-snap` (foot on a wrapping road, cursor off the centre line); all demos pass.
    `--storyboard=self-snap | self-snap-joined` (the user's screenshot: `90° to edge` while dragging, a T · 90° on
    release).
