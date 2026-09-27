using Godot;
using System;
using CitySim.App;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;
using System.Linq;
using CitySim.Tools;
using CitySim.UI;

namespace CitySim.Debug;

/// <summary>
/// On-screen stats and controls help. Also supports automated screenshots:
///   Godot --path . -- --screenshot=out.png [--screenshot-frames=60]
/// saves the viewport after N frames and quits. Also: --cam=x,z,distance,pitch,yaw, --demo-sculpt, --demo-paint, --demo-camera, --demo-mapfile,
/// --demo-heightmap, --demo-generate, --demo-scale[=cells], --flat[=height], --preset=name, --seed=n, --show-generator, --load=path,
/// --heightmap=path[,min,max], --game (handled by MainMenu),
/// --bake-terrain-textures and --bake-brushes (bake textures / brush masks for import, then quit; see TextureBaker).
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public Terrain? Terrain { get; set; }
    [Export] public TerrainToolController? Tools { get; set; }

    private Label _label = null!;
    private double _refresh;
    private string? _screenshotPath;
    private int _screenshotFrames = 60;

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(12, 10) };
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 4);
        AddChild(_label);

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot="))
                _screenshotPath = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--screenshot-frames=") && int.TryParse(arg["--screenshot-frames=".Length..], out int f))
                _screenshotFrames = f;
            else if (arg.StartsWith("--cam=") && CityCamera is not null)
            {
                // --cam=x,z,distance,pitch,yaw (world metres / degrees)
                var v = System.Array.ConvertAll(arg["--cam=".Length..].Split(','),
                    s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                if (v.Length == 5)
                    Callable.From(() => CityCamera.JumpTo(new Vector2(v[0], v[1]), v[2], v[3], v[4])).CallDeferred();
            }
            else if (arg == "--demo-sculpt" && Tools is not null)
                Callable.From(Tools.RunDemo).CallDeferred();
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
            else if (arg == "--bake-terrain-textures")
            {
                bool ok = TextureBaker.Bake();
                GetTree().Quit(ok ? 0 : 1);
            }
        }
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

        // Lakes: channels and basins each lower part of the lowlands.
        var hills = GenPresets.Default.ApplyTo(baseSettings);
        var dry = TerrainGen.Create(hills with { Noise = hills.Noise with { ChannelDepth = 0f, BasinAmount = 0f } });
        float Lowered(GenSettings s)
        {
            var m = TerrainGen.Create(s);
            int n = 0;
            for (int i = 0; i < m.Data.Length; i++) if (dry.Data[i] - m.Data[i] > 1f) n++;
            return n / (float)m.Data.Length;
        }
        float channels = Lowered(hills with { Noise = hills.Noise with { BasinAmount = 0f } });
        var basinMap = TerrainGen.Create(hills with { Noise = hills.Noise with { ChannelDepth = 0f } });
        float basins = Lowered(hills with { Noise = hills.Noise with { ChannelDepth = 0f } });
        GD.Print($"Demo generate: {hills.Preset} lowered > 1 m by channels {channels:0.0%}, by basins {basins:0.0%}");
        // Basin floors should be level, banks a mix of gentle and steep.
        var (floorFlat, _, _) = SlopeWhere(basinMap, i => dry.Data[i] - basinMap.Data[i] > 1f, 1f);
        var (bankFlat, bankP50, bankP90) = SlopeWhere(basinMap, i => dry.Data[i] - basinMap.Data[i] is > 0.2f and < 3f, 5f);
        GD.Print($"Demo generate: basins: dug ground (level floors + banks) under 1° {floorFlat:0%}; banks under 5° {bankFlat:0%}, p50 {bankP50:0}°, p90 {bankP90:0}°");
        // Sea shores: ground within 3 m of sea level.
        var coast = GenPresets.Find("coast")!.ApplyTo(baseSettings);
        var coastMap = TerrainGen.Create(coast);
        var (shoreFlat, shoreP50, shoreP90) = SlopeWhere(coastMap, i => MathF.Abs(coastMap.Data[i] - coast.SeaLevel) < 3f, 5f);
        GD.Print($"Demo generate: coast: shore under 5° {shoreFlat:0%}, p50 {shoreP50:0}°, p90 {shoreP90:0}°");
        if (channels < 0.005f || basins < 0.005f) { ok = false; GD.PushError("Demo generate: channels or basins missing"); }

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
        GD.Print(ok ? "Demo generate: ok" : "Demo generate: FAILED");
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
        if (_screenshotPath is not null && --_screenshotFrames <= 0)
        {
            var err = GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"Screenshot saved to {_screenshotPath} ({err})");
            _screenshotPath = null;
            GetTree().Quit();
            return;
        }

        _refresh -= delta;
        if (_refresh > 0) return;
        _refresh = 0.25;

        string text = $"FPS {Engine.GetFramesPerSecond():0}  ·  {MapSession.Mode}";
        if (Terrain?.Map is { } map)
            text += $"  ·  {map.SizeX / 1000f:0.#} km  ·  {System.IO.Path.GetFileName(MapSession.CurrentPath) ?? "unsaved"}";
        if (CityCamera is not null)
        {
            var p = CityCamera.Pivot;
            text += $"\nPivot ({p.X:0}, {p.Y:0.0}, {p.Z:0})" +
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
        }
        text += "\n\nWASD move · Q/E rotate · R/F tilt · Z/X or wheel zoom" +
                "\nCtrl/Cmd+Z undo · Ctrl/Cmd+Shift+Z redo · Esc deselect tool / menu · G grid · C contours";
        _label.Text = text;
    }
}
