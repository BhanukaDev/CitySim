using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        var lakes = RunLakes(map, Mem);

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
        ok &= CheckLakeWindows(map, lakes, map.CircleRect(c.X, c.Y, brush.Radius + 40f));
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
        map2 = null!;
        splat2 = null!;
        GC.Collect();

        ok &= RunWater(map, min, max, Mem);

        bool fast = genMs < 3000 && loadMs < 2000 && worst < 4;
        GD.Print($"Demo scale: targets (generate < 3 s, load < 2 s, tick < 4 ms) {(fast ? "met" : "MISSED")}");
        GD.Print(ok ? "Demo scale: ok" : "Demo scale: FAILED");
        return ok;
    }

    /// <summary>
    /// Water on the big map (M6 phase 3f: 7 m cells, sparse tiles): a mountain stream, a lake and a sea along the low
    /// border, 10 sim-minutes. Reports the grid, the tiles in memory and their MB, the substep cost, and a save/load of
    /// the water grids.
    /// </summary>
    private static bool RunWater(HeightMap map, float min, float max, Action<string> mem)
    {
        CitySim.WaterSystem.WaterNative.Directory ??= ProjectSettings.GlobalizePath("res://native/water/bin");
        var sw = Stopwatch.StartNew();
        using var sim = new CitySim.WaterSystem.WaterSim(map, threaded: false);
        double createMs = sw.Elapsed.TotalMilliseconds;
        float cx = map.SizeX * 0.5f, cz = map.SizeZ * 0.5f, sea = min + (max - min) * 0.05f;
        sim.SetSources([
            new CitySim.WaterSystem.WaterSource(1, CitySim.WaterSystem.WaterSourceKind.Stream, cx, cz, 20, 0, FlowRate: 20),
            new CitySim.WaterSystem.WaterSource(2, CitySim.WaterSystem.WaterSourceKind.Lake, cx - 3000, cz + 2000, 150,
                map.SampleHeight(cx - 3000, cz + 2000) + 8f, MaxFlow: 200),
            new CitySim.WaterSystem.WaterSource(3, CitySim.WaterSystem.WaterSourceKind.Sea, 0, 0, 0, sea),
        ]);
        sw.Restart();
        sim.FillHollows(null);
        sim.Advance(1);
        double fillMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        sim.Advance(600);
        double runMs = sw.Elapsed.TotalMilliseconds;
        var st = sim.LastStats;
        GD.Print($"Demo scale: water {sim.Width}² cells of {sim.CellSize:0.#} m (marks {sim.MarksWidth}² of {sim.MarksCellSize:0.#} m), " +
                 $"created in {createMs:0} ms, sea fill {fillMs:0} ms; 10 sim-min in {runMs / 1000:0.0} s: " +
                 $"{st.AllocatedTiles} of {sim.TilesX * sim.TilesZ} tiles in memory ({st.AllocatedMb:0} MB), {st.ActiveTiles} active, " +
                 $"{st.SleepingTiles} sleeping, {sim.StepMs:0.00} ms per substep, {st.Substeps} substeps per tick, " +
                 $"{st.WetCells * sim.CellSize * sim.CellSize / 1e6:0.#} km² wet");
        mem("with water");
        sw.Restart();
        var depth = sim.ReadDepth();
        var paint = sim.ReadPaint();
        double readMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        sim.LoadWater(depth, null, paint);
        sim.Advance(1);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        bool kept = Math.Abs(sim.LastStats.Volume - st.Volume) <= st.Volume * 0.01 + 1;
        GD.Print($"Demo scale: water grids read {readMs:0} ms, loaded {loadMs:0} ms, volume {st.Volume / 1e6:0.###} → " +
                 $"{sim.LastStats.Volume / 1e6:0.###} million m³ {Ok(kept && st.AllocatedTiles < sim.TilesX * sim.TilesZ)}");
        return kept && st.AllocatedTiles < sim.TilesX * sim.TilesZ;
    }

    /// <summary>The full lake + ground mask search (what ran after every edit before M6 3c), by stage.</summary>
    private static CitySim.TerrainSystem.Erosion.LakeMap RunLakes(HeightMap map, Action<string> mem)
    {
        CitySim.TerrainSystem.Erosion.Native.Directory ??= ProjectSettings.GlobalizePath("res://native/erosion/bin");
        var sw = Stopwatch.StartNew();
        var lakes = CitySim.TerrainSystem.Erosion.Lakes.Find(map, new CitySim.TerrainSystem.Erosion.LakeSettings(), null, ground: true);
        double ms = sw.Elapsed.TotalMilliseconds;
        var st = CitySim.TerrainSystem.Erosion.Lakes.LastStageMs();
        ulong hash = 14695981039346656037ul;
        foreach (uint v in lakes!.Ground!) hash = (hash ^ v) * 1099511628211ul;
        foreach (uint v in lakes.Flow!) hash = (hash ^ v) * 1099511628211ul;
        GD.Print($"Demo scale: lakes + ground masks {ms:0} ms ({lakes.Count} lakes): flood {st[0]:0}, lakes {st[1]:0}, " +
                 $"flow {st[2]:0}, masks {st[3]:0} ms; masks + flow hash {hash:x16}");
        mem("after lake search");
        return lakes;
    }

    /// <summary>
    /// Window search (M6 3c) vs a full search after edits: the demo stroke (<paramref name="stroke"/>, already applied),
    /// no edit on the biggest river near the centre (the window alone must match), then a 3 m bump across that river.
    /// Compares the part a window search keeps: ground mask bytes within ±2, lake levels, catchment areas.
    /// </summary>
    private static bool CheckLakeWindows(HeightMap map, CitySim.TerrainSystem.Erosion.LakeMap last, VertexRect stroke)
    {
        var settings = new CitySim.TerrainSystem.Erosion.LakeSettings();
        bool ok = true;

        // The river: the vertex with the biggest catchment within 1 km of the centre.
        int cx = map.Width / 2, cz = map.Depth / 2, best = -1, reach = (int)(1000 / map.CellSize);
        uint bestArea = 0;
        for (int z = cz - reach; z <= cz + reach; z++)
            for (int x = cx - reach; x <= cx + reach; x++)
            {
                uint a = last.Flow![z * map.Width + x] & 0xFFFF;
                if (a > bestArea) { bestArea = a; best = z * map.Width + x; }
            }
        int rx = best % map.Width, rz = best / map.Width, r = (int)(30 / map.CellSize);
        var bump = new VertexRect(rx - r, rz - r, rx + r, rz + r);

        foreach (var (name, edit, apply) in new (string, VertexRect, Action?)[]
                 {
                     ("stroke", stroke, null),
                     ("no edit, on the river", bump, () => { }),
                     ($"3 m bump on a river ({MathF.Pow(2, bestArea / 2048f) / 1e6f:0.#} km² catchment)", bump, () =>
                     {
                         for (int z = bump.MinZ; z <= bump.MaxZ; z++)
                             for (int x = bump.MinX; x <= bump.MaxX; x++)
                             {
                                 float d = MathF.Sqrt((x - rx) * (x - rx) + (z - rz) * (z - rz)) / r;
                                 if (d < 1) map[x, z] += 3f * (1 - d * d);
                             }
                         map.Invalidate(bump);
                     }),
                 })
        {
            apply?.Invoke();
            var window = edit.Expand(TerrainSystem.Terrain.WindowMargin, map.Width, map.Depth);
            var inner = edit.Expand(TerrainSystem.Terrain.WindowKeep, map.Width, map.Depth);
            var sw = Stopwatch.StartNew();
            var win = CitySim.TerrainSystem.Erosion.Lakes.FindWindow(map, last, window, settings, null)!;
            double winMs = sw.Elapsed.TotalMilliseconds;
            var full = CitySim.TerrainSystem.Erosion.Lakes.Find(map, settings, null, ground: true)!;

            var within = new long[4];
            var worst = new int[4];
            long cells = 0, levelSame = 0, areaSame = 0;
            for (int z = inner.MinZ; z <= inner.MaxZ; z++)
                for (int x = inner.MinX; x <= inner.MaxX; x++)
                {
                    int g = z * map.Width + x, l = (z - window.MinZ) * window.Width + (x - window.MinX);
                    uint a = win.Ground[l], b = full.Ground![g];
                    for (int k = 0; k < 4; k++)
                    {
                        int d = Math.Abs((int)((a >> (8 * k)) & 0xFF) - (int)((b >> (8 * k)) & 0xFF));
                        if (d <= 2) within[k]++;
                        worst[k] = Math.Max(worst[k], d);
                    }
                    float la = win.Level[l], lb = full.Level[g];
                    if ((float.IsNaN(la) && float.IsNaN(lb)) || MathF.Abs(la - lb) < 0.01f) levelSame++;
                    // Catchment within 1 % (log2 × 2048: 1 % ≈ 29 steps).
                    if (Math.Abs((int)(win.Flow[l] & 0xFFFF) - (int)(full.Flow![g] & 0xFFFF)) <= 29) areaSame++;
                    cells++;
                }
            string[] names = ["shore", "gully", "wear", "deposit"];
            var bytes = string.Join(", ", Enumerable.Range(0, 4).Select(k => $"{names[k]} {100.0 * within[k] / cells:0.00} % (worst {worst[k]})"));
            bool pass = Enumerable.Range(0, 4).All(k => within[k] >= cells * 0.99) && levelSame >= cells * 0.99;
            GD.Print($"Demo scale: window search, {name}: {window.Width}×{window.Depth} in {winMs:0} ms; inner {inner.Width}×{inner.Depth} " +
                     $"within ±2 of a full search: {bytes}; lake levels {100.0 * levelSame / cells:0.00} %, catchment ±1 % " +
                     $"{100.0 * areaSame / cells:0.00} % {Ok(pass)}");
            ok &= pass;
            last = full;
        }
        return ok;
    }

    private static string Ok(bool b) => b ? "ok" : "FAILED";
}
