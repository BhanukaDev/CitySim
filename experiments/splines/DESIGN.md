# Splines: Design

The spec for the shared spline addon: what the player does, how it's stored, and what a network type can change.
`ROADMAP.md` has the build order and status. The storyboard (every control below drawn as a still) is
`docs/spline-controls.html`, also published at https://claude.ai/code/artifact/84122d18-d480-4230-b047-de480dfda51e.

## Scope: a generic addon

The addon draws, stores, edits and validates **splines on the terrain**, and shapes the ground under them. It knows nothing about roads, rails or canals.
Everything specific to a network comes from a **profile** (rules as data) and from **hooks** the consuming project
provides (meshes, lanes, costs, terrain shaping).

```
packages/citysim_splines/        the addon (planned; built from this experiment, like citysim_terrain)
  src/Core/                      engine-agnostic: graph, alignment geometry, fillet, snapping, validation, undo. No Godot types.
  src/Godot/                     tools, input, preview rendering, HUD, profile resources, terrain adapter
experiments/splines/             the testbed: every tool and rule, with placeholder profiles and flat-ribbon visuals
experiments/roads/ rails/ canals/   consumers: real profiles, meshes, junction art, simulation data
```

Rules for staying generic:
- No network names in the addon code. "Street", "Rail" and the rest exist only as profile files in the consumers,
  plus test copies in this experiment.
- Everything that differs between networks is a profile field or a hook. If a consumer needs an `if (isRail)`,
  add a profile field instead.
- Core stays free of Godot types (`System.Numerics` only), the same as `HeightMap`, so it ports to the main game and
  can run in headless checks.
- Each segment carries a `CustomData` slot for the consumer (lanes, speed, zoning, pollution), which the addon stores,
  saves and copies on split/merge but never reads.

## Data model

### Graph
- **Node**: a junction or an end. It holds a position and the list of edges meeting there.
- **Edge**: runs between two nodes and owns one **alignment** and one **vertical profile**. It references a profile
  by id. Its `CustomData` belongs to the consumer.
- Crossing or ending on an edge splits it and adds a node. Deleting an edge whose node is left with exactly two
  straight-through edges (within 1°) of the same profile merges them back into one. A split inside an arc becomes two
  PIs where the tangent at the cut meets the legs, so a later merge folds it back into the original corner.

### Alignment: points of intersection (PIs), not Béziers
An edge is stored the way civil engineers lay out a road: a polyline of **PIs** (the corner points the player clicked).
Each interior PI has:
- `Radius`: the arc that rounds that corner (0 = hard corner).
- `Spiral`: transition length in and out (a clothoid; 0 = plain arc).
- `Hard`: the player asked for a sharp corner (Alt).

Geometry is derived: straights, then spiral + arc + spiral at each PI. We don't use Béziers because:
- The radius is exact and constant. It's the number the rules, the HUD and rail speed need.
- Offsets of lines and arcs are still lines and arcs, so parallel networks come out exact.
- Editing stays simple: move a point or change a radius. There are no tangent handles to keep in sync.
- All four draw modes produce PIs (see below), so there's one representation.

If a radius doesn't fit between its neighbours, the corner is **clamped** to the largest radius that fits
(the tangent length is at most half of each neighbouring leg, or the whole leg at an end). The clamped value is
stored as `EffectiveRadius`; validation flags it only when that is below `MinRadius`, and Shift+wheel never
grows a radius past what fits.

Queries on an alignment: length, position / tangent / curvature at a distance, closest point, sampling at a spacing,
offset curve, intersection with another alignment, and minimum radius over a range.

### Vertical profile
This is a list of `(distance, height, mode)` stations along the edge. `mode` is `Ground` (follow the terrain),
`Absolute`, or `Offset` (relative to the ground). Grade between stations is derived. PgUp/PgDn while drawing adds or
changes the station at the current point.

`Ground` doesn't mean "copy every bump". The spline's height line is the ground sampled along the centre, then
**averaged over `GroundSmoothing` and limited to `MaxGrade`**. A street (20 m) hugs the hills. A rail line (400 m)
runs straight through them on cuts and embankments, like Transport Fever 2. This answers the top CS1 complaint that
roads build "masses of ground" instead of climbing hills. The terrain is then shaped to meet that line (below). The spline height is
the top of the section: the road surface, or a canal's bank top.

