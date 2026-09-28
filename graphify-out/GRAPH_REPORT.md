# Graph Report - CitySim  (2026-09-29)

## Corpus Check
- 127 files · ~133,794 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 252 file(s) not represented in the graph (top: .uid 101, .import 57, .exr 35)

## Summary
- 1905 nodes · 4001 edges · 139 communities (104 shown, 35 thin omitted)
- Extraction: 91% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 205 edges (avg confidence: 0.81)
- Token cost: 187,000 input · 0 output

## Community Hubs (Navigation)
- Core Terrain System
- Camera System
- Terrain Rendering
- Terrain Tools
- Water Simulation
- Terrain Generation
- Erosion Simulation
- UI Panels
- Material & Themes
- Sculpting Operations
- Module 10
- Module 11
- Module 12
- Module 13
- Module 14
- Module 15
- Module 16
- Module 17
- Module 18
- Module 19
- Module 20
- Module 21
- Module 22
- Module 23
- Module 24
- Module 25
- Module 26
- Module 27
- Module 28
- Module 29
- Module 30
- Module 31
- Module 32
- Module 33
- Module 34
- Module 35
- Module 36
- Module 37
- Module 38
- Module 39
- Module 40
- Module 41
- Module 42
- Module 43
- Module 44
- Module 45
- Module 46
- Module 47
- Module 48
- Module 49
- Module 50
- Module 51
- Module 52
- Module 53
- Module 54
- Module 55
- Module 56
- Module 57
- Module 58
- Module 59
- Module 60
- Module 61
- Module 62
- Module 63
- Module 64
- Module 65
- Module 66
- Module 67
- Module 69
- Module 70
- Module 71
- Module 72
- Module 73
- Module 74
- Module 75
- Module 76
- Module 77
- Module 78
- Module 79
- Module 80
- Module 81
- Module 82
- Module 83
- Module 84
- Module 85
- Module 86
- Module 87
- Module 88
- Module 89
- Module 90
- Module 91
- Module 92
- Module 93
- Module 94
- Module 95
- Module 96
- Module 97
- Module 98
- Module 99
- Module 100
- Module 101
- Module 102
- Module 104
- Module 105
- Module 106
- Module 107
- Module 108
- Module 109
- Module 110
- Module 111
- Module 113
- Module 114
- Module 115
- Module 116
- Module 117
- Module 118
- Module 119
- Module 120
- Module 121
- Module 122
- Module 123
- Module 125
- Module 126
- Module 127
- Module 128
- Module 129
- Module 130
- Module 131
- Module 132
- Module 133
- Module 134
- Module 135
- Module 136
- Module 137
- Module 138

## God Nodes (most connected - your core abstractions)
1. `Terrain` - 118 edges
2. `TerrainToolController` - 109 edges
3. `WaterSim` - 98 edges
4. `CsWater` - 96 edges
5. `FastNoiseLite` - 77 edges
6. `HeightMap` - 66 edges
7. `CityCamera` - 64 edges
8. `TerrainTheme` - 49 edges
9. `SplatMap` - 44 edges
10. `WaterSourceTool` - 43 edges

## Surprising Connections (you probably didn't know these)
- `Terrain System` --part_of--> `Layer System`  [HIGH]
  experiments/terrain/README.md → experiments/terrain/addons/terrain_3d/icons/layers.svg
- `CitySim` --part_of--> `Terrain System`  [HIGH]
  CLAUDE.md → experiments/terrain/README.md
- `Region Add Tool` --uses--> `Terrain System`  [HIGH]
  experiments/terrain/addons/terrain_3d/icons/region_add.svg → experiments/terrain/README.md
- `Region Remove Tool` --uses--> `Terrain System`  [HIGH]
  experiments/terrain/addons/terrain_3d/icons/region_remove.svg → experiments/terrain/README.md
- `Terrain System` --uses--> `C#`  [HIGH]
  experiments/terrain/README.md → CLAUDE.md

## Import Cycles
- None detected.

## Communities (139 total, 35 thin omitted)

### Community 0 - "Core Terrain System"
Cohesion: 0.08
Nodes (29): CitySim.UI, CitySim.Tools, CitySim.TerrainSystem.Erosion, CitySim.WaterSystem, CitySim.TerrainSystem.Generation, CitySim.CameraSystem, CitySim.Editor, CitySim.Debug (+21 more)

