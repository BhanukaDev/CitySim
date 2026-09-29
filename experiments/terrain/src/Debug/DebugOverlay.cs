using Godot;
using System;
using CitySim.App;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Erosion;
using CitySim.TerrainSystem.Generation;
using CitySim.TerrainSystem.Sculpt;
using CitySim.TerrainSystem.Themes;
using System.Linq;
using CitySim.Tools;
using CitySim.UI;
using CitySim.WaterSystem;

namespace CitySim.Debug;

/// <summary>
/// On-screen stats and controls help. Adds the terrain package's <see cref="ScreenshotCapture"/> (--screenshot=out.png
/// [--screenshot-frames=60], --screenshot-zoom=a,b,c, --cam=x,z,distance,pitch,yaw, --setting=Graphics|Tuning.Name=value).
/// Also: --demo-sculpt, --demo-channel, --demo-paint, --demo-camera, --demo-mapfile,
/// --demo-heightmap, --demo-generate, --demo-erosion, --erode[=preset], --show-erosion,
/// --theme=id (switch the map's theme), --show-theme, --show-settings[=Graphics|Tuning] (the Settings panel, not paused), --demo-water (water self-checks, then sources on the map),
/// --demo-falls (a made-up map with a cliff and two falls), --falls-fx=off|low|high (waterfall curtains and mist),
/// --show-water (Water panel), --hide-water (don't draw it), --water-arrows (flow arrows on), --no-tool (--demo-water ends without a water tool out),
/// --preview-at=x,z,level (the Lake placement preview there), --stream-at=x,z,flow (a Stream source there, map metres), --water-speed=n, --water-run=seconds (simulate that long right away),
/// --pollute=kg/s (the --demo-water stream carries pollutant), --stream-flow=m³/s (its flow, default 40), --view=materials|cost|slot:&lt;n&gt;|wetlook (debug views), --demo-themes,
/// --sea=level (a Sea source at that level), --edge=line|fog|horizon (the map edge's look), --rain[=intensity] (it starts raining: the ground wets over sim time), --wetness=x (the whole map's wetness at once, 0-1),
/// --demo-scale[=cells], --flat[=height], --preset=name, --seed=n, --show-generator, --load=path, --water-cells=n (water grid side cap; 2048 = old 14 m on 28.7 km),
/// --heightmap=path[,min,max], --game (handled by MainMenu),
/// --profile[=frames] (frame times with vsync off, once the first lake search is in; --profile-during-search: right away),
/// --demo-push (Terrain3D push time per frame mid-region and on a region corner), --demo-lake-window (an edit gets a
/// window lake search, then a background full one; --lake-shot=path saves a screenshot after the window search),
/// --bake-theme=id|all (bake a theme's textures, previews and include, then quit; see ThemeBaker; run --import after) and
/// --bake-brushes (bake brush masks for import, then quit; see TextureBaker).
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    private float _demoPollution;
    private float _demoStreamFlow = 40f;
    private bool _demoNoTool;
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public Terrain? Terrain { get; set; }
    [Export] public TerrainToolController? Tools { get; set; }

    private Label _label = null!;
    private double _refresh;
    /// <summary>--profile[=frames]: frames still to wait (then measure), and what's been measured so far.</summary>
    private int _profileWait = -1, _profileFrames;
    private bool _profileDuringSearch;
    /// <summary>--demo-push: frames done, and push times (ms) mid-region and on a region corner.</summary>
    private int _pushFrame = -1;
    /// <summary>--demo-lake-window: step (-1 = off), time in it, and lake searches seen (sum of their times).</summary>
    private int _lakeStep = -1, _lakeSearches;
    private double _lakeTime, _lakeLastMs;
    private readonly System.Collections.Generic.List<double> _pushMid = new(), _pushCorner = new();
    private readonly System.Collections.Generic.List<(double Frame, double Cpu, double Gpu)> _profile = new();

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(12, 10) };
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 4);
        AddChild(_label);
        // --screenshot*, --cam, --setting (the terrain package's; every project with a Terrain has them).
        AddChild(new ScreenshotCapture { Name = "ScreenshotCapture", Terrain = Terrain, CityCamera = CityCamera });

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--profile" || arg.StartsWith("--profile="))
            {
                _profileFrames = arg.Length > "--profile=".Length && int.TryParse(arg["--profile=".Length..], out int pf) ? pf : 240;
                _profileWait = 180; // let the map, water and LODs settle
                // No vsync, so the numbers show the headroom above 60.
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            }
            else if (arg == "--demo-push")
                _pushFrame = 0;
            else if (arg == "--demo-lake-window")
                _lakeStep = 0;
            else if (arg == "--profile-during-search")
                _profileDuringSearch = true;
            else if (arg == "--demo-sculpt" && Tools is not null)
                Callable.From(Tools.RunDemo).CallDeferred();
            else if (arg == "--demo-channel" && Tools is not null)
                Callable.From(Tools.RunChannelDemo).CallDeferred();
            else if (arg == "--demo-camera" && CityCamera is not null)
                Callable.From(CityCamera.RunDemo).CallDeferred();
            else if (arg == "--demo-paint" && Tools is not null)
                Callable.From(Tools.RunPaintDemo).CallDeferred();
            else if (arg == "--demo-mapfile")
                Callable.From(RunMapFileDemo).CallDeferred();
            else if (arg == "--demo-heightmap")
                Callable.From(RunHeightmapDemo).CallDeferred();
            else if (arg == "--demo-generate")
                Callable.From(RunGenerateDemo).CallDeferred();
            else if (arg == "--show-erosion")
                Callable.From(() =>
                {
                    foreach (var node in GetParent().GetChildren())
                        if (node is GameUi ui) ui.OpenErosion();
                }).CallDeferred();
            else if (arg == "--demo-water")
                Callable.From(RunWaterDemo).CallDeferred();
            else if (arg == "--demo-falls")
                Callable.From(RunFallsDemo).CallDeferred();
            else if (arg == "--hide-water" && Terrain is not null)
                Terrain.ShowWater = false;
            else if (arg.StartsWith("--falls-fx=") && Terrain is not null
                     && System.Enum.TryParse<WaterfallQuality>(arg["--falls-fx=".Length..], ignoreCase: true, out var fallsFx))
                // --falls-fx=off|low|high: waterfall curtains and mist (graphics setting).
                Terrain.WaterfallFx = fallsFx;
            else if (arg.StartsWith("--preview-at="))
            {
                var v = arg["--preview-at=".Length..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                GetTree().CreateTimer(3.0).Timeout += () =>
                    Terrain?.PreviewSource(new WaterSource(0, WaterSourceKind.Lake, v[0], v[1], 40f, v[2], MaxFlow: 100f));
            }
            else if (arg.StartsWith("--stream-at="))
            {
                // --stream-at=x,z,flow: a Stream source at map metres (x, z) with that flow (m³/s), e.g. to check water far out.
                var v = arg["--stream-at=".Length..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Callable.From(() =>
                {
                    if (Terrain?.Water is not { } w) return;
                    w.SetSources(w.Sources.Append(new WaterSource(w.NextSourceId(), WaterSourceKind.Stream, v[0], v[1], 20f, 0f, FlowRate: v[2])));
                }).CallDeferred();
            }
            else if (arg.StartsWith("--rain") && Terrain is not null)
            {
                // --rain[=intensity]: it starts raining (the ground wets over the Weather's ramp time, on sim time).
                Terrain.Weather.Raining = true;
                if (arg.StartsWith("--rain=") && float.TryParse(arg["--rain=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float rain))
                    Terrain.Weather.Intensity = rain;
            }
            else if (arg.StartsWith("--wetness=") && Terrain is not null
                     && float.TryParse(arg["--wetness=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float wetness))
                Terrain.Weather.SetWetness(wetness);
            else if (arg.StartsWith("--sea=") && float.TryParse(arg["--sea=".Length..], System.Globalization.CultureInfo.InvariantCulture, out float seaLevel))
                // --sea=level: a Sea source at that level on the map's first border (e.g. to check the sea past the border).
                Callable.From(() =>
                {
                    if (Terrain?.Water is not { } w) return;
                    w.SetSources(w.Sources.Append(new WaterSource(w.NextSourceId(), WaterSourceKind.Sea, 0f, 0f, 0f, seaLevel)));
                }).CallDeferred();
            else if (arg.StartsWith("--edge=") && Terrain is not null
                     && System.Enum.TryParse<EdgeStyle>(arg["--edge=".Length..], ignoreCase: true, out var edge))
                // --edge=line|fog|horizon: the map edge's look (after the mode's default is set in Terrain._Ready).
                Callable.From(() => Terrain.EdgeStyle = edge).CallDeferred();
            else if (arg == "--no-tool")
                _demoNoTool = true;
            else if (arg == "--water-arrows")
                Callable.From(() => { if (Terrain is not null) Terrain.FlowArrows = true; }).CallDeferred();
            else if (arg == "--show-water")
                Callable.From(() =>
                {
                    foreach (var node in GetParent().GetChildren())
                        if (node is GameUi ui) ui.OpenWater();
                }).CallDeferred();
            else if (arg.StartsWith("--water-speed=") && float.TryParse(arg["--water-speed=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out float speed))
                Callable.From(() => { if (Terrain?.Water is { } w) w.Settings = w.Settings with { Speed = speed }; }).CallDeferred();
            else if (arg.StartsWith("--stream-flow=") && float.TryParse(arg["--stream-flow=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out float streamFlow))
                _demoStreamFlow = streamFlow;
            else if (arg.StartsWith("--pollute=") && float.TryParse(arg["--pollute=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out float pollute))
                _demoPollution = pollute;
            else if (arg.StartsWith("--water-run=") && double.TryParse(arg["--water-run=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                // After the hollow search has put lake sources in (a new map), so they're part of the run.
                GetTree().CreateTimer(2.0).Timeout += () => Terrain?.Water?.RunFor(seconds);
            else if (arg == "--show-settings" || arg.StartsWith("--show-settings="))
            {
                string? tab = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
                Callable.From(() =>
                {
                    foreach (var node in GetParent().GetChildren())
                        if (node is GameUi ui) ui.ShowSettings(tab);
                }).CallDeferred();
            }
            else if (arg == "--show-theme")
                Callable.From(() =>
                {
                    foreach (var node in GetParent().GetChildren())
                        if (node is GameUi ui) ui.OpenTheme();
                }).CallDeferred();
            else if (arg.StartsWith("--theme=") && Terrain is not null)
            {
                if (ThemeLibrary.Get(arg["--theme=".Length..]) is { } theme) Terrain.SetTheme(theme);
            }
            else if (arg.StartsWith("--view=") && Terrain is not null)
            {
                string v = arg["--view=".Length..];
                if (v == "materials") Terrain.DebugView = Terrain.MaterialDebugView;
                else if (v == "cost") Terrain.DebugView = Terrain.CostDebugView;
                else if (v.StartsWith("slot:") && int.TryParse(v[5..], out int slot)) Terrain.SlotDebug = slot;
                else if (v == "wetlook") Terrain.GroundDebug = Terrain.WetLookDebugView;
            }
            else if (arg == "--demo-themes")
                Callable.From(RunThemeDemo).CallDeferred();
            else if (arg == "--demo-erosion")
                Callable.From(RunErosionDemo).CallDeferred();
            else if (arg == "--erode" || arg.StartsWith("--erode="))
            {
                var preset = ErosionPresets.Find(arg.Length > "--erode=".Length ? arg["--erode=".Length..] : "") ?? ErosionPresets.Default;
                Callable.From(() => Erode(preset.Settings)).CallDeferred();
            }
            else if (arg == "--demo-scale" || arg.StartsWith("--demo-scale="))
            {
                int cells = arg.Length > "--demo-scale=".Length && int.TryParse(arg["--demo-scale=".Length..], out int n) ? n : 8192;
                Callable.From(() => GetTree().Quit(ScaleDemo.Run(cells) ? 0 : 1)).CallDeferred();
            }
            else if (arg == "--bake-brushes")
            {
                bool ok = TextureBaker.BakeBrushes();
                GetTree().Quit(ok ? 0 : 1);
            }
            else if (arg.StartsWith("--bake-theme="))
            {
                string id = arg["--bake-theme=".Length..];
                bool ok = true;
                foreach (var theme in ThemeLibrary.All)
                    if (id == "all" || theme.Id == id)
                        ok &= ThemeBaker.Bake(theme) is not null;
                GetTree().Quit(ok ? 0 : 1);
            }
        }
    }

    /// <summary>
    /// Water self-checks (<see cref="WaterDemo"/>), then puts a stream near the highest ground, a river on the west border
    /// and a lake in the lowest spot of this map, and simulates 15 minutes at once, for a screenshot.
    /// </summary>
    private void RunWaterDemo()
    {
        WaterDemo.Run(line => GD.Print(line));
        if (Terrain?.Map is not { } map || Terrain.Water is not { } water || Tools is null) return;
        int w = map.Width, d = map.Depth;
        (int X, int Z) high = (w / 2, d / 2), low = high, border = (0, d / 2);
        for (int z = d / 5; z < d * 4 / 5; z += 2)
            for (int x = w / 5; x < w * 4 / 5; x += 2)
            {
                if (map[x, z] > map[high.X, high.Z]) high = (x, z);
                if (map[x, z] < map[low.X, low.Z]) low = (x, z);
            }
        for (int z = d / 5; z < d * 4 / 5; z++)
            if (map[0, z] > map[0, border.Z]) border = (0, z);
        // The tool: a river placed near the west border snaps onto it, undo/redo, right-click removes it.
        Tools.Tool = TerrainTool.WaterRiver;
        int count = water.Sources.Count;
        float pz = map.SizeZ * 0.3f;
        Tools.Water.Press(true, Terrain.MapToWorld(20f, pz, Terrain.GetHeightAtMap(20f, pz)));
        bool placed = water.Sources.Count == count + 1 && water.Sources[^1] is { Kind: WaterSourceKind.River, X: 0f };
        Tools.Undo();
        bool undone = water.Sources.Count == count;
        Tools.Redo();
        bool redone = water.Sources.Count == count + 1;
        Tools.Water.Press(false, Terrain.MapToWorld(5f, pz, Terrain.GetHeightAtMap(5f, pz)));
        bool removed = water.Sources.Count == count;
        bool toolOk = placed && undone && redone && removed;
        GD.Print($"Demo water: tool snaps to the border {placed}, undo {undone}, redo {redone}, right-click removes {removed}: " +
                 (toolOk ? "ok" : "FAILED"));

        float cs = map.CellSize;
        water.SetSources([
            new WaterSource(1, WaterSourceKind.Stream, high.X * cs, high.Z * cs, 20f, 0f, FlowRate: _demoStreamFlow, Pollution: _demoPollution),
            new WaterSource(2, WaterSourceKind.River, 0f, border.Z * cs, 60f, map[0, border.Z] + 4f),
            new WaterSource(3, WaterSourceKind.Lake, low.X * cs, low.Z * cs, 60f, map[low.X, low.Z] + 6f, MaxFlow: 200f),
        ]);
        water.RunFor(900);
        Tools.Tool = _demoNoTool ? TerrainTool.None : TerrainTool.WaterRiver;
        GD.Print($"Demo water: stream at ({high.X * cs:0}, {high.Z * cs:0}), river at (0, {border.Z * cs:0}), lake at ({low.X * cs:0}, {low.Z * cs:0})");
        // For close-ups of flow foam and arrows: the deepest water running at 0.5–2 m/s once things have settled.
        GetTree().CreateTimer(20.0).Timeout += () =>
        {
            if (Terrain?.Water != water) return;
            (float X, float Z, float Depth, float Speed) best = (0, 0, 0, 0);
            for (float z = 0; z < map.SizeZ; z += water.CellSize * 2)
                for (float x = 0; x < map.SizeX; x += water.CellSize * 2)
                {
                    float d = water.DepthAt(x, z);
                    var (vx, vz) = water.VelocityAt(x, z);
                    float v = Mathf.Sqrt(vx * vx + vz * vz);
                    if (v is > 0.5f and < 2f && d > best.Depth) best = (x, z, d, v);
                }
            GD.Print($"Demo water: moderate flow at ({best.X:0}, {best.Z:0}), {best.Depth:0.00} m deep, {best.Speed:0.00} m/s");
        };
    }

    /// <summary>
    /// A made-up 1.8 km map for looking at waterfalls: a sloping plateau ending in a 20 m cliff above a sea, with a small, a narrow and a
    /// wide stream running over the edge (plateau slope 5 %), simulated 15 minutes at once. Try <c>--cam=900,900,500,30,0</c>.
    /// </summary>
    private void RunFallsDemo()
    {
        if (Terrain is null) return;
        var map = new HeightMap(513, 513, 3.5f);
        for (int z = 0; z < map.Depth; z++)
            for (int x = 0; x < map.Width; x++)
            {
                float wx = x * map.CellSize, wz = z * map.CellSize;
                float ripple = 1.5f * MathF.Sin(wx * 0.013f) * MathF.Sin(wz * 0.011f);
                float top = 90f - wz * 0.05f + ripple * 0.2f;
                // Channels cut into the plateau: a small shallow one, a narrow one and a wide one.
                top -= 1f * MathF.Max(0f, 1f - MathF.Abs(wx - 300f) / 10f);
                top -= 4f * MathF.Max(0f, 1f - MathF.Abs(wx - 600f) / 18f);
                top -= 3f * Math.Clamp((110f - MathF.Abs(wx - 1150f)) / 30f, 0f, 1f);
                float cliff = Math.Clamp((wz - 900f) / 25f, 0f, 1f);
                float below = 20f - (wz - 900f) * 0.04f + ripple;
                map[x, z] = top + (below - top) * cliff * cliff * (3f - 2f * cliff);
            }
        Terrain.SetMap(map);
        if (Terrain.Water is not { } water) return;
        water.SetSources([
            new WaterSource(1, WaterSourceKind.Stream, 600f, 200f, 12f, 0f, FlowRate: 40f),
            new WaterSource(4, WaterSourceKind.Stream, 300f, 200f, 6f, 0f, FlowRate: 2f),
            new WaterSource(2, WaterSourceKind.Stream, 1150f, 200f, 60f, 0f, FlowRate: 250f),
            new WaterSource(3, WaterSourceKind.Sea, map.SizeX * 0.5f, 0f, 0f, 10f),
        ]);
        water.RunFor(900);
    }

    /// <summary>
    /// Checks the generator: full-map timing, each preset's 257² preview against its full map, heightmap tiling, and that a
    /// run of live generator updates undoes and redoes as one step. Prints "Demo generate: ok".
    /// </summary>
    private void RunGenerateDemo()
    {
        if (Terrain?.Map is not { } map || Tools is null) return;
        bool ok = true;
        var baseSettings = Terrain.Settings with { Cells = map.Width - 1, CellSize = map.CellSize };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TerrainGen.Create(baseSettings);
        GD.Print($"Demo generate: {map.Width}² full map in {sw.ElapsedMilliseconds} ms");

        const int previewVerts = 257;
        foreach (var preset in GenPresets.All)
        {
            var s = preset.ApplyTo(baseSettings);
            sw.Restart();
            var preview = TerrainGen.Preview(s, previewVerts);
            long previewMs = sw.ElapsedMilliseconds;
            var full = TerrainGen.Create(s);
            int stride = (full.Width - 1) / (previewVerts - 1);
            double sum = 0;
            for (int z = 0; z < previewVerts; z++)
                for (int x = 0; x < previewVerts; x++)
                {
                    float d = preview[x, z] - full[x * stride, z * stride];
                    sum += d * d;
                }
            double rms = Math.Sqrt(sum / (previewVerts * previewVerts));
            var (min, max) = full.GetRange();
            var (maxSlope, p99, flat) = SlopeStats(full);
            GD.Print($"Demo generate: {preset.Name,-14} preview {previewMs} ms, range {min:0}–{max:0} m, preview vs full rms {rms:0.00} m, " +
                     $"slope max {maxSlope:0}° p99 {p99:0}°, under 5° {flat:0%}");
            if (rms > 3.0) { ok = false; GD.PushError($"Demo generate: {preset.Name} preview differs from the full map"); }
        }

        // Sea shores: ground within 3 m of sea level.
        var coast = GenPresets.Find("coast")!.ApplyTo(baseSettings);
        var coastMap = TerrainGen.Create(coast);
        var (shoreFlat, shoreP50, shoreP90) = SlopeWhere(coastMap, i => MathF.Abs(coastMap.Data[i] - coast.SeaLevel) < 3f, 5f);
        GD.Print($"Demo generate: coast: shore under 5° {shoreFlat:0%}, p50 {shoreP50:0}°, p90 {shoreP90:0}°");

        // Tiling: at half scale, the image repeats every half map.
        var image = HeightmapImage.FromHeightMap(map, out var range);
        var tiled = new HeightmapSampler(image, new ImagePlacement { Lowest = range.Min, Highest = range.Max, Scale = 0.5f, Edges = EdgeMode.Tile }, 4097); // fine enough to sample bilinearly
        float tileErr = 0;
        for (int i = 0; i < 200; i++)
        {
            float u = 0.05f + i * 0.002f, v = 0.1f + i * 0.0017f;
            tileErr = MathF.Max(tileErr, MathF.Abs(tiled.Sample(u, v) - tiled.Sample(u + 0.5f, v)));
        }
        GD.Print($"Demo generate: tile repeat error {tileErr:0.###} m");
        if (tileErr > 0.5f) { ok = false; GD.PushError("Demo generate: tiled heightmap doesn't repeat"); }

        // Live updates (several applies) undo as one step and redo to the last one.
        var before = map.Snapshot();
        Tools.ApplyGenerated(TerrainGen.Create(GenPresets.Find("island")!.ApplyTo(baseSettings)), baseSettings);
        var last = GenPresets.Find("coast")!.ApplyTo(baseSettings);
        Tools.ApplyGenerated(TerrainGen.Create(last), last);
        var after = map.Snapshot();
        Tools.CommitGenerated();
        Tools.Undo();
        bool undoOk = map.Data.SequenceEqual(before);
        Tools.Redo();
        bool redoOk = map.Data.SequenceEqual(after);
        GD.Print($"Demo generate: undo {(undoOk ? "ok" : "FAILED")}, redo {(redoOk ? "ok" : "FAILED")}");
        ok &= undoOk && redoOk;

        // Another size (the panel's Size) replaces the map: the horizon ring and water come back (ObjectDisposedException before).
        var resized = baseSettings with { Cells = map.Width - 1 == 512 ? 1024 : 512 };
        Tools.ApplyGenerated(TerrainGen.Create(resized), resized);
        int rings = Terrain.GetChildren().OfType<TerrainHorizon>().Count();
        bool resizeOk = Terrain.Map?.Width == resized.Cells + 1 && rings == 1 && Terrain.Water is not null;
        GD.Print($"Demo generate: new size {Terrain.Map?.Width}², horizon rings {rings}, water {(Terrain.Water is null ? "none" : "running")}: {(resizeOk ? "ok" : "FAILED")}");
        ok &= resizeOk;
        GD.Print(ok ? "Demo generate: ok" : "Demo generate: FAILED");
    }

    /// <summary>Erodes the current map (blocking) as one undo step.</summary>
    private void Erode(ErosionSettings settings)
    {
        if (Terrain?.Map is not { } map || Tools is null) return;
        var copy = new HeightMap(map.Width, map.Depth, map.CellSize);
        map.Data.CopyTo(copy.Data);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ErosionSim.Run(copy, settings, Terrain.SeaLevel);
        GD.Print($"Erode: {settings.Preset ?? "custom"} on {map.Width}² in {sw.ElapsedMilliseconds} ms");
        Tools.ApplyEroded(copy);
    }

    /// <summary>
    /// Checks erosion and lakes: timing per preset, how much ground moved, that a run is repeatable, that lakes sit in
    /// real depressions (level at or above the ground, never below a dry neighbour that isn't sea), and that erosion
    /// undoes and redoes as one step. Prints "Demo erosion: ok".
    /// </summary>
    /// <summary>
    /// Checks themes and painted materials: paint survives a switch to another theme and back (by material id, including
    /// materials the other theme lacks), a v3 map file keeps the theme and palette, and a v2 file loads with the default
    /// theme's legacy ids. Restores the starting theme and prints "Demo themes: ok".
    /// </summary>
    private void RunThemeDemo()
    {
        if (Terrain?.Map is not { } map || Terrain.Splat is not { } splat || Terrain.Theme is not { } start) return;
        bool ok = true;
        void Check(bool cond, string what)
        {
            GD.Print($"Demo themes: {what}: {(cond ? "ok" : "FAILED")}");
            ok &= cond;
        }
        var def = ThemeLibrary.Get(ThemeLibrary.DefaultId)!;
        var other = ThemeLibrary.All.FirstOrDefault(t => t != def);
        Terrain.SetTheme(def);

        // Two painted patches: sand (the other theme may lack it) and rock.
        var c = Terrain.Bounds.GetCenter();
        var brush = new Brush(30f, 1f);
        PaintOps.Paint(splat, new System.Numerics.Vector2(c.X, c.Y), brush, def.IndexOf("sand"), 1f);
        PaintOps.Paint(splat, new System.Numerics.Vector2(c.X + 120, c.Y), brush, def.IndexOf("rock"), 1f);
        Terrain.MarkSplatDirty(splat.All);
        int x = (int)(c.X / map.CellSize), z = (int)(c.Y / map.CellSize), x2 = (int)((c.X + 120) / map.CellSize);
        string IdAt(int vx) => splat.Palette[SplatMap.Base(splat.Get(vx, z))];
        Check(IdAt(x) == "sand" && IdAt(x2) == "rock", "painted by id");

        if (other is not null)
        {
            Terrain.SetTheme(other);
            Check(splat.Palette.Take(other.Materials.Count).SequenceEqual(other.MaterialIds()), $"palette starts with {other.Id}'s materials");
            Check(IdAt(x) == "sand" && IdAt(x2) == "rock", $"paint keeps its ids in {other.Id}");
            Check(other.IndexOf("sand") >= 0 || SplatMap.Base(splat.Get(x, z)) >= other.Materials.Count, "missing material is past the theme's");
            Terrain.SetTheme(def);
            Check(SplatMap.Base(splat.Get(x, z)) == def.IndexOf("sand") && IdAt(x2) == "rock", "back to default: indices restored");
        }
        else GD.Print("Demo themes: only one theme, switch not tested");

        string path = System.IO.Path.Combine(MapFiles.MapsDir, "_themetest.csmap");
        MapFile.Save(path, map, splat);
        var (_, loaded) = MapFile.Load(path);
        Check(loaded.ThemeId == def.Id && loaded.Palette.SequenceEqual(splat.Palette) &&
              loaded.Snapshot().AsSpan().SequenceEqual(splat.Snapshot()), "v3 file keeps theme, palette and paint");

        // A v2 file is a v3 file without the trailing theme section.
        var tail = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(tail, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(splat.ThemeId);
            w.Write(splat.Palette.Length);
            foreach (var id in splat.Palette) w.Write(id);
        }
        var bytes = System.IO.File.ReadAllBytes(path);
        Array.Resize(ref bytes, bytes.Length - (int)tail.Length);
        bytes[4] = 2;
        bytes[5] = 0;
        System.IO.File.WriteAllBytes(path, bytes);
        var (_, legacy) = MapFile.Load(path);
        Check(legacy.ThemeId == MapFile.LegacyThemeId && legacy.Palette.SequenceEqual(SplatMap.LegacyPalette), "v2 file loads with legacy ids");
        System.IO.File.Delete(path);

        Terrain.ReloadTheme();
        Check(Terrain.Theme is { } reloaded && reloaded.Id == def.Id && IdAt(x) == "sand", "reload from disk keeps paint");

        Terrain.SetTheme(ThemeLibrary.Get(start.Id)!);
        GD.Print(ok ? "Demo themes: ok" : "Demo themes: FAILED");
    }

    private void RunErosionDemo()
    {
        if (Terrain?.Map is not { } map || Tools is null) return;
        bool ok = true;
        float? sea = Terrain.SeaLevel;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lakesBefore = Lakes.Find(map, Terrain.LakeSettings, sea)!;
        GD.Print($"Demo erosion: lakes before: {lakesBefore.Count} lakes, {lakesBefore.WetVertices() * map.CellSize * map.CellSize / 1e6f:0.###} km², {sw.ElapsedMilliseconds} ms");

        HeightMap? medium = null;
        // Big maps: Medium only, and no repeat run (each run takes a while there).
        bool big = map.Width > 2049;
        foreach (var preset in big ? [ErosionPresets.Default] : ErosionPresets.All)
        {
            var copy = new HeightMap(map.Width, map.Depth, map.CellSize);
            map.Data.CopyTo(copy.Data);
            sw.Restart();
            ErosionSim.Run(copy, preset.Settings, sea);
            long ms = sw.ElapsedMilliseconds;
            double sumAbs = 0, net = 0;
            float cut = 0, fill = 0;
            for (int i = 0; i < copy.Data.Length; i++)
            {
                float d = copy.Data[i] - map.Data[i];
                sumAbs += MathF.Abs(d);
                net += d;
                cut = MathF.Min(cut, d);
                fill = MathF.Max(fill, d);
            }
            var (maxSlope, p99, flat) = SlopeStats(copy);
            double droplets = preset.Settings.Droplets * (double)(map.Width - 1) * (map.Depth - 1);
            GD.Print($"Demo erosion: {preset.Name,-6} {ms} ms ({droplets / Math.Max(ms, 1) / 1000:0.0} M droplets/s), mean change {sumAbs / copy.Data.Length:0.00} m, " +
                     $"deepest cut {-cut:0.0} m, highest fill {fill:0.0} m, net {net / copy.Data.Length * 1000:0.0} mm, slope max {maxSlope:0}° p99 {p99:0}°, under 5° {flat:0%}");
            if (!float.IsFinite(cut) || !float.IsFinite(fill) || sumAbs / copy.Data.Length < 0.01) { ok = false; GD.PushError($"Demo erosion: {preset.Name} did nothing or broke the map"); }
            if (preset == ErosionPresets.Default) medium = copy;
        }

        // Same seed, same result.
        if (!big)
        {
            var again = new HeightMap(map.Width, map.Depth, map.CellSize);
            map.Data.CopyTo(again.Data);
            ErosionSim.Run(again, ErosionPresets.Default.Settings, sea);
            bool repeatable = again.Data.SequenceEqual(medium!.Data);
            GD.Print($"Demo erosion: repeatable {(repeatable ? "ok" : "FAILED")}");
            ok &= repeatable;
        }

        sw.Restart();
        var lakes = Lakes.Find(medium!, Terrain.LakeSettings, sea)!;
        long lakeMs = sw.ElapsedMilliseconds;
        // Every wet vertex: level above its ground, and every dry neighbour stands at or above the level (else the water
        // would spill there).
        int bad = 0;
        for (int z = 1; z < map.Depth - 1; z++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                float level = lakes.LevelAt(x, z);
                if (float.IsNaN(level)) continue;
                if (level <= medium[x, z]) bad++;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (float.IsNaN(lakes.LevelAt(x + dx, z + dz)) && medium[x + dx, z + dz] < level - 1e-3f && !(sea is { } s && medium[x + dx, z + dz] < s))
                            bad++;
            }
        PrintLakeDepths(medium, lakes);
        GD.Print($"Demo erosion: lakes after Medium: {lakes.Count} lakes, {lakes.WetVertices() * map.CellSize * map.CellSize / 1e6f:0.###} km², {lakeMs} ms, " +
                 $"{bad} vertices breaking the spill rule");
        ok &= bad == 0;

        // One undo step, exact restore.
        var before = map.Snapshot();
        Tools.ApplyEroded(medium);
        var after = map.Snapshot();
        Tools.Undo();
        bool undoOk = map.Data.SequenceEqual(before);
        Tools.Redo();
        bool redoOk = map.Data.SequenceEqual(after);
        GD.Print($"Demo erosion: undo {(undoOk ? "ok" : "FAILED")}, redo {(redoOk ? "ok" : "FAILED")}");
        ok &= undoOk && redoOk;
        GD.Print(ok ? "Demo erosion: ok" : "Demo erosion: FAILED");
    }

    /// <summary>Share of lake area by water depth, and where the biggest lake is (for close-up screenshots).</summary>
    private static void PrintLakeDepths(HeightMap map, LakeMap lakes)
    {
        int wet = 0, under1 = 0, under2 = 0, under4 = 0;
        double sx = 0, sz = 0;
        float deepest = 0;
        for (int z = 0; z < map.Depth; z++)
            for (int x = 0; x < map.Width; x++)
            {
                float level = lakes.LevelAt(x, z);
                if (float.IsNaN(level)) continue;
                float d = level - map[x, z];
                wet++;
                if (d < 1) under1++;
                if (d < 2) under2++;
                if (d < 4) under4++;
                deepest = MathF.Max(deepest, d);
                sx += x;
                sz += z;
            }
        if (wet == 0) return;
        // A shore vertex near the map centre, for close-up screenshots.
        int bx = -1, bz = -1;
        float best = float.MaxValue;
        for (int z = 1; z < map.Depth - 1; z++)
            for (int x = 1; x < map.Width - 1; x++)
                if (!float.IsNaN(lakes.LevelAt(x, z)) && float.IsNaN(lakes.LevelAt(x + 1, z)))
                {
                    float d = MathF.Abs(x - map.Width / 2f) + MathF.Abs(z - map.Depth / 2f);
                    if (d < best) { best = d; bx = x; bz = z; }
                }
        GD.Print($"Demo erosion: shore point ({bx * map.CellSize:0}, {bz * map.CellSize:0})");
        GD.Print($"Demo erosion: lake depth: under 1 m {under1 / (float)wet:0%}, under 2 m {under2 / (float)wet:0%}, under 4 m {under4 / (float)wet:0%}, " +
                 $"deepest {deepest:0.0} m; wet centroid ({sx / wet * map.CellSize:0}, {sz / wet * map.CellSize:0})");
    }

    /// <summary>Over the vertices <paramref name="pick"/> selects: share under <paramref name="under"/>°, median and p90 slope.</summary>
    private static (float Under, float P50, float P90) SlopeWhere(HeightMap map, Func<int, bool> pick, float under)
    {
        var slopes = new System.Collections.Generic.List<float>();
        for (int z = 0; z < map.Depth; z++)
            for (int x = 0; x < map.Width; x++)
                if (pick(z * map.Width + x))
                    slopes.Add(MathF.Acos(Math.Clamp(map.GetNormal(x, z).Y, -1f, 1f)) * (180f / MathF.PI));
        if (slopes.Count == 0) return (0, 0, 0);
        slopes.Sort();
        return (slopes.Count(v => v < under) / (float)slopes.Count, slopes[slopes.Count / 2], slopes[slopes.Count * 9 / 10]);
    }

    /// <summary>Steepest vertex, 99th-percentile slope (degrees) and the share of vertices under 5° (buildable-ish).</summary>
    private static (float Max, float P99, float Flat) SlopeStats(HeightMap map)
    {
        var hist = new int[91];
        for (int z = 0; z < map.Depth; z++)
            for (int x = 0; x < map.Width; x++)
                hist[(int)(MathF.Acos(Math.Clamp(map.GetNormal(x, z).Y, -1f, 1f)) * (180f / MathF.PI))]++;
        int total = map.Width * map.Depth, top = 90, seen = 0, p99 = 0;
        while (top > 0 && hist[top] == 0) top--;
        for (int d = 0; d <= 90; d++) { seen += hist[d]; if (seen >= total * 0.99) { p99 = d; break; } }
        return (top + 1, p99 + 1, (hist[0] + hist[1] + hist[2] + hist[3] + hist[4]) / (float)total);
    }

    /// <summary>
    /// Saves the current map, loads it back and checks every height and splat weight survived, then puts the
    /// loaded copy on the terrain. Run after --demo-sculpt / --demo-paint to round-trip real edits.
    /// Prints "Demo mapfile: round trip ok".
    /// </summary>
    private void RunMapFileDemo()
    {
        if (Terrain?.Map is not { } map || Terrain.Splat is not { } splat) return;
        string path = System.IO.Path.Combine(MapFiles.MapsDir, "_selftest.csmap");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MapFile.Save(path, map, splat);
        long saveMs = sw.ElapsedMilliseconds;
        var (map2, splat2) = MapFile.Load(path);
        long loadMs = sw.ElapsedMilliseconds - saveMs;
        // Heights are stored as 16 bits over the map's range: each may move by half a step.
        var (min, max) = map.GetRange();
        float maxErr = 0f;
        for (int i = 0; i < map.Data.Length; i++)
            maxErr = MathF.Max(maxErr, MathF.Abs(map2.Data[i] - map.Data[i]));
        float step = (max - min) / 65535f;
        bool ok = map2.Width == map.Width && map2.Depth == map.Depth && map2.CellSize == map.CellSize &&
                  maxErr <= step * 0.5f + 1e-4f && splat2.Snapshot().AsSpan().SequenceEqual(splat.Snapshot());
        long kb = new System.IO.FileInfo(path).Length / 1024;
        Terrain.SetMap(map2, splat2);
        GD.Print($"Demo mapfile: round trip {(ok ? "ok" : "FAILED")} ({kb} KB, save {saveMs} ms, load {loadMs} ms, " +
                 $"max height error {maxErr * 1000:0.##} mm, painted tiles {splat.AllocatedTiles})");
    }

    /// <summary>
    /// Exports the map as 16-bit PNG and RAW, imports both back at the same size and range and checks every height is
    /// within half a 16-bit step. Also decodes a 16-bit brush stamp (if fetched) and compares it with Godot's 8-bit
    /// decode. Puts the PNG import on the terrain. Prints "Demo heightmap: all ok".
    /// </summary>
    private void RunHeightmapDemo()
    {
        if (Terrain?.Map is not { } map) return;
        bool ok = true;
        foreach (string ext in new[] { "png", "r16" })
        {
            string path = System.IO.Path.Combine(MapFiles.HeightmapsDir, "_selftest." + ext);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var image = HeightmapImage.FromHeightMap(map, out var range);
            image.Write(path, range);
            long writeMs = sw.ElapsedMilliseconds;
            var read = HeightmapImage.Read(path);
            var back = read.ToHeightMap(map.Width, map.CellSize, range.Min, range.Max);
            long readMs = sw.ElapsedMilliseconds - writeMs;
            float maxErr = 0f;
            for (int i = 0; i < map.Data.Length; i++)
                maxErr = MathF.Max(maxErr, MathF.Abs(back.Data[i] - map.Data[i]));
            float step = (range.Max - range.Min) / 65535f;
            bool rangeOk = ext == "r16" || read.Range == range;
            bool pass = back.Width == map.Width && maxErr <= step * 0.5f + 1e-3f && rangeOk;
            ok &= pass;
            GD.Print($"Demo heightmap: {ext} {(pass ? "ok" : "FAILED")} (max error {maxErr * 1000:0.##} mm, step {step * 1000:0.##} mm, " +
                     $"range kept {rangeOk}, {new System.IO.FileInfo(path).Length / 1024} KB, write {writeMs} ms, read+import {readMs} ms)");
            if (ext == "png") Terrain.SetMap(back);
        }

        // Cross-check the PNG decoder (all row filters) against libpng on a 16-bit file from another encoder.
        string stamp = ProjectSettings.GlobalizePath("res://assets/brushes/src/Stamp 001 - Hills.png");
        if (System.IO.File.Exists(stamp))
        {
            var ours = HeightmapImage.Read(stamp);
            var godot = Image.LoadFromFile(stamp);
            godot.Convert(Image.Format.L8);
            byte[] l8 = godot.GetData();
            int worst = 0;
            for (int i = 0; i < ours.Pixels.Length; i++)
                worst = Math.Max(worst, Math.Abs((ours.Pixels[i] >> 8) - l8[i]));
            bool pass = ours.Width == godot.GetWidth() && ours.BitDepth == 16 && worst <= 1;
            ok &= pass;
            GD.Print($"Demo heightmap: stamp decode {(pass ? "ok" : "FAILED")} ({ours.Width}x{ours.Height}, max 8-bit diff vs Godot {worst})");
        }
        GD.Print($"Demo heightmap: {(ok ? "all ok" : "FAILED")}");
    }

    public override void _Process(double delta)
    {
        if (_profileWait >= 0) Profile(delta);
        if (_pushFrame >= 0) PushDemo();
        if (_lakeStep >= 0) LakeWindowDemo(delta);
        _refresh -= delta;
        if (_refresh > 0) return;
        _refresh = 0.25;

        string text = $"FPS {Engine.GetFramesPerSecond():0}  ·  {MapSession.Mode}";
        if (Terrain?.Map is { } map)
            text += $"  ·  {map.SizeX / 1000f:0.#} km  ·  {System.IO.Path.GetFileName(MapSession.CurrentPath) ?? "unsaved"}";
        if (CityCamera is not null)
        {
            var p = CityCamera.Pivot;
            // In map metres, like --cam (the world origin is the map's centre).
            var pm = Terrain?.WorldToMap(p) ?? new Vector2(p.X, p.Z);
            text += $"\nPivot ({pm.X:0}, {p.Y:0.0}, {pm.Y:0})" +
                    $"\nYaw {CityCamera.YawDegrees:0}°  Pitch {CityCamera.PitchDegrees:0}°  Distance {CityCamera.Distance:0} m";
            if (Terrain?.Map is not null)
                text += $"\nSlope at pivot {Terrain.GetSlopeDegrees(p.X, p.Z):0.0}°";
        }
        if (Tools is not null && Terrain is not null)
        {
            text += $"\nTool {Tools.Tool}";
            if (Tools.Cursor is { } c)
                text += $"  ·  cursor height {c.Y:0.0} m, slope {Terrain.GetSlopeDegrees(c.X, c.Z):0.0}°";
            text += $"\nLast push {Terrain.LastPushRegions} regions in {Terrain.LastPushMs:0.0} ms";
            if (Terrain.Lakes is { } lakes)
                text += $"  ·  {lakes.Count} hollows (found in {Terrain.LastLakeMs:0} ms)";
            if (Terrain.Water is { } water)
            {
                var st = water.LastStats;
                text += $"\nWater {st.Volume / 1e6:0.###} Mm³, {st.WetCells * water.CellSize * water.CellSize / 1e6:0.##} km², " +
                        $"× {water.SimRatio:0.#}, {water.StepMs:0.00} ms/substep, {st.ActiveTiles} tiles";
                if (Tools.Cursor is { } wc && Terrain.GetWaterDepth(wc.X, wc.Z) is > 0.01f and var depth)
                {
                    var v = Terrain.GetWaterVelocity(wc.X, wc.Z);
                    text += $"  ·  cursor depth {depth:0.00} m, flow {v.Length():0.0} m/s";
                }
            }
        }
        text += "\n\nWASD move · Q/E rotate · R/F tilt · Z/X or wheel zoom" +
                "\nCtrl/Cmd+Z undo · Ctrl/Cmd+Shift+Z redo · Esc deselect tool / menu · G grid · C contours";
        _label.Text = text;
    }

    /// <summary>
    /// --profile: after the settle frames, records frame time and the viewport's measured CPU/GPU render time, then
    /// prints averages, the 95th percentile and the draw stats, and quits.
    /// </summary>
    private void Profile(double delta)
    {
        if (_profileWait > 0) { _profileWait--; return; }
        // Steady state: wait for the first lake/ground search too (it runs on workers for several seconds on big maps),
        // unless --profile-during-search.
        if (!_profileDuringSearch && _profile.Count == 0 && Terrain?.Map is not null && Terrain.Lakes is null)
        {
            _profileWait = 60; // then settle again after its ground upload
            return;
        }
        var rid = GetViewport().GetViewportRid();
        _profile.Add((delta * 1000, RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid)));
        if (_profile.Count < _profileFrames) return;

        static double Avg(System.Collections.Generic.IEnumerable<double> v) => System.Linq.Enumerable.Average(v);
        var frames = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.Select(_profile, p => p.Frame), f => f));
        double avg = Avg(frames), p95 = frames[(int)(frames.Count * 0.95)];
        double cpu = Avg(System.Linq.Enumerable.Select(_profile, p => p.Cpu)), gpu = Avg(System.Linq.Enumerable.Select(_profile, p => p.Gpu));
        var vp = GetViewport();
        long draws = (long)vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
        long prims = (long)vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame);
        long shadowDraws = (long)vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
        long shadowPrims = (long)vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
        GD.Print($"Profile: {_profile.Count} frames, frame {avg:0.00} ms avg ({1000 / avg:0} FPS), p95 {p95:0.00} ms; " +
                 $"render CPU {cpu:0.00} ms, GPU {gpu:0.00} ms (0 on Metal); " +
                 $"{draws} draws, {prims / 1000} k primitives; shadows {shadowDraws} draws, {shadowPrims / 1000} k primitives");
        if (Terrain?.Falls is { } falls)
            GD.Print($"Profile: waterfalls {Terrain.WaterfallFx}: {falls.Segments} curtain segments, {falls.Puffs} mist puffs, search {falls.ScanMs:0.0} ms");
        _profileWait = -1;
        GetTree().Quit();
    }

    /// <summary>
    /// --demo-push: raises a 35² patch a little every frame, as a brush does, first in the middle of a Terrain3D region
    /// (1 region per push) and then on a region corner (4), and prints what the pushes to Terrain3D cost per frame.
    /// </summary>
    private void PushDemo()
    {
        if (Terrain?.Map is not { } map || Terrain.RegionVertices == 0) return;
        // Measure with the first lake search done, unless --profile-during-search.
        if (_pushFrame == 0 && !_profileDuringSearch && Terrain.Lakes is null) return;
        const int settle = 60, frames = 180, half = 17;
        int f = _pushFrame++;
        // The push of the previous frame's edit has run by now (Terrain pushes after everything else).
        if (f > settle && f <= settle + frames) _pushMid.Add(Terrain.LastPushMs);
        else if (f > settle + frames + 1 && f <= settle + 2 * frames + 1) _pushCorner.Add(Terrain.LastPushMs);
        if (f == settle + 2 * frames + 2)
        {
            static string Stats(System.Collections.Generic.List<double> v)
            {
                v.Sort();
                return $"avg {System.Linq.Enumerable.Average(v):0.00} ms, p95 {v[(int)(v.Count * 0.95)]:0.00}, max {v[^1]:0.00}";
            }
            GD.Print($"Demo push: {map.Width}², {Terrain.RegionVertices}² regions; mid-region {Stats(_pushMid)}; corner {Stats(_pushCorner)}");
            _pushFrame = -1;
            GetTree().Quit();
            return;
        }
        int r = Terrain.RegionVertices;
        int cx = f <= settle + frames ? r * 3 / 2 : r * 2, cz = cx;
        for (int z = cz - half; z <= cz + half; z++)
        {
            var row = map.Row(z);
            for (int x = cx - half; x <= cx + half; x++) row[x] += 0.01f;
        }
        map.Invalidate(new VertexRect(cx - half, cz - half, cx + half, cz + half));
        Terrain.MarkDirty(cx - half, cz - half, cx + half, cz + half);
    }

    /// <summary>
    /// --demo-lake-window (M6 3c): after the first lake search, raises a 60 m patch by 3 m in one go. On a big map a window
    /// search must follow within 3 s (well under a second of work), then, with no more edits, a background full search
    /// after ~15 s. Prints ok/FAILED and quits.
    /// </summary>
    private void LakeWindowDemo(double delta)
    {
        if (Terrain?.Map is not { } map) return;
        _lakeTime += delta;
        switch (_lakeStep)
        {
            case 0:
                if (Terrain.Lakes is null) return;
                Terrain.LakesChanged += () => { _lakeSearches++; _lakeLastMs = Terrain.LastLakeMs; };
                int cx = map.Width / 3, cz = map.Depth / 3, half = (int)(30 / map.CellSize);
                for (int z = cz - half; z <= cz + half; z++)
                {
                    var row = map.Row(z);
                    for (int x = cx - half; x <= cx + half; x++) row[x] += 3f;
                }
                var rect = new VertexRect(cx - half, cz - half, cx + half, cz + half);
                map.Invalidate(rect);
                Terrain.MarkDirty(rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ);
                _lakeStep = 1;
                _lakeTime = 0;
                break;
            case 1:
                if (_lakeTime < 3) return;
                bool window = _lakeSearches == 1 && _lakeLastMs < 1000;
                GD.Print($"Demo lake window: {_lakeSearches} search(es) within 3 s of the edit, last {_lakeLastMs:0} ms {(window ? "ok" : "FAILED")}");
                foreach (string arg in OS.GetCmdlineUserArgs())
                    if (arg.StartsWith("--lake-shot="))
                        GetViewport().GetTexture().GetImage().SavePng(arg["--lake-shot=".Length..]);
                _lakeStep = window ? 2 : 3;
                _lakeTime = 0;
                break;
            case 2:
                if (_lakeSearches < 2 && _lakeTime < 60) return;
                bool full = _lakeSearches == 2 && _lakeLastMs > 1000;
                GD.Print($"Demo lake window: background full search {(full ? $"after {_lakeTime:0} s, {_lakeLastMs / 1000:0.0} s of work ok" : "FAILED")}");
                _lakeStep = full ? 4 : 3;
                break;
            default:
                bool ok = _lakeStep == 4;
                GD.Print(ok ? "Demo lake window: all ok" : "Demo lake window: FAILED");
                _lakeStep = -1;
                GetTree().Quit(ok ? 0 : 1);
                break;
        }
    }
}
