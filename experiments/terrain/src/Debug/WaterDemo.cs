using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Erosion;
using CitySim.WaterSystem;

namespace CitySim.Debug;

/// <summary>
/// Self-checks for the water simulation (<c>--demo-water</c>), on small made-up terrains with unthreaded sims so every
/// run is the same: volume is conserved, streams run downhill, level sources hold their level, unfed water evaporates,
/// Fill Hollows matches the hollow search, a dam built across a stream backs the water up, the map file keeps the water,
/// and a step's cost at 1025² and 2049². Prints one line per check and "Demo water: all ok". Engine-agnostic.
/// </summary>
public static class WaterDemo
{
    public static bool Run(Action<string> log)
    {
        bool ok = true;
        void Check(bool pass, string what)
        {
            log($"Demo water: {what} {(pass ? "ok" : "FAILED")}");
            ok &= pass;
        }

        // 1. Volume: a lump of water sloshing in a walled bowl keeps its volume and settles flat.
        {
            var map = Make(129, 4f, (x, z) => 0.002f * ((x - 256) * (x - 256) + (z - 256) * (z - 256)) / 4f);
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { OpenEdges = false, EvaporationMmPerMin = 0 } };
            var depth = new float[sim.Width * sim.Depth];
            for (int z = 40; z < 90; z++)
                for (int x = 20; x < 60; x++) depth[z * sim.Width + x] = 3f;
            sim.LoadDepth(depth);
            double before = Volume(sim);
            sim.Advance(900);
            double after = Volume(sim);
            var surfaces = Wet(sim).Select(i => map.Data[i] + sim.ReadDepth()[i]).ToArray();
            float spread = surfaces.Length == 0 ? 99f : surfaces.Max() - surfaces.Min();
            Check(Math.Abs(after - before) / before < 1e-3, $"volume kept ({before:0} → {after:0} m³)");
            Check(spread < 0.1f, $"settles flat (surface spread {spread:0.000} m)");
        }