### Community 1 - "Camera System"
Cohesion: 0.05
Nodes (42): Environment, CityCamera, BoostMultiplier, Camera, ClearanceFar, ClearanceNear, Distance, EdgeMargin (+34 more)

### Community 2 - "Terrain Rendering"
Cohesion: 0.04
Nodes (44): Callable, Terrain, Bounds, CellSize, CellsX, CellsZ, DebugView, DefaultTheme (+36 more)

### Community 3 - "Terrain Tools"
Cohesion: 0.05
Nodes (44): BrushRotationMode, Fixed, Follow, Random, TerrainToolController, BrushAngle, BrushIndex, BrushRadius (+36 more)

### Community 4 - "Water Simulation"
Cohesion: 0.05
Nodes (44): CsWater, allocated, awake, calm, carry, changed, d, data (+36 more)

### Community 5 - "Terrain Generation"
Cohesion: 0.06
Nodes (35): WaterSim, CellSize, ClampHits, Depth, Factor, GridLimit, Ground, GroundVersion (+27 more)

### Community 6 - "Erosion Simulation"
Cohesion: 0.10
Nodes (15): Field, Depth, Paint, Pollution, Params, Source, WaterNative, Directory (+7 more)

### Community 7 - "UI Panels"
Cohesion: 0.09
Nodes (15): Func, Step, HeightMap, Buffer, CellSize, Data, Depth, SizeX (+7 more)

### Community 8 - "Material & Themes"
Cohesion: 0.08
Nodes (24): Array, TerrainTheme, AlbedoArrayPath, Deposit, DepositHeavy, Description, Dir, DisplayName (+16 more)

### Community 9 - "Sculpting Operations"
Cohesion: 0.09
Nodes (16): SplatMap, All, AllocatedTiles, CellSize, Depth, Palette, ThemeId, TilesX (+8 more)

### Community 10 - "Module 10"
Cohesion: 0.12
Nodes (12): BinaryWriter, MapFile, BinaryReader, ReadOnlySpan, Span, Stream, WaterData, WaterFile (+4 more)

### Community 11 - "Module 11"
Cohesion: 0.15
Nodes (19): Entry, Tile, UndoChange, UndoStack, Bytes, CanRedo, CanUndo, Capacity (+11 more)

### Community 12 - "Module 12"
Cohesion: 0.11
Nodes (19): WaterSourceTool, Depth, FlowRate, Hovered, IsDragging, Kind, MaxFlow, PickedLevel (+11 more)

### Community 13 - "Module 13"
Cohesion: 0.10
Nodes (21): algorithm, atomic, condition_variable, F, thread, vector, Run(), ThreadPool (+13 more)

### Community 14 - "Module 14"
Cohesion: 0.11
Nodes (8): CellularDistanceFunction, CellularReturnType, DomainWarpType, FastNoiseLite, FractalType, NoiseType, RotationType3D, TransformType3D

### Community 15 - "Module 15"
Cohesion: 0.13
Nodes (10): WaterGrid, Depth, Empty, IsEmpty, TilesX, TilesZ, Width, Action (+2 more)

### Community 16 - "Module 16"
Cohesion: 0.15
Nodes (8): ConfigPanel, Action, Button, CheckButton, Control, GridContainer, Label, Texture2D

### Community 17 - "Module 17"
Cohesion: 0.23
Nodes (10): Brush, Round, BrushMask, Name, Size, SculptOps, Action, HeightMap (+2 more)

### Community 18 - "Module 18"
Cohesion: 0.17
Nodes (23): BrushLibrary, BrushMask, Noise Patch Brush, Plateau Brush, Ridged Brush, Soft Round Brush, Splatter Brush, Streaks Brush (+15 more)

### Community 19 - "Module 19"
Cohesion: 0.14
Nodes (8): CanvasLayer, DebugOverlay, CityCamera, Terrain, Tools, Label, Func, Queue

### Community 20 - "Module 20"
Cohesion: 0.13
Nodes (13): Terrain3DBridge, LastPushRegions, RenderedX, RenderedZ, Camera3D, Func, GodotObject, Image (+5 more)

### Community 21 - "Module 21"
Cohesion: 0.14
Nodes (8): HeightmapImage, BitDepth, Height, Pixels, Range, Width, ReadOnlySpan, Stream

