# CitySim Roads

Where roads are built and checked: the build UI with the Roads and Terrain categories, roads drawn with the splines
addon and rendered as real road meshes. See ROADMAP.

Since zoning's Z0 (2026-10-06) the code and content live in packages, symlinked into `addons/`:

- `packages/citysim_roads/`: road types, styles, rendering, crossings, lane links, the road and terrain tools, and
  their content. **Its README is the content format.**
- `packages/citysim_build_ui/`: the bar, tray, cards, hover card, `UiTheme`, icons and `ContentLibrary`.

This project keeps the scene (`scenes/Main.tscn`), the start-up wiring (`src/App.cs`) and the demos
(`src/Demos/`: `--demo-*` checks and `--ui=` for screenshots).
