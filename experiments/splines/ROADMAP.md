# Splines Experiment: Roadmap

Working document for the spline addon. **Keep it updated**: tick items off, add findings, and note decisions when a
milestone lands. A new session reads this file, then `DESIGN.md` (the spec) and `README.md`.

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
$G --path . -- --flat --screenshot=out.png --cam=1000,1000,300,50,30
```

Each milestone adds a `--demo-<name>` self-check (headless, prints `all ok` or the failures) and, where visual, a
scripted `--screenshot` scene. See the milestones.

## Milestones

### ✅ S0: Skeleton
- `src/Splines/Core/` (`CitySim.Splines`) and `src/Splines/Godot/` (`CitySim.Splines.Godot`).
- `ProfileRules` (plain record, every `DESIGN.md` field) + enums (`JunctionKind`, `VerticalMode`, `ShapingMode`,
  `EdgeMode`, `[Flags] SnapProviders`, `DrawMode`). `SplineProfile` (`[GlobalClass]` resource) → `ToRules()`.
- Test profiles in `profiles/`: `street`, `avenue`, `highway`, `rail`, `canal`, `fence` (values from the `DESIGN.md`
  table; avenue = 24 m, R 40).
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
- Not yet: `Spiral` is stored on `Pi` but unused until S7. Validation (clamped → Warn) comes with S4.

### ⬜ S2: Draw tool (mode 1) on terrain
- Terrain picking through `IGround` (`Terrain.Raycast`). Click / double-click / Enter / RMB / Esc.
- Auto-rounded corners, Alt hard corner (refused by profile), Shift+wheel / `[` `]` radius.
- Preview ribbon (blue), built ribbon (profile colour), edges draped on the ground (sampled heights).
- Cursor tag: length, heading, turn angle, radius. One undo stack for add/remove.
- `--demo-draw`: scripted clicks for each test profile, then screenshot. Then **move the addon to
  `packages/citysim_splines/`** (see tech decisions).

### ⬜ S3: Snapping and guides
- Providers in `DESIGN.md` order, exact values, screen-pixel catch distance, snap tag, Space to disable, a toggle row
  in the options bar.
- Guides (`DESIGN.md` → Snapping and guides), **shown only when aligned**, at most two at once:
  - extension (straight and arc ends)
  - node alignment (Figma-style: other nodes' edge directions and square to them, plus the current leg's
    reference directions)
  - parallel to nearby edges at lot steps edge-to-edge, following arcs
  - perpendicular to nearby edges
  - equal-length ticks
  - guide crossings as the strongest guide snap
- Length ticks along the preview.
- `--demo-snap`: each provider and guide catches when expected and loses to a higher one; a guide crossing beats a
  single guide; a parallel guide along an arc stays concentric; Space disables all of them.

### ⬜ S4: Graph and junctions
- Split on end/cross, merge on delete, `ConnectsTo`.
- `Node` / `Turnout` / `Join` kinds, junction angle checks, and footprint (cut-back + curb arcs) as data, drawn flat.
- Validation issues and colours (blue/amber/red), plus Anarchy (Ctrl+A).
- `--demo-junctions`: T, X, a too-sharp angle, a rail square branch refused, and a legal turnout.

### ⬜ S5: Edit tool
- Select, drag PI/node, radius knob, Alt-straighten, radial menu, box select + move, delete. Everything undoable.
- `--demo-edit-splines`.

### ⬜ S6: Curve, Freehand and Grid modes
- Curve: 3 clicks → one PI with the largest fitting radius.
- Freehand: drag → RDP simplify → PIs with fitted radii.
- Grid: 3 clicks → a block of edges and junctions, sized to `SnapLength`.

### ⬜ S7: Transition spirals and speed
- Clothoid in/out at each arc (profile `SpiralLength`), clamped with the arc.
- Speed readout from the tightest radius (`SpeedFromRadius`), and a curvature strip in the HUD for the selected edge.

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
- Should an amber clamped radius on rail/highway build as is (current plan), or refuse until there's room?
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