### Community 22 - "Module 22"
Cohesion: 0.22
Nodes (12): ChannelMeasure, GradePercent, ChannelOps, ChannelPoint, ChannelProfile, ChannelShape, Box, FlatBed (+4 more)

### Community 23 - "Module 23"
Cohesion: 0.17
Nodes (11): CenterContainer, Control, GeneratedMapRequest, MainMenu, Action, Button, GridContainer, Label (+3 more)

### Community 24 - "Module 24"
Cohesion: 0.11
Nodes (17): ErosionSlot, Edge, Fade, LimitSlope, Material, MaxSlope, Noise, NoiseSize (+9 more)

### Community 25 - "Module 25"
Cohesion: 0.14
Nodes (13): WaterPreview, CancellationTokenSource, Color, Label3D, MeshInstance3D, ShaderMaterial, Task, WaterFlood (+5 more)

### Community 26 - "Module 26"
Cohesion: 0.18
Nodes (10): ToolCatalog, ToolCategory, ToolDef, ToolTab, Texture2D, ToolPanel, Button, Dictionary (+2 more)

### Community 27 - "Module 27"
Cohesion: 0.16
Nodes (11): ErosionPanel, Terrain, Tools, Action, Button, CancellationTokenSource, Control, Func (+3 more)

### Community 28 - "Module 28"
Cohesion: 0.12
Nodes (5): Action, InputEvent, InputEventKey, InputEventMouseButton, InputEventMouseMotion

### Community 29 - "Module 29"
Cohesion: 0.20
Nodes (5): IEnumerable, List, Vector2, Vector3, MouseButton

### Community 30 - "Module 30"
Cohesion: 0.18
Nodes (12): Aabb, Page, WaterSurface, ArrayMesh, Image, ImageTexture, Material, MeshInstance3D (+4 more)

### Community 31 - "Module 31"
Cohesion: 0.19
Nodes (16): CsWaterParams, CS_API, cs_water_changed_tiles(), cs_water_clear(), cs_water_commit(), cs_water_create(), cs_water_destroy(), cs_water_fill_sources() (+8 more)

### Community 32 - "Module 32"
Cohesion: 0.11
Nodes (18): EdgeMode, Clamp, Fill, Mirror, Tile, ImagePlacement, Edges, Highest (+10 more)

### Community 33 - "Module 33"
Cohesion: 0.11
Nodes (17): TerrainMaterial, Albedo, DetailContrast, DisplayName, FarTileSize, Height, Id, Label (+9 more)

### Community 34 - "Module 34"
Cohesion: 0.29
Nodes (5): ControlOp, PaintOps, SplatMap, Vector2, VertexRect

### Community 35 - "Module 35"
Cohesion: 0.13
Nodes (15): GenPreset, GenPresets, Default, NoiseSettings, BaseHeight, Flatness, Frequency, Gain (+7 more)

### Community 36 - "Module 36"
Cohesion: 0.15
Nodes (13): WaterPanel, Sim, Terrain, Tools, Action, Button, CheckButton, Control (+5 more)

### Community 37 - "Module 37"
Cohesion: 0.12
Nodes (17): ErosionSettings, Capacity, DepositSpeed, DrainDepth, Droplets, ErodeSpeed, Evaporation, Gravity (+9 more)

### Community 38 - "Module 38"
Cohesion: 0.15
Nodes (7): PauseMenu, Erosion, Generator, ThemePanel, Tools, Water, InputEvent

### Community 39 - "Module 39"
Cohesion: 0.16
Nodes (8): GeneratorPanel, Tools, CancellationTokenSource, CheckButton, Image, ImageTexture, Label, TextureRect

### Community 40 - "Module 40"
Cohesion: 0.20
Nodes (3): WaterDemo, Action, Func

### Community 42 - "Module 42"
Cohesion: 0.12
Nodes (16): GenSettings, Cells, CellSize, FlatHeight, GentleShores, Image, ImageName, Noise (+8 more)

### Community 43 - "Module 43"
Cohesion: 0.22
Nodes (3): Func, IEnumerable, WaterSource

### Community 44 - "Module 44"
Cohesion: 0.19
Nodes (7): WaterSourceMarkers, ArrayMesh, Color, Dictionary, Label3D, MeshInstance3D, StandardMaterial3D

### Community 45 - "Module 45"
Cohesion: 0.26
Nodes (10): Down(), Left(), Near, b, c, l, r, t (+2 more)