## Terrain shaping

The addon shapes the ground under and around a spline. It is generic: the profile's **section template** decides the
shape, so roads, rail embankments, canals and building pads all use the same code.

### Section template (profile)
- `Section`: points across the spline, `(offset from centre, height relative to the spline)`, mirrored on both sides.
  - Road: `(0, 0) (6, 0) (10, -0.2)`, a 12 m flat top plus a 4 m shoulder falling slightly.
  - Rail: `(0, 0) (3, 0) (4.5, -0.6)`, a ballast bed.
  - Canal: `(0, -3) (3, -3) (6, 0) (7, 0)`, a 6 m bed 3 m deep, sloped banks, and a towpath lip.
  - Fence / wall: no section (`Shaping = None`), so it just drapes on the ground.
- `CutSlope`, `FillSlope`: the angle at which the section's outer edge blends back into the natural ground, cutting
  down into a hill or building an embankment out over a dip. Roads use 1:2 (≈ 27°) for both.
- `Edge`: `Slope` blends out at the cut/fill slope. `Wall` ends the section in retaining walls, with no slope,
  for tight or steep spots. `Auto` uses walls only where the cut or fill is higher than `WallAbove`. Walls are
  consumer visuals (`ISplineVisual`), and the addon only reports where they are and how tall.
