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
/// On-screen stats and controls help. Also supports automated screenshots:
///   Godot --path . -- --screenshot=out.png [--screenshot-frames=60]
/// saves the viewport after N frames and quits. Also: --cam=x,z,distance,pitch,yaw, --demo-sculpt, --demo-paint, --demo-camera, --demo-mapfile,
/// --demo-heightmap, --demo-generate, --demo-erosion, --erode[=preset], --show-erosion,
/// --theme=id (switch the map's theme), --show-theme, --demo-water (water self-checks, then sources on the map),
/// --show-water (Water panel), --hide-water (don't draw it), --water-arrows (flow arrows on), --no-tool (--demo-water ends without a water tool out),
/// --preview-at=x,z,level (the Lake placement preview there), --water-speed=n, --water-run=seconds (simulate that long right away),
/// --pollute=kg/s (the --demo-water stream carries pollutant), --view=materials|cost|slot:&lt;n&gt; (debug views), --demo-themes,
/// --demo-scale[=cells], --flat[=height], --preset=name, --seed=n, --show-generator, --load=path,
/// --heightmap=path[,min,max], --game (handled by MainMenu),
/// --bake-theme=id|all (bake a theme's textures, previews and include, then quit; see ThemeBaker; run --import after) and
/// --bake-brushes (bake brush masks for import, then quit; see TextureBaker).
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    private float _demoPollution;
    private bool _demoNoTool;
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
            else if (arg == "--show-erosion")
                Callable.From(() =>
                {
                    foreach (var node in GetParent().GetChildren())
                        if (node is GameUi ui) ui.OpenErosion();
                }).CallDeferred();
            else if (arg == "--demo-water")
                Callable.From(RunWaterDemo).CallDeferred();
            else if (arg == "--hide-water" && Terrain is not null)
                Terrain.ShowWater = false;
            else if (arg.StartsWith("--preview-at="))
            {
                var v = arg["--preview-at=".Length..].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                GetTree().CreateTimer(3.0).Timeout += () =>
                    Terrain?.PreviewSource(new WaterSource(0, WaterSourceKind.Lake, v[0], v[1], 40f, v[2], MaxFlow: 100f));
            }
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
            else if (arg.StartsWith("--pollute=") && float.TryParse(arg["--pollute=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out float pollute))
                _demoPollution = pollute;
            else if (arg.StartsWith("--water-run=") && double.TryParse(arg["--water-run=".Length..],
                         System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                // After the hollow search has put lake sources in (a new map), so they're part of the run.
                GetTree().CreateTimer(2.0).Timeout += () => Terrain?.Water?.RunFor(seconds);
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
        Tools.Water.Press(true, new Vector3(20f, Terrain.GetHeight(20f, pz), pz));
        bool placed = water.Sources.Count == count + 1 && water.Sources[^1] is { Kind: WaterSourceKind.River, X: 0f };
        Tools.Undo();
        bool undone = water.Sources.Count == count;
        Tools.Redo();
        bool redone = water.Sources.Count == count + 1;
        Tools.Water.Press(false, new Vector3(5f, Terrain.GetHeight(5f, pz), pz));
        bool removed = water.Sources.Count == count;
        bool toolOk = placed && undone && redone && removed;
        GD.Print($"Demo water: tool snaps to the border {placed}, undo {undone}, redo {redone}, right-click removes {removed}: " +
                 (toolOk ? "ok" : "FAILED"));

        float cs = map.CellSize;
        water.SetSources([
            new WaterSource(1, WaterSourceKind.Stream, high.X * cs, high.Z * cs, 20f, 0f, FlowRate: 40f, Pollution: _demoPollution),
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
}