### Community 46 - "Module 46"
Cohesion: 0.15
Nodes (14): CityCamera, CitySim, C#, Multimesh Rendering, Region Add Tool, Region Remove Tool, Wetness Property, Godot 4.7.2 (+6 more)

### Community 47 - "Module 47"
Cohesion: 0.16
Nodes (14): Cory Petkovsek, FastNoiseLite, GDExtension, GenPresets, Generator Panel, HeightMap, HeightmapImage, MapFile (+6 more)

### Community 48 - "Module 48"
Cohesion: 0.16
Nodes (11): CsErosionParams, vector, Eroder, brush, cell, d, h, margin (+3 more)

### Community 49 - "Module 49"
Cohesion: 0.22
Nodes (7): EditorInspectorPlugin, EditorPlugin, ThemeInspector, ThemeToolsPlugin, GodotObject, Label, List

### Community 50 - "Module 50"
Cohesion: 0.14
Nodes (13): TileData, conc, depth, fb, fl, fr, ft, ground (+5 more)

### Community 51 - "Module 51"
Cohesion: 0.14
Nodes (13): ShapeKind, Archipelago, Coast, Island, None, ShapeSettings, Direction, EdgeWidth (+5 more)

### Community 52 - "Module 52"
Cohesion: 0.23
Nodes (6): ThemeBaker, Action, Image, List, ReadOnlySpan, Format

### Community 53 - "Module 53"
Cohesion: 0.22
Nodes (6): MapFiles, HeightmapsDir, MapsDir, Action, FileDialog, FileDialog

### Community 55 - "Module 55"
Cohesion: 0.26
Nodes (12): cmath, cstring, blur(), chamfer(), F, encode(), encode_log(), ground_masks() (+4 more)

### Community 56 - "Module 56"
Cohesion: 0.29
Nodes (9): ErosionParams, Native, Directory, FileName, Progress, ReadOnlySpan, Span, GroundParams (+1 more)

### Community 57 - "Module 57"
Cohesion: 0.21
Nodes (8): LakeMap, CellSize, Count, Depth, Ground, Level, Width, HeightMap

### Community 58 - "Module 58"
Cohesion: 0.31
Nodes (4): GameUi, Tools, Button, Label

### Community 59 - "Module 59"
Cohesion: 0.31
Nodes (5): Terrain3DSpike, Camera3D, GodotObject, Stopwatch, Vector3

### Community 60 - "Module 60"
Cohesion: 0.31
Nodes (5): ShoreStyle, TerrainGen, Action, CancellationToken, HeightMap

### Community 61 - "Module 61"
Cohesion: 0.22
Nodes (3): TerrainSkirt, MeshInstance3D, TextureLayered

### Community 62 - "Module 62"
Cohesion: 0.45
Nodes (6): Action, Control, Func, GridContainer, HSlider, List

### Community 63 - "Module 63"
Cohesion: 0.21
Nodes (8): Entry, BrushLibrary, Entry, Mask, ResPath, Texture, ImageTexture, Texture2D

### Community 64 - "Module 64"
Cohesion: 0.32
Nodes (4): cs_water_drain(), cs_water_set_ground(), cs_water_set_sources(), cell

### Community 65 - "Module 65"
Cohesion: 0.18
Nodes (10): AppMode, Game, MapEditor, LoadedMapRequest, MapRequest, MapSession, CurrentPath, Mode (+2 more)

### Community 66 - "Module 66"
Cohesion: 0.24
Nodes (4): ThemeLibrary, All, IReadOnlyList, List

### Community 69 - "Module 69"
Cohesion: 0.18
Nodes (11): ErosionSlotInfo, ErosionSlotKind, Deposit, DepositHeavy, Scour, ScourHeavy, Shore, ShoreFringe (+3 more)

### Community 70 - "Module 70"
Cohesion: 0.17
Nodes (12): TerrainTool, Channel, Level, None, Paint, Shift, Slope, Smooth (+4 more)

### Community 71 - "Module 71"
Cohesion: 0.20
Nodes (9): BottomBar, Action, Button, Dictionary, HBoxContainer, IEnumerable, Button, Texture2D (+1 more)

### Community 72 - "Module 72"
Cohesion: 0.20
Nodes (7): ThemePanel, Terrain, Tools, GridContainer, Label, OptionButton, HFlowContainer