- `Shaping`: `Section` or `None`. The options bar has a **Shape ground** toggle, defaulting to the profile's
  setting, so the player can lay a road on the ground as is (Network Anarchy's "no terrain change").

### How it's applied
- **When:** once, when a spline is **built** (finish) and when an Edit **drag is released**. There's no live
  preview, so drawing never touches the heightmap. This is deliberate: a per-frame terrain edit re-uploads large
  Terrain3D regions and wakes the water. The cursor tag may show the largest cut/fill along the centre
  (`cut 4 m · fill 6 m`), computed from a few ground samples. That's cheap text, not a preview.
- **What:** for each terrain vertex in the corridor, find the closest point on the alignment (station `s`,
  offset `d`).
  - Inside the section, the height is `spline height(s) + section(|d|)`.
  - Outside it, the height moves from the section edge toward the natural ground at `CutSlope` (ground above) or
    `FillSlope` (ground below), and stops where it meets the ground.
  - Where two corridors overlap, the vertex takes the closer spline.
- **Junctions:** a node's footprint is flattened as a plate at the node height, so arms meet cleanly. Edges next
  to a moved node are re-shaped with it.
- **Undo:** the spline command and its terrain change are **one undo step** (`terrain.BeginEdit()` inside the same
  command).
- **Delete:** the ground **stays shaped**, as in Cities: Skylines. The player sculpts it back if they want.
- **Water:** nothing special. The terrain package updates water, lakes and ground masks after any edit, so a
  canal cut fills with water on its own if it connects to a water source.
- The shaping math is Core code working on a small height-grid interface (`IHeightEdit`: read, write, cell size).
  The Godot side hands it the terrain's `edit.Heights`.

## Profiles (`SplineProfile`)

A Godot `Resource` wrapping a plain-C# `ProfileRules` record. Consumers create one `.tres` per network type. The
numbers are game feel, not engineering standards.

| Field | Meaning | Street | Highway | Rail | Canal | Fence |
|---|---|---|---|---|---|---|
| `Width` | corridor width (snapping, junction cut-back, collision) | 12 m | 24 m | 5 m | 14 m | 0.3 m |
| `DefaultRadius` | corner radius a new corner gets | 16 m | 300 m | 500 m | 40 m | 0 |
| `MinRadius` | lower limit (below = invalid unless Anarchy) | 10 m | 200 m | 300 m | 25 m | 0 |
| `AllowHardCorners` | Alt-click makes a sharp corner | yes | no | no | no | yes (default) |
| `SpiralLength` | transition in/out of each arc | 0 | 60 m | 80 m | 0 | 0 |
| `JunctionKind` | `Node` (any branch), `Turnout` (tangent branch only), `Join` (no junction shape), `None` | Node | Turnout (ramps) | Turnout | Node | Join |
| `MinJunctionAngle` | smallest angle between arms at a `Node` junction | 30° | n/a | n/a | 45° | 0° |
| `TurnoutMaxAngle` | largest branch angle for `Turnout` | n/a | 8° | 6.3° (1:9) | n/a | n/a |
| `MaxGrade` | steepest slope | 12 % | 5 % | 2.5 % | 0 % | follows ground |
| `SnapLength` | length step | 8 m | 8 m | 8 m | 8 m | 2 m |
| `SnapUnitName` | what one step is called in tags ("40 m gap (5 lots)") | lot | lot | lot | lot | post |
| `SpeedFromRadius` | show a speed readout from `v = √(a·R)` with lateral `a` | off | on, a = 2.0 | on, a = 2.2 | off | off |
| `ParallelPresets` | named offset sets for Parallel mode | – | carriageways | twin track | towpath | – |
| `VerticalMode` | default station mode | Ground | Ground | Ground | Absolute (level water) | Ground |
| `Shaping` | shape the ground with `Section` | Section | Section | Section | Section | None |
| `Section` | cross-section points (see Terrain shaping) | flat 12 m + shoulder | flat 24 m + shoulder | ballast bed | 3 m deep channel | – |
| `CutSlope` / `FillSlope` | blend angle back to natural ground | 1:2 / 1:2 | 1:2 / 1:3 | 1:1.5 / 1:2 | 1:2 / 1:2 | – |
| `GroundSmoothing` | how far along the line the ground is averaged for `Ground` stations (short = hugs hills) | 20 m | 250 m | 400 m | level | 0 (exact) |
| `Edge` | how the section meets the ground: `Slope`, `Wall`, or `Auto` (walls above `WallAbove`) | Auto, 3 m | Slope | Auto, 4 m | Auto, 2 m | – |
| `MaxCutFill` | above this the edge turns amber ("consider a bridge or tunnel") | 8 m | 15 m | 15 m | 6 m | – |
| `SnapProviders` | which snaps and guides this profile offers | all | all | all | all | no parallel |

Profiles can also restrict which other profiles they connect to (a canal doesn't join a road; a road crosses a canal
only as a bridge). That's `ConnectsTo`, a list of profile ids (tags later). Two profiles connect when **each** accepts
the other (its own id always counts); `JunctionKind.None` never connects.

## Player controls

One Draw tool with four modes and one Edit tool. Camera keys are unchanged from the terrain experiment
(WASD, Q/E, R/F, Z/X, wheel, middle-drag).

### Draw tool
| Input | Action |
|---|---|
| LMB | place a point: the first starts, each later one **builds the leg up to it** at once (one undo step); the chain goes on from there, so only the leg to the cursor is a preview. On a dead end it also finishes |
| Double-click, Enter | finish: end the chain (the legs are already built) |
| RMB | stop: end the chain, dropping the preview leg (built legs stay) |
| Esc | stop, as RMB; again leaves the tool |
| Alt + click | hard corner (only if the profile allows it; otherwise a red hint and a normal corner) |
| Shift+wheel, `[` `]` | radius of the live corner (the last point placed, which the preview leg rounds) and of the corners after it (clamped to `MinRadius` unless Anarchy) |
| Ctrl (hold) | absolute 15° angle steps; Ctrl+Shift: 5° (a fan of step spokes shows around the leg's start) |
| Space (hold) | all snapping off |
| `1`–`4` | Draw · Curve · Freehand · Grid |
| `P` | Parallel on/off; wheel changes the offset, Alt flips the side |
| PgUp / PgDn | elevation step (1 / 2.5 / 5 / 10 m, chosen in the options bar) |
| Ctrl+A | Anarchy: ignore radius, angle and grade limits (the result shows red but is built) |
| Ctrl+Z, Ctrl+Shift+Z / Ctrl+Y | undo / redo; mid-draw, takes back the last built leg and steps back a point |

**Modes** all produce PIs:
1. **Draw**: each click is a PI with the profile's `DefaultRadius`. This is the default mode.
2. **Curve**: CS-style three clicks: start, bend, end. The bend is the PI, and the radius is the largest one that
   fits, so the arc passes near the bend point.
3. **Freehand**: hold LMB and drag. Samples are simplified (Ramer–Douglas–Peucker) into PIs, and each PI gets a
   radius fitted to the stroke, clamped to the profile.
4. **Grid**: corner, width, depth (three clicks). Makes a block of edges with 90° `Node` junctions. Sizes snap to
   `SnapLength`.

### Edit tool (`M`)
| Input | Action |
|---|---|
| LMB on a spline | select it: shows PIs, nodes and a radius knob on each corner |
| Drag a PI / node | move it; connected edges follow and keep their radii |
| Drag a radius knob | change that corner's radius (same limits as drawing) |
| Alt + drag a PI | snap it onto the line through its neighbours (straighten) |
| RMB on a PI / node | radial menu: Smooth · Hard corner · Straighten · Delete |
| Drag on empty ground | box select; drag the selection to move it |
| Delete | delete the selection |

### Feedback
- **Colours**: blue preview / valid, amber warning (still buildable: clamped radius, tight junction), red invalid
  (below a hard limit without Anarchy, or collision), and grey/asphalt for built.
- **Cursor tag**: the leg's length (as `168 m · 21 × 8 m` when it's a whole number of steps) and its angle. The angle is
  the snapped lock (`∡ 90°`, or `30.0°` with Ctrl), else the live angle between this leg and the previous leg or the
  road the draw started on (`∡ 87°`), else the heading. Grade % joins it in S8, speed when `SpeedFromRadius` is on (S7).
- **Snap tag**: names the snap, angle or guide that caught, next to it: `snap: node`, `90° to edge`, `extension · ∡ 90°`,
  `parallel · 40 m gap (5 lots)`, `extension × extension`, `∡ 90° · square to edge`.
- **Corner tags**: `R 36 m` next to the live corner's radius knob (the radius it builds; below `MinRadius` it's red), a
  `Shift+wheel` key hint, and `Alt · hard corner` / `62° turn` on a hard corner. `Total 277 m` shows briefly after a
  finish. A red `hard corners not allowed` flashes when the profile refuses Alt.
