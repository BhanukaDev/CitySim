# CitySim

A city builder in the style of Cities: Skylines, built **feature by feature**. Each feature starts as a
standalone Godot project under `experiments/<feature>/`, gets proven out, and is later merged into
the main game.

Stack: Godot 4.7.2 .NET (`/Applications/Godot_mono.app`), C# on .NET 9. C++ (GDExtension) only for
profiled hot paths.

## Shared packages
- `packages/citysim_terrain/`: the terrain + water package every project uses, symlinked in as
  `addons/citysim_terrain` (with `packages/terrain_3d` as `addons/terrain_3d`). **Its README is the API**: queries,
  the edit API, events, settings, and how to add it to a project. Edits here affect every experiment, so re-check each one.

## Experiments
- `experiments/terrain/`: terrain system and the in-app Map Editor. **Read `experiments/terrain/ROADMAP.md` first.** It has
  status, next steps, build/verify commands and decisions. Update it when a milestone changes.
- `experiments/splines/`: testbed for the **generic** spline addon (roads, rails, canals, fences, walls build on it as
  separate consumer experiments). **Read `experiments/splines/ROADMAP.md` first**, then `DESIGN.md` (the spec).
  Feature first, performance later (milestone S11).

## Working conventions
- Verify visual changes yourself before handing off: `dotnet build`, a headless run, then the
  `--screenshot=` run (see ROADMAP), and look at the PNG.
- Don't commit until the user has tried the change in Godot.
- Keep simulation-facing data (such as `HeightMap`) free of Godot types so it ports to the main game cleanly.