### Community 73 - "Module 73"
Cohesion: 0.20
Nodes (9): LakePlan, Labels, Lakes, Width, LakeSources, PlannedLake, CancellationToken, IReadOnlyList (+1 more)

### Community 74 - "Module 74"
Cohesion: 0.29
Nodes (11): Height Add Tool, Height Slope Tool, Height Smooth Tool, Hills Brush, Noise Brush, Plateau Brush, Height Divide Tool, Height Map (+3 more)

### Community 77 - "Module 77"
Cohesion: 0.20
Nodes (4): ShaderMaterial, Action, LakeSourcesAdded, Variant

### Community 78 - "Module 78"
Cohesion: 0.24
Nodes (6): WaterFlowArrows, ArrayMesh, Color, MultiMesh, MultiMeshInstance3D, Node3D

### Community 79 - "Module 79"
Cohesion: 0.20
Nodes (10): ambientCG, CitySim Themes Plugin, ErosionSlot, GDShader, TerrainMaterial, Terrain SDK, TerrainSkirt, TerrainTheme (+2 more)

### Community 80 - "Module 80"
Cohesion: 0.33
Nodes (5): F, cs_water_raise_tile(), cs_water_read_tiles(), cs_water_set_tile(), ForTile()

### Community 81 - "Module 81"
Cohesion: 0.27
Nodes (6): ErosionSim, Action, CancellationToken, HeightMap, ErosionParams, ProgressCall

### Community 82 - "Module 82"
Cohesion: 0.22
Nodes (5): WaterSourceKind, Lake, River, Sea, Stream

### Community 83 - "Module 83"
Cohesion: 0.28
Nodes (9): Color Picker Tool, Color Paint Tool, Navigation Tool, Texture Paint Tool, Texture Spray Tool, Layer System, Painting Tools, Splat Map (+1 more)

### Community 84 - "Module 84"
Cohesion: 0.22
Nodes (9): CsWaterSource, vector, Source, cells, s, weight_sum, SourceCell, i (+1 more)

### Community 85 - "Module 85"
Cohesion: 0.22
Nodes (8): LakeSettings, GullyMinArea, MinArea, MinDepth, RiverMinArea, Lakes, CancellationToken, GroundParams

### Community 86 - "Module 86"
Cohesion: 0.22
Nodes (8): WaterSettings, EvaporationMmPerMin, OpenEdges, PaintFadeHours, PaintMinutes, Paused, PollutionHalfLifeMin, Speed

### Community 87 - "Module 87"
Cohesion: 0.25
Nodes (9): River Source, Sea Source, Stream Source, WaterFlowArrows, Water Panel, WaterSim, WaterSource, WaterSourceMarkers (+1 more)

### Community 89 - "Module 89"
Cohesion: 0.32
Nodes (8): Erosion Native Library, Erosion Panel, Erosion System, Hydraulic Erosion, LakeMap, Lake Source, Priority-Flood Algorithm, Sebastian Lague

### Community 90 - "Module 90"
Cohesion: 0.25
Nodes (8): CellularReturnType, CellValue, Distance, Distance2, Distance2Add, Distance2Div, Distance2Mul, Distance2Sub

### Community 92 - "Module 92"
Cohesion: 0.39
Nodes (3): NoiseSource, ReadOnlySpan, Span

### Community 93 - "Module 93"
Cohesion: 0.25
Nodes (3): Func, Texture2D, Vector3

### Community 95 - "Module 95"
Cohesion: 0.29
Nodes (7): ChannelOps, Channel Tool, Box Channel Variant, Flatbed Channel Variant, Rounded Channel Variant, V-Channel Variant, Channel System

### Community 96 - "Module 96"
Cohesion: 0.52
Nodes (7): CsGroundParams, CsProgress, CS_API, cs_erode(), cs_find_lakes(), cs_find_water(), find_water()

### Community 98 - "Module 98"
Cohesion: 0.29
Nodes (7): FractalType, DomainWarpIndependent, DomainWarpProgressive, FBm, None, PingPong, Ridged

### Community 99 - "Module 99"
Cohesion: 0.29
Nodes (7): NoiseType, Cellular, OpenSimplex2, OpenSimplex2S, Perlin, Value, ValueCubic

### Community 100 - "Module 100"
Cohesion: 0.48
Nodes (5): UiTheme, Theme, Color, StyleBoxFlat, Theme

### Community 101 - "Module 101"
Cohesion: 0.33
Nodes (3): mix(), Rng, s

