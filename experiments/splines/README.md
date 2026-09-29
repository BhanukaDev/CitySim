# Splines Experiment

Splines for roads, canals, walls and fences, on the shared terrain package (`packages/citysim_terrain`, symlinked as
`addons/citysim_terrain`; API in its README). So far it's terrain only: a map, the city camera and screenshot tools.

## Run

Needs the package's native libraries and textures (see the package README; the terrain experiment's
`tools/fetch_textures.sh` does the textures). Then:

```sh
G=/Applications/Godot_mono.app/Contents/MacOS/Godot
dotnet build && $G --headless --path . --import
$G --path .                                         # a generated map; or -- --load=path.csmap / --flat / --preset=island
$G --path . -- --screenshot=out.png --cam=1300,700,120,25,30
$G --headless --path . --quit-after 200 -- --demo-edit   # edit API check: prints "Demo edit: all ok"
```

Controls: WASD move · Q/E rotate · R/F tilt · Z/X or mouse wheel zoom.

## Layout

- `src/App.cs`: start-up wiring (`TerrainCommandLine.Use()`: the map comes from the command line)
- `src/EditDemo.cs`: `--demo-edit`, the package's `Terrain.BeginEdit` / undo / events check
- `scenes/Main.tscn`: sun, environment, `Terrain`, `CityCamera`, `ScreenshotCapture`