- **Cut/fill tag** (profiles with shaping): the largest cut and fill along the centre, e.g. `cut 4 m · fill 6 m`.
- **Issue list**: validation results in plain words with the fix ("22°, min 30° · Ctrl+A allows").

### Overlay visual language
The storyboard (`docs/spline-controls.html`) sets *what* is shown; the in-game look follows Cities: Skylines 2's
road tool (the user's reference, 2026-09-30). `--storyboard=<frame>` rebuilds each storyboard frame for a check. The
feedback is drawn over the 3D view (`SplineOverlay`): line widths and text are fixed in pixels, while rings, discs
and arcs are laid on the ground plane so they follow the camera's perspective.
- **Legs**: thick white dashes (4 px, soft shadow) from point to point, the tangent polygon the corners round off.
- **Preview ribbon**: translucent light blue with a white outline along both edges (amber outline and halo when a
  corner is clamped).
- **Nodes**: white discs on the ground at the start and the cursor end; a small dot at each corner point; a ring
  knob at each arc's middle (the radius knob); a white square at a hard corner.
- **Angles**: at every corner, an arc drawn between the two legs with a dark pill next to it: `∡ 97°`. The live
  corner's pill adds its radius (`∡ 118° · R 36 m`, the radius it builds, never more than fits). The first leg gets an
  arc against the edge the draw started on. A snapped angle's pill has a blue border and says what it means
  (`∡ 90° · square`).
- **Lengths**: a pill in the middle of every leg: `↔ 130 m`; the current leg adds whole steps (`↔ 168 m · 21 × 8 m`)
  or `↔ = 100 m` for equal length, with thin step ticks and bold equal-length ticks.
- **Guides**: thick white dashes (3 px) from their source to just past the snap; the snap point gets a white ground
  ring (larger on a node, double on a guide crossing). Perpendicular: a right-angle mark at the foot. Parallel: a
  white gap bracket. The snap's pill (blue border) names it: `extension · ∡ 90°`, `parallel · 40 m gap (5 lots)`.
- **Hints**: a stack of pills next to the cursor saying what each input does now: `LMB Place`, `RMB Stop`,
  `Double-click Finish`, `Ctrl+Z Undo leg`, `Shift+wheel Radius`.
- **Pills**: dark rounded (`#121418`, 86 %), white sans text, the key or symbol in accent blue `#6A9CF2`; amber
  `#E5A430` warn, red `#E7654F` refused. `∡` and `↔` are drawn as symbols. Pills never overlap (they nudge down).

## Snapping and guides

Snapped values are **exact** (90.0°, 336.0 m), not "near". Catch distances are in **screen pixels** (~8 px), so
snapping feels the same at every zoom. Space held turns every snap off.

### Guides
A guide is a dashed line the cursor can lock onto. Guides are **only shown when aligned**: nothing is drawn until the
cursor comes within catch distance of one. Then that guide lights up (at most two at once), drawn from what it comes
from to just past the cursor, with a tag saying what it is. Guides come from edges and nodes near the cursor (within
~400 m).

| Guide | What it is | Tag |
|---|---|---|
| **Extension** | A straight edge end continues past its end node. An arc end continues along its end tangent. | `extension` |
| **Node alignment** | A line through another node, square to that node's edge, and along the current leg's reference directions (the previous leg or start road, and 90° to it). It lights up when the cursor lines up with the node, like Figma's smart guides. (Along the node's edge is the extension's line.) | `aligned · node` |
| **Parallel** | A line alongside a nearby edge at a clean spacing: edge-to-edge gap = 0, then steps of `SnapLength` (one lot, 8 m) up to 10 steps, measured between the two corridors' sides (half widths added). It follows arcs as concentric arcs, so a new road can run alongside a curve. It's caught at half the normal catch distance, since there's one every 8 m. | `parallel · 40 m gap (5 lots)` |
| **Perpendicular** | A line square to a nearby edge through the current leg's start. Its **foot** on the edge is a snap target of its own (priority 2), which gives a clean T at exactly 90.0°. | `90° to edge` |
| **Equal length** | Not a line: a tick on the current leg and on the matched leg when their lengths are equal. It matches the previous leg or a nearby edge's leg (PI to PI). | `100 m` / `= 100 m` |

**Guide crossings** (e.g. extension × node alignment, or parallel × perpendicular) are the strongest guide snap. The
point where two guides meet is where a planned grid wants the next corner.

### Angles
The soft angle snaps to **square (90°), diagonal (45°) and straight on** against two references: the road the draw
started on (if the first click snapped to a node or edge) and the previous leg. Whichever target is closer wins. The
tag names the angle *between the roads* and what it means: `∡ 90° · square to edge`, `∡ 135° · diagonal to leg`,
`∡ 180° · straight on`. Ctrl replaces it with absolute 15° steps (5° with Shift): `30.0° · Ctrl`.

### Priority
1. Existing node (radius ~ half the profile width)
2. The perpendicular foot from the leg's start, then an existing edge (T-junction point, closest point on the alignment)
3. **Direction lock**: Ctrl's absolute steps, else the soft angle above
4. With a lock, a guide (or guide crossing) only picks **where along the locked direction** the point lands
   (`extension · ∡ 90°`); failing that, the length snaps (6). A guide never pulls a leg off its angle.
5. With no lock: guide crossing, then a single guide (extension, node alignment, parallel, perpendicular)
6. Length in `SnapLength` steps along the current leg, or equal length (equal wins a tie)

Guides rank **below** nodes, edges and angles. In CS2, guide snapping "can often break grids if left on", and players
turn it off. Keeping guides below angles and lots, exact, and shown only when aligned is the answer to that.

Each snap and each guide type toggles in the options bar, as in CS2. A profile can switch providers off
(`SnapProviders`), so a fence doesn't offer parallel-to-highway guides unless it wants them; the bar greys those out.

## Junctions
- They form automatically when a new edge ends on, or crosses, an edge whose profile is in `ConnectsTo`, and where a
  spline crosses or ends on **itself** (a loop, a figure eight, a chain coming back across its own first leg), for a
  profile that joins its own kind.
- A node's kind is the strictest of its arms' profiles: `Turnout`, then `Node`, then `Join`.
- `Node` kind: splits both edges. Arms are cut back from the node centre so the corners fit. The addon computes the
  **junction footprint** (the arm cut-backs and curb corner arcs from the arm widths and the narrower arm's
  `DefaultRadius`), for three or more arms. The consumer draws it.
- `Turnout` kind: a branch has to leave tangentially, within `TurnoutMaxAngle` (every arm must run along another
  arm's line within that angle). A square attempt shows red, and the tool offers the nearest legal turnout as a
  ghost: leaving along the line, curving at `MinRadius`, the arc starting at the switch. A click takes it.
- `Join` kind: edges meet at a shared node with no footprint (fences, walls).
- **Continuing a dead end**: a draw that starts or ends on a node with only one edge of the **same profile** doesn't
  make a junction there. It extends that edge: the node becomes an ordinary corner (PI) of one longer edge, with the
  draw's pending radius (Shift+wheel sizes it live, Alt makes it hard), so every corner rule applies as if it had been
  drawn in one go: clamping (amber), `MinRadius` (red), reversals. The old edge's other corners keep their built
  radius. The snap tag says `continue · <profile>`, and the preview shows the whole road it becomes. Clicking a dead
  end after the first point also finishes the draw (hint `LMB Place and finish`), no double-click needed. A draw back onto
  the other end of the same edge closes a loop instead. See `docs/dead-end-joins.html`.
- Two arms meeting at an angle that can't be one edge (two profiles, or what a delete leaves) keep their node, and
  the addon gives the **bend fill**: the outside of the bend, from one arm's side round to the other's.
- An angle below `MinJunctionAngle` (the strictest Node arm's) is amber, and always buildable.
- A crossing that isn't allowed by `ConnectsTo` is red, or becomes a bridge/tunnel if the vertical gap is enough
  (later milestone).

## Validation

Each edge and node gets a list of issues `{Severity: Warn|Invalid, Code, Message, Where}`. The checks are: radius
(clamped → Warn, below `MinRadius` → Invalid), junction angle, turnout angle, grade, self-overlap, overlap with
other corridors, and too-short edges. Warn builds. Invalid is refused unless Anarchy is on; Anarchy doesn't change a
severity, so what it builds stays red. Validation is pure Core code, so headless checks can run it.

## Hooks for consumers

These are C# events and interfaces on the Godot side, with plain data only:
- `EdgeAdded / EdgeChanged / EdgeRemoved(edgeId)`, `NodeChanged(nodeId)`, and `GraphReplaced` (load/undo). They fire
  once per frame, batched, like the terrain's `HeightsChanged`.
- `ISplineVisual`: the consumer builds the mesh for an edge or a junction footprint. The testbed ships a flat-ribbon
  visual.
- `ISplineCost` (optional): cost per metre, per junction, per grade, for the HUD.
- `CorridorOf(edgeId)`: polygon + heights, for the consumer's zoning and placement. (Terrain shaping is built in:
  see Terrain shaping.)
- `ShapingOverride` (optional): a consumer can replace or post-process the section per edge. For example, a road
  experiment might widen the section where a bus stop sits.
- Custom data copies across split/merge through `ISplineDataPolicy` (the default copies it unchanged).
- The terrain goes through an `IGround` adapter (`Raycast`, `GetHeight`, `HeightsChanged` → re-conform `Ground`
  stations). The Godot side implements it with `citysim_terrain`, so Core never references the terrain.

## Undo, save

- Every tool action is one command on the graph (add / remove / move / set radius / split / merge). There's one undo
  stack, and a spline edit that also shapes terrain joins the terrain's undo step. (Implementation: whole-graph
  snapshots, cheap because alignments are shared between them.) Each leg a click builds is one command. While
  drawing, Ctrl+Z takes back the last leg and the draw goes on from the point before; with no draw in progress it
  undoes the last graph command.
- Save: a versioned graph file (nodes, edges, PIs, stations, profile ids, custom data blobs), next to the `.csmap`.
  Profile ids are strings so mods can add profiles.

## Deliberately later
Lanes, markings and traffic rules (TM:PE, Intersection Marking Tool territory) belong to the consumers. The addon
only stores their custom data. Also deferred: roundabout and prefab junction placement, bridges and tunnels, the
Replace/Upgrade tool, and copy/paste of whole layouts. Each is a milestone once the basics are in.