### Community 102 - "Module 102"
Cohesion: 0.47
Nodes (3): ErosionPreset, ErosionPresets, Default

### Community 104 - "Module 104"
Cohesion: 0.33
Nodes (6): Source, Noise, Round, Splatter, Stamp, Streaks

### Community 105 - "Module 105"
Cohesion: 0.60
Nodes (5): Cancelled, breach(), priority_flood(), border, Report

### Community 106 - "Module 106"
Cohesion: 0.40
Nodes (5): CellularDistanceFunction, Euclidean, EuclideanSq, Hybrid, Manhattan

### Community 107 - "Module 107"
Cohesion: 0.40
Nodes (5): TransformType3D, DefaultOpenSimplex2, ImproveXYPlanes, ImproveXZPlanes, None

### Community 108 - "Module 108"
Cohesion: 0.40
Nodes (5): SourceType, Lake, Level, Sea, Stream

### Community 109 - "Module 109"
Cohesion: 0.50
Nodes (4): BrushCell, dx, dz, weight

### Community 110 - "Module 110"
Cohesion: 0.50
Nodes (4): DomainWarpType, BasicGrid, OpenSimplex2, OpenSimplex2Reduced

### Community 111 - "Module 111"
Cohesion: 0.50
Nodes (4): RotationType3D, ImproveXYPlanes, ImproveXZPlanes, None

### Community 113 - "Module 113"
Cohesion: 0.67
Nodes (3): Default Theme, Terrain Layers, Winter Theme

### Community 115 - "Module 115"
Cohesion: 0.67
Nodes (3): ChannelMode, FollowGround, Graded

### Community 116 - "Module 116"
Cohesion: 0.67
Nodes (3): TerrainExp, net9.0, Godot.NET.Sdk/4.7.2

## Knowledge Gaps
- **589 isolated node(s):** `net9.0`, `Godot.NET.Sdk/4.7.2`, `CitySim.Editor`, `workers_`, `m_` (+584 more)
  These have ≤1 connection - possible missing edges. (Counts symbols only; 794 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **35 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Terrain` connect `Terrain Rendering` to `Core Terrain System`, `Camera System`, `Terrain Tools`, `Terrain Generation`, `UI Panels`, `Material & Themes`, `Sculpting Operations`, `Module 10`, `Module 12`, `Module 19`, `Module 20`, `Module 25`, `Module 27`, `Module 28`, `Module 30`, `Module 36`, `Module 42`, `Module 43`, `Module 44`, `Module 53`, `Module 57`, `Module 61`, `Module 66`, `Module 72`, `Module 75`, `Module 77`, `Module 78`, `Module 85`, `Module 93`, `Module 94`, `Module 103`, `Module 112`?**
  _High betweenness centrality (0.258) - this node is a cross-community bridge._
- **Why does `DebugOverlay` connect `Module 19` to `Core Terrain System`, `Camera System`, `Module 66`, `Terrain Rendering`, `Terrain Tools`, `UI Panels`, `Module 10`, `Module 75`, `Module 81`, `Module 21`, `Module 57`, `Module 58`?**
  _High betweenness centrality (0.243) - this node is a cross-community bridge._
- **Why does `TerrainToolController` connect `Terrain Tools` to `Core Terrain System`, `Camera System`, `Terrain Rendering`, `UI Panels`, `Module 11`, `Module 12`, `Module 16`, `Module 17`, `Module 19`, `Module 22`, `Module 27`, `Module 28`, `Module 29`, `Module 33`, `Module 36`, `Module 38`, `Module 39`, `Module 43`, `Module 58`, `Module 63`, `Module 70`, `Module 72`, `Module 82`, `Module 103`, `Module 115`?**
  _High betweenness centrality (0.214) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `FastNoiseLite` (e.g. with `.NoisePatch()` and `.Streaks()`) actually correct?**
  _`FastNoiseLite` has 2 INFERRED edges - model-reasoned connections that need verification._
- **What connects `net9.0`, `Godot.NET.Sdk/4.7.2`, `CitySim.Editor` to the rest of the system?**
  _589 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Core Terrain System` be split into smaller, more focused modules?**
  _Cohesion score 0.0794148380355277 - nodes in this community are weakly interconnected._
- **Should `Camera System` be split into smaller, more focused modules?**
  _Cohesion score 0.0523532522474881 - nodes in this community are weakly interconnected._