        // 2. A stream on a slope runs downhill (toward z = 0) and off the open edge.
        {
            var map = Make(129, 4f, (x, z) => z * 0.05f + 2f * MathF.Abs(x - 256) / 256f);
            using var sim = new WaterSim(map, threaded: false);
            sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 256, 400, 10, 0, FlowRate: 5)]);
            sim.Advance(600);
            var wet = Wet(sim);
            double meanZ = wet.Length == 0 ? 999 : wet.Average(i => (double)(i / sim.Width) * sim.CellSize);
            bool reachedEdge = wet.Any(i => i / sim.Width < 3);
            Check(wet.Length > 50 && meanZ < 400 && reachedEdge, $"stream runs downhill ({wet.Length} wet cells, mean z {meanZ:0} m, reaches the edge {reachedEdge})");
        }

        // 3. A river (level) source fills a pit to its level.
        {
            var map = Make(129, 4f, (x, z) => Pit(x, z, 256, 256, 60, 20f, 10f));
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { OpenEdges = false, EvaporationMmPerMin = 0 } };
            sim.SetSources([new WaterSource(1, WaterSourceKind.River, 256, 256, 20, 15f)]);
            sim.Advance(600);
            float surface = map.SampleHeight(296, 256) + sim.DepthAt(296, 256);
            Check(MathF.Abs(surface - 15f) < 0.05f, $"level source holds its level (15 m → {surface:0.000} m, 40 m from it)");
        }

        // 4. Water nothing feeds evaporates.
        {
            var map = Make(65, 4f, (x, z) => Pit(x, z, 128, 128, 40, 10f, 1f));
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { EvaporationMmPerMin = 10 } };
            sim.FillHollows(Levels(map, 10f, 9.99f));
            double before = Volume(sim);
            // 10 mm/min takes the 1 m deep middle in 100 minutes.
            sim.Advance(6300);
            double after = Volume(sim);
            Check(before > 100 && after < before * 0.01, $"unfed pond evaporates ({before:0} → {after:0.#} m³)");
        }

        // 5. Fill Hollows matches the hollow search (Priority-Flood) volume.
        {
            var map = Make(129, 4f, (x, z) => Pit(x, z, 180, 200, 50, 20f, 6f) + Pit(x, z, 360, 330, 70, 0f, 4f) + 0.01f * x);
            var lakes = Lakes.Find(map, new LakeSettings { MinArea = 0 }, null)!;
            double expected = 0;
            for (int i = 0; i < lakes.Level.Length; i++)
                if (!float.IsNaN(lakes.Level[i])) expected += (lakes.Level[i] - map.Data[i]) * map.CellSize * map.CellSize;
            using var sim = new WaterSim(map, threaded: false);
            sim.FillHollows(lakes.Level);
            double got = Volume(sim);
            Check(expected > 0 && Math.Abs(got - expected) / expected < 0.01, $"fill hollows ({lakes.Count} lakes, {expected:0} m³ expected, {got:0} m³ filled)");
        }

        // 6. A dam raised across a stream's valley (a terrain edit) backs the water up behind it.
        {
            var map = Make(129, 4f, (x, z) => z * 0.03f + 3f * MathF.Abs(x - 256) / 64f);
            using var sim = new WaterSim(map, threaded: false);
            sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 256, 450, 10, 0, FlowRate: 20)]);
            sim.Advance(300);
            float before = sim.DepthAt(256, 260);
            for (int z = 60; z <= 62; z++)
                for (int x = 0; x < map.Width; x++) map[x, z] += 12f;
            sim.GroundChanged(new VertexRect(0, 60, map.Width - 1, 62));
            sim.Advance(900);
            float after = sim.DepthAt(256, 260);
            Check(after > before + 2f, $"dam backs water up (depth behind it {before:0.00} → {after:0.00} m)");
        }

        // 7. Map file v4 keeps sources, settings and depths.
        {
            var map = Make(129, 4f, (x, z) => Pit(x, z, 256, 256, 60, 20f, 10f));
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { Speed = 4, EvaporationMmPerMin = 1.5f, OpenEdges = false } };
            sim.SetSources([
                new WaterSource(3, WaterSourceKind.River, 0, 256, 60, 25f),
                new WaterSource(7, WaterSourceKind.Lake, 256, 256, 40, 14f, MaxFlow: 50),
                new WaterSource(9, WaterSourceKind.Stream, 100, 100, 12, 0, FlowRate: 7.5f),
            ]);
            sim.FillHollows(Levels(map, 20f, 16f));
            var data = new WaterData(sim.Settings, sim.Sources, sim.Width, sim.Depth, sim.ReadDepth());
            string path = Path.Combine(Path.GetTempPath(), "citysim_water_demo.csmap");
            MapFile.Save(path, map, new SplatMap(map.Width, map.Depth, map.CellSize) { ThemeId = "default" }, data);
            var (_, _, loaded) = MapFile.LoadWithWater(path);
            File.Delete(path);
            float worst = 0;
            if (loaded?.DepthGrid is { } g)
                for (int i = 0; i < g.Length; i++) worst = MathF.Max(worst, MathF.Abs(g[i] - data.DepthGrid![i]));
            bool same = loaded is not null && loaded.Settings == data.Settings && loaded.Sources.SequenceEqual(data.Sources) && loaded.DepthGrid is not null;
            Check(same && worst < 0.01f, $"map file round trip (worst depth error {worst:0.0000} m)");
        }

        // 8. Cost of a substep with every cell wet (2 m of water on a gentle slope, walls), 1025² and 2049².
        foreach (int verts in new[] { 1025, 2049 })
        {
            var map = Make(verts, 3.5f, (x, z) => 0.001f * x);
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { OpenEdges = false } };
            var depth = new float[sim.Width * sim.Depth];
            Array.Fill(depth, 2f);
            sim.LoadDepth(depth);
            sim.Advance(1);
            var sw = Stopwatch.StartNew();
            sim.Advance(20);
            var st = sim.LastStats;
            log($"Demo water: {verts}² fully wet: {sim.StepMs:0.00} ms per substep ({st.Substeps} substeps for the last 5 s, {sw.ElapsedMilliseconds} ms for 20 s)");
        }

        log(ok ? "Demo water: all ok" : "Demo water: FAILED");
        return ok;
    }

    private static HeightMap Make(int verts, float cell, Func<float, float, float> height)
    {
        var map = new HeightMap(verts, verts, cell);
        for (int z = 0; z < verts; z++)
            for (int x = 0; x < verts; x++) map[x, z] = height(x * cell, z * cell);
        map.Invalidate();
        return map;
    }

    /// <summary>Flat ground at <paramref name="ground"/> with a round pit <paramref name="depth"/> deep (bowl-shaped).</summary>
    private static float Pit(float x, float z, float cx, float cz, float radius, float ground, float depth)
    {
        float r = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) / radius;
        return r >= 1f ? ground : ground - depth * (1f - r * r);
    }

    /// <summary>Lake levels for <paramref name="map"/>: <paramref name="level"/> wherever the ground is below <paramref name="below"/>.</summary>
    private static float[] Levels(HeightMap map, float level, float below)
    {
        var l = new float[map.Width * map.Depth];
        for (int i = 0; i < l.Length; i++) l[i] = map.Data[i] < below ? level : float.NaN;
        return l;
    }

    private static double Volume(WaterSim sim)
    {
        double v = 0;
        foreach (float d in sim.ReadDepth()) v += d;
        return v * sim.CellSize * sim.CellSize;
    }

    private static int[] Wet(WaterSim sim)
    {
        var d = sim.ReadDepth();
        return Enumerable.Range(0, d.Length).Where(i => d[i] > 0.01f).ToArray();
    }
}
