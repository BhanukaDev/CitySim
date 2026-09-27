using System;
using System.Diagnostics;
using System.IO;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;
using CitySim.TerrainSystem.Sculpt;
using CitySim.UI;

namespace CitySim.Debug;

/// <summary>
/// <c>--demo-scale[=cells]</c>: times the data side of a build-area map (default 8192 cells, 28.7 km) without rendering it:
/// generate, height range, sculpt and paint strokes with undo/redo, save and load, and memory. Prints "Demo scale: ok".
/// M6 targets on an 8 GB M1: generate &lt; 3 s, load &lt; 2 s, stroke &lt; 4 ms/frame.
/// </summary>
public static class ScaleDemo
{
    public static bool Run(int cells)
    {
        bool ok = true;
        var proc = Process.GetCurrentProcess();
        void Mem(string when)
        {
            proc.Refresh();
            GD.Print($"Demo scale: memory {when}: working set {proc.WorkingSet64 >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB");
        }
        Mem("at start");

        foreach (var preset in GenPresets.All)
        {
            var ps = preset.ApplyTo(new GenSettings { Cells = cells });
            var t = Stopwatch.StartNew();
            var m = new HeightMap(cells + 1, cells + 1, ps.CellSize);
            TerrainGen.Fill(m, ps with { SmoothPasses = 0 });
            double fill = t.Elapsed.TotalMilliseconds;
            t.Restart();
            m.Smooth(ps.SmoothPasses);
            GD.Print($"Demo scale: {preset.Name,-14} fill {fill:0} ms, smooth {t.Elapsed.TotalMilliseconds:0} ms");
        }
        GC.Collect();

        var settings = GenPresets.Find("mountains")!.ApplyTo(new GenSettings { Cells = cells });
        var sw = Stopwatch.StartNew();
        int reports = 0;
        var map = TerrainGen.Create(settings, default, _ => reports++);
        double genMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var (min, max) = map.GetRange();
        double rangeMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"Demo scale: {map.Width}² ({map.SizeX / 1000f:0.#} km) generated in {genMs:0} ms ({reports} progress reports), " +
                 $"first range {rangeMs:0.0} ms: {min:0}–{max:0} m");
        Mem("after generate");

        var splat = new SplatMap(map.Width, map.Depth, map.CellSize);
        var history = new UndoStack();
        var c = new System.Numerics.Vector2(map.SizeX * 0.5f, map.SizeZ * 0.5f);
        var brush = new Brush(80f, 0.6f, null, 0f);
        var region = map.CircleRect(c.X, c.Y, brush.Radius + 40f);
        var heightsBefore = map.CopyRegion(region);

        // Sculpt: 120 ticks (2 s) of raising while moving 40 m, each tick touching undo first, as the tool controller does.
        const int ticks = 120;
        double worst = 0;
        sw.Restart();
        history.BeginStroke(map);
        for (int i = 0; i < ticks; i++)
        {
            var t0 = sw.Elapsed.TotalMilliseconds;
            var p = c + new System.Numerics.Vector2(40f * i / ticks, 0f);
            history.Touch(map.CircleRect(p.X, p.Y, brush.Radius));
            SculptOps.Shift(map, p, brush, 1f, 1f / 60f);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
        }
        history.EndStroke();
        double strokeMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        map.GetRange();
        double rangeAfterMs = sw.Elapsed.TotalMilliseconds;
        var heightsAfter = map.CopyRegion(region);

        // Paint: 60 ticks.
        var splatBefore = splat.CopyRegion(region);
        sw.Restart();
        history.BeginStroke(null, splat);
        for (int i = 0; i < 60; i++)
        {
            history.Touch(splat.CircleRect(c.X, c.Y, brush.Radius));
            PaintOps.Paint(splat, c, brush, 3, 1f / 60f); // any material index
        }
        history.EndStroke();
        double paintMs = sw.Elapsed.TotalMilliseconds;
        var splatAfter = splat.CopyRegion(region);
        GD.Print($"Demo scale: sculpt {ticks} ticks in {strokeMs:0.0} ms (worst tick {worst:0.00} ms), range after stroke {rangeAfterMs:0.00} ms, " +
                 $"paint 60 ticks in {paintMs:0.0} ms ({splat.AllocatedTiles} paint tiles), undo holds {history.Bytes >> 10} KB");

        history.Undo(map, splat);
        bool paintUndo = splat.CopyRegion(region).AsSpan().SequenceEqual(splatBefore);
        history.Undo(map, splat);
        bool sculptUndo = map.CopyRegion(region).AsSpan().SequenceEqual(heightsBefore);
        history.Redo(map, splat);
        history.Redo(map, splat);
        bool redo = map.CopyRegion(region).AsSpan().SequenceEqual(heightsAfter) && splat.CopyRegion(region).AsSpan().SequenceEqual(splatAfter);
        GD.Print($"Demo scale: undo sculpt {Ok(sculptUndo)}, undo paint {Ok(paintUndo)}, redo both {Ok(redo)}");
        ok &= sculptUndo && paintUndo && redo;

        string path = Path.Combine(MapFiles.MapsDir, "_scale.csmap");
        sw.Restart();
        MapFile.Save(path, map, splat);
        double saveMs = sw.Elapsed.TotalMilliseconds;
        long bytes = new FileInfo(path).Length;
        Mem("after save");
        sw.Restart();
        var (map2, splat2) = MapFile.Load(path);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        File.Delete(path);
        (min, max) = map.GetRange();
        float step = (max - min) / 65535f, maxErr = 0f;
        for (int z = 0; z < map.Depth; z++)
        {
            var a = map.Row(z);
            var b = map2.Row(z);
            for (int x = 0; x < a.Length; x++) maxErr = MathF.Max(maxErr, MathF.Abs(a[x] - b[x]));
        }
        // Half a 16-bit step, plus float rounding at the map's largest heights.
        float tolerance = step * 0.5f + MathF.Max(MathF.Abs(min), MathF.Abs(max)) * 1e-6f;
        bool fileOk = maxErr <= tolerance && splat2.CopyRegion(region).AsSpan().SequenceEqual(splatAfter) &&
                      splat2.AllocatedTiles == splat.AllocatedTiles;
        GD.Print($"Demo scale: save {saveMs:0} ms, load {loadMs:0} ms, {bytes >> 20} MB, max height error {maxErr * 1000:0.##} mm, round trip {Ok(fileOk)}");
        ok &= fileOk;
        Mem("after load (two maps alive)");

        bool fast = genMs < 3000 && loadMs < 2000 && worst < 4;
        GD.Print($"Demo scale: targets (generate < 3 s, load < 2 s, tick < 4 ms) {(fast ? "met" : "MISSED")}");
        GD.Print(ok ? "Demo scale: ok" : "Demo scale: FAILED");
        return ok;
    }

    private static string Ok(bool b) => b ? "ok" : "FAILED";
}
