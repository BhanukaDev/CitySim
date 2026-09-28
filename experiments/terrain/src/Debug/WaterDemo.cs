using System;
using System.Collections.Generic;
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
/// runs repeat exactly (also on another thread count), settled water sleeps and wakes without losing any, outflow is
/// never clamped, pollutant rides the flow with its mass kept, how narrow channels fare on a coarse grid, lake sources
/// placed in the hollows hold them (and deleting one drains it), water paints the ground, the placement preview's flood,
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
            sim.LoadDepth(WaterGrid.FromDense(depth, sim.Width, sim.Depth));
            double before = Volume(sim);
            sim.Advance(900);
            double after = Volume(sim);
            var surfaces = Wet(sim).Select(i => map.Data[i] + sim.ReadDepth().ToDense()[i]).ToArray();
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
            var pollution = sim.ReadDepth().ToDense().Select(d => d > 0 ? d * 0.01f : 0f).ToArray();
            sim.LoadPollution(WaterGrid.FromDense(pollution, sim.Width, sim.Depth));
            sim.LoadPaint(WaterGrid.FromDense(sim.ReadDepth().ToDense().Select(d => MathF.Min(d / 5f, 1f)).ToArray(), sim.Width, sim.Depth));
            var data = new WaterData(sim.Settings, sim.Sources, sim.Width, sim.Depth, sim.ReadDepth(), sim.ReadPollution(), sim.ReadPaint());
            string path = Path.Combine(Path.GetTempPath(), "citysim_water_demo.csmap");
            MapFile.Save(path, map, new SplatMap(map.Width, map.Depth, map.CellSize) { ThemeId = "default" }, data);
            var (_, _, loaded) = MapFile.LoadWithWater(path);
            File.Delete(path);
            float worst = 0, worstPollution = 0;
            static float Worst(WaterGrid? a, WaterGrid? b)
            {
                if (a is null || b is null) return 0f;
                float[] da = a.ToDense(), db = b.ToDense();
                float m = 0;
                for (int i = 0; i < da.Length; i++) m = MathF.Max(m, MathF.Abs(da[i] - db[i]));
                return m;
            }
            worst = Worst(loaded?.DepthGrid, data.DepthGrid);
            worstPollution = Worst(loaded?.PollutionGrid, data.PollutionGrid);
            float worstPaint = Worst(loaded?.PaintGrid, data.PaintGrid);
            bool same = loaded is not null && loaded.Settings == data.Settings && loaded.Sources.SequenceEqual(data.Sources)
                && loaded.DepthGrid is not null && loaded.PollutionGrid is not null && data.PollutionGrid!.Sum() > 1f
                && loaded.PaintGrid is not null && data.PaintGrid!.Sum() > 1f;
            Check(same && worst < 0.01f && worstPollution == 0f && worstPaint < 0.003f,
                $"map file round trip (worst depth error {worst:0.0000} m, pollutant {worstPollution:0.######} kg, paint {worstPaint:0.000})");
        }

        // 8. The same run gives the same water: twice in a row, and on one thread instead of several.
        {
            var map = Make(193, 4f, (x, z) => z * 0.02f + 2f * MathF.Abs(x - 384) / 128f + Pit(x, z, 384, 200, 90, 0f, 3f));
            ulong Run(int threads)
            {
                using var sim = new WaterSim(map, threaded: false, threads: threads);
                sim.SetSources([
                    new WaterSource(1, WaterSourceKind.Stream, 384, 700, 12, 0, FlowRate: 15, Pollution: 0.5f),
                    new WaterSource(2, WaterSourceKind.Lake, 200, 300, 40, 12f, MaxFlow: 30),
                ]);
                sim.Advance(240);
                return Hash(sim.ReadDepth().ToDense()) ^ (Hash(sim.ReadPollution().ToDense()) * 31);
            }
            ulong a = Run(0), b = Run(0), c = Run(1);
            Check(a == b && a == c, $"repeatable (hash {a:x16}, again {(a == b ? "same" : "different")}, on 1 thread {(a == c ? "same" : "different")})");
        }

        // 9. Sleeping: a filled pond settles and sleeps; a stream then feeds it and every drop is accounted for; a
        //    terrain edit wakes it; outflow is never clamped anywhere.
        {
            var map = Make(257, 4f, (x, z) => Pit(x, z, 512, 512, 380, 10f, 6f) + Pit(x, z, 180, 180, 60, 0f, -3f));
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { OpenEdges = false, EvaporationMmPerMin = 0 } };
            sim.FillHollows(Levels(map, 8f, 9.99f));
            sim.Advance(120);
            var settled = sim.LastStats;
            double before = Volume(sim);
            const float rate = 4f;
            sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 180, 180, 10, 0, FlowRate: rate)]);
            sim.Advance(300);
            var fed = sim.LastStats;
            double after = Volume(sim), expected = before + rate * 300;
            sim.SetSources([]);
            // The film left on the mound trickles for a few minutes before everything is calm.
            var calm = sim.LastStats;
            int waited = 0;
            while (waited < 1200 && (waited == 0 || calm.ActiveTiles > 0))
            {
                sim.Advance(60);
                waited += 60;
                calm = sim.LastStats;
            }
            for (int z = 120; z <= 124; z++)
                for (int x = 120; x <= 124; x++) map[x, z] += 3f;
            sim.GroundChanged(new VertexRect(120, 120, 124, 124));
            sim.Advance(TickOf(sim));
            var woken = sim.LastStats;
            Check(settled.ActiveTiles == 0 && settled.SleepingTiles > 0 && fed.ActiveTiles > 0 && fed.SleepingTiles > 0
                  && calm.ActiveTiles == 0 && woken.ActiveTiles > 0,
                $"settled water sleeps ({settled.SleepingTiles} sleeping, {settled.ActiveTiles} active; fed {fed.ActiveTiles} active, " +
                $"{fed.SleepingTiles} sleeping; {calm.ActiveTiles} active {waited} s after it stops; an edit wakes {woken.ActiveTiles})");
            Check(Math.Abs(after - expected) < rate * 300 * 0.01,
                $"no water lost across sleeping tiles ({before:0} + {rate * 300:0} = {expected:0} m³, got {after:0} m³)");
            Check(sim.ClampHits == 0, $"outflow never clamped ({sim.ClampHits} cells)");
        }

        // 10. Pollution: a sewage stream into a walled basin keeps its mass (no decay) and drifts downhill; with a
        //     half-life the mass levels off.
        {
            var map = Make(129, 4f, (x, z) => z * 0.02f + Pit(x, z, 256, 120, 110, 0f, 4f));
            float Run(float halfLife, out float near, out float far)
            {
                using var sim = new WaterSim(map, threaded: false)
                {
                    Settings = new WaterSettings { OpenEdges = false, EvaporationMmPerMin = 0, PollutionHalfLifeMin = halfLife },
                };
                sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 256, 450, 10, 0, FlowRate: 5, Pollution: 2f)]);
                sim.Advance(600);
                near = sim.PollutionAt(256, 440);
                far = sim.PollutionAt(256, 120);
                return (float)sim.LastStats.Pollution;
            }
            float kept = Run(0, out float near, out float far), decayed = Run(2f, out _, out _);
            // With a 2 min half-life, the steady state is rate / decay = 2 / (ln 2 / 120 s) ≈ 346 kg.
            Check(MathF.Abs(kept - 1200f) < 12f && far > 0 && near > 0 && decayed < 400f && decayed > 250f,
                $"pollutant rides the flow (1200 kg added, {kept:0.#} kg in the water; {near:0.0000} kg/m³ at the outlet, " +
                $"{far:0.0000} kg/m³ in the basin; with a 2 min half-life {decayed:0} kg)");
        }

        // 11. Narrow channels: a 7 m and a 14 m wide channel (1.5 m deep) down a gentle slope, on 3.5, 7 (28.7 km maps) and
        //     14 m cells (averaged like a 2× or 4× coarser water grid). Reports how much water leaves the channel.
        foreach (float cell in new[] { 3.5f, 7f, 14f })
            foreach (float width in new[] { 7f, 14f })
            {
                // A 3.5 m terrain; the sim takes every 1st, 2nd or 4th vertex, as on maps up to 14.3 km, 28.7 km (7 m),
                // and before M6 phase 3f (14 m).
                var map = Make(257, 3.5f, (x, z) => z * 0.01f + (MathF.Abs(x - 448f) < width * 0.5f ? -1.5f : 0f));
                using var sim = new WaterSim(map, threaded: false, maxCells: (int)(256 * 3.5f / cell))
                    { Settings = new WaterSettings { EvaporationMmPerMin = 0 } };
                sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 448, 850, 5, 0, FlowRate: 3)]);
                sim.Advance(900);
                var d = sim.ReadDepth().ToDense();
                // What a player sees: how wide the wet strip is (mean over the rows it runs through), and the water
                // lying more than 7 m past the banks (a fixed band, whatever the cell size).
                double inside = 0, outside = 0, wetArea = 0;
                var rows = new HashSet<int>();
                for (int i = 0; i < d.Length; i++)
                {
                    if (d[i] <= 0.01f) continue;
                    float x = i % sim.Width * sim.CellSize;
                    wetArea += sim.CellSize * sim.CellSize;
                    rows.Add(i / sim.Width);
                    if (MathF.Abs(x - 448f) <= width * 0.5f + 7f) inside += d[i];
                    else outside += d[i];
                }
                double share = outside / Math.Max(inside + outside, 1e-9);
                double wetWidth = rows.Count == 0 ? 0 : wetArea / (rows.Count * sim.CellSize);
                log($"Demo water: narrow channel {width:0} m wide on {cell:0.#} m cells: wet strip {wetWidth:0.0} m wide, " +
                    $"{share:P1} of the water > 7 m past the banks, deepest {sim.LastStats.MaxDepth:0.00} m");
            }

        // 13. Lake sources: one per hollow, filled at once to the Priority-Flood volume, and they keep it (evaporation on);
        //     planning again adds none. Deleting one drains its lake only; putting the drained surface back restores it.
        {
            var map = Make(129, 4f, (x, z) => Pit(x, z, 180, 200, 50, 20f, 6f) + Pit(x, z, 360, 330, 70, 0f, 4f) + 0.01f * x);
            var lakes = Lakes.Find(map, new LakeSettings { MinArea = 0 }, null)!;
            double expected = 0;
            for (int i = 0; i < lakes.Level.Length; i++)
                if (!float.IsNaN(lakes.Level[i])) expected += (lakes.Level[i] - map.Data[i]) * map.CellSize * map.CellSize;
            using var sim = new WaterSim(map, threaded: false);
            var plan = LakeSources.Plan(lakes, map, sim.Sources, 1, sim.CellSize * 1.5f, sim.Settings.EvaporationMmPerMin)!;
            sim.SetSources(plan.Lakes.Select(l => l.Source));
            sim.RaiseTo(plan.Levels());
            double filled = Volume(sim);
            sim.Advance(1800);
            double kept = Volume(sim);
            var again = LakeSources.Plan(lakes, map, sim.Sources, 10, sim.CellSize * 1.5f, sim.Settings.EvaporationMmPerMin)!;
            bool inside = plan.Lakes.All(l => !float.IsNaN(lakes.LevelAt((int)(l.Source.X / map.CellSize), (int)(l.Source.Z / map.CellSize))));
            Check(plan.Lakes.Count == lakes.Count && inside && again.Lakes.Count == 0 && Math.Abs(filled - expected) / expected < 0.01
                && Math.Abs(kept - expected) / expected < 0.02,
                $"lake sources ({plan.Lakes.Count} for {lakes.Count} hollows, in the water {inside}, {expected:0} m³ expected, " +
                $"{filled:0} filled, {kept:0} after 30 min, {again.Lakes.Count} more on a second pass)");

            var first = plan.Lakes[0];
            var removed = sim.DrainSource(first.Source);
            double drained = Volume(sim);
            bool otherKept = Math.Abs(kept - drained - first.Volume) / first.Volume < 0.05;
            sim.RestoreSurface(removed);
            double restored = Volume(sim);
            Check(otherKept && Math.Abs(restored - kept) / kept < 0.01,
                $"drain on delete ({kept:0} → {drained:0} m³, its lake held {first.Volume:0}; restored {restored:0} m³)");
        }

        // 14. Wet paint: a stream paints its bed in PaintMinutes and leaves dry ground alone; the ground marks give
        //     distance 0 on the water and the far end (255) on dry ground far from it.
        {
            var map = Make(129, 4f, (x, z) => z * 0.05f + 2f * MathF.Abs(x - 256) / 256f);
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { PaintMinutes = 5f } };
            sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 256, 400, 10, 0, FlowRate: 20)]);
            sim.Advance(900);
            var paint = sim.ReadPaint().ToDense();
            var depth = sim.ReadDepth().ToDense();
            int wetCell = Enumerable.Range(0, depth.Length).Where(i => depth[i] > 0.3f).DefaultIfEmpty(-1).First(); // shores count water over 25 cm
            int dryCell = 5 * sim.Width + 5;
            sim.PublishNow();
            var marks = new byte[sim.Width * sim.Depth * 2];
            sim.CopyGroundMarks(marks);
            bool painted = wetCell >= 0 && paint[wetCell] > 0.99f && paint[dryCell] == 0f;
            bool distance = wetCell >= 0 && marks[wetCell * 2] == 0 && marks[dryCell * 2] == 255 && marks[wetCell * 2 + 1] > 250;
            Check(painted && distance, $"wet paint (bed {(wetCell >= 0 ? paint[wetCell] : -1):0.00}, dry ground {paint[dryCell]:0.00}; " +
                $"marks bed {(wetCell >= 0 ? marks[wetCell * 2] : -1)}/{(wetCell >= 0 ? marks[wetCell * 2 + 1] : -1)}, far {marks[dryCell * 2]})");
        }

        // 15. Placement preview: a lake below the rim fills the pit to its level; above the rim it spills at the rim.
        {
            var map = Make(129, 4f, (x, z) => Pit(x, z, 256, 256, 80, 20f, 10f));
            var ground = WaterFlood.Ground(map, 1, map.Width, map.Depth);
            var filledGrid = WaterFlood.Filled(ground, map.Width, map.Depth)!;
            var below = WaterFlood.Compute(ground, filledGrid, map.Width, map.Depth, 4f, new WaterSource(0, WaterSourceKind.Lake, 256, 256, 20, 15f));
            var above = WaterFlood.Compute(ground, filledGrid, map.Width, map.Depth, 4f, new WaterSource(0, WaterSourceKind.Lake, 256, 256, 20, 30f));
            // Bowl: depth 10 (1 - r²) under 20 m; filled to 15 m covers r < √0.5, volume = π R² · 10 · (0.5²/2) = 1.25 π R².
            double expected = Math.PI * 80 * 80 * 1.25;
            Check(below is { Spills: false } && MathF.Abs(below.Surface - 15f) < 0.01f && Math.Abs(below.Volume - expected) / expected < 0.05
                && above is { Spills: true } && MathF.Abs(above.Surface - 20f) < 0.05f,
                $"flood preview (below the rim: {below?.Surface:0.0} m, {below?.Volume:0} of {expected:0} m³; above: spills {above?.Spills} at {above?.Surface:0.0} m)");
        }

        // 16. Sparse tiles (M6 phase 3f): on a water grid 2× coarser than the terrain (as on 28.7 km maps), a stream holds
        //     memory only along its path, and once it's gone and dried up the tiles are freed. A pond saved on that grid
        //     loads onto a 2× finer one (an old 14 m save on today's 7 m grid) with about the same volume.
        {
            var map = Make(513, 3.5f, (x, z) => z * 0.02f + 3f * MathF.Abs(x - 900) / 900f + Pit(x, z, 400, 1300, 150, 0f, 6f));
            var dry = new WaterSettings { EvaporationMmPerMin = 0, PaintMinutes = 0, PaintFadeHours = 0 };
            using var sim = new WaterSim(map, threaded: false, maxCells: 256) { Settings = dry };
            int total = sim.TilesX * sim.TilesZ;
            sim.SetSources([new WaterSource(1, WaterSourceKind.Stream, 900, 1700, 10, 0, FlowRate: 5)]);
            sim.Advance(600);
            int running = sim.LastStats.AllocatedTiles;
            sim.SetSources([]);
            sim.Settings = dry with { EvaporationMmPerMin = 60 };
            sim.Advance(1800);
            int after = sim.LastStats.AllocatedTiles;
            Check(sim.Factor == 2 && running > 0 && running < total && after == 0,
                $"sparse tiles ({sim.Width}² cells of {sim.CellSize:0.#} m, {total} tiles: {running} in memory with a stream, " +
                $"{after} after it dried up)");

            var bowl = Make(513, 3.5f, (x, z) => Pit(x, z, 900, 900, 300, 10f, 8f));
            using var coarse = new WaterSim(bowl, threaded: false, maxCells: 256) { Settings = dry };
            coarse.FillHollows(Levels(bowl, 7f, 7f));
            coarse.Advance(1);
            double before = Volume(coarse);
            using var fine = new WaterSim(bowl, threaded: false) { Settings = dry };
            fine.LoadWater(coarse.ReadDepth().Resample(fine.Width, fine.Depth));
            fine.Advance(1);
            double loaded = Volume(fine);
            Check(before > 1000 && Math.Abs(loaded - before) / before < 0.05,
                $"coarse save on a finer grid ({before:0} m³ on {coarse.CellSize:0.#} m cells → {loaded:0} m³ on {fine.CellSize:0.#} m)");
        }

        // 12. Cost of a substep with every cell wet (2 m of water on a gentle slope, walls), 1025² and 2049².
        foreach (int verts in new[] { 1025, 2049 })
        {
            var map = Make(verts, 3.5f, (x, z) => 0.001f * x);
            using var sim = new WaterSim(map, threaded: false) { Settings = new WaterSettings { OpenEdges = false } };
            var depth = new float[sim.Width * sim.Depth];
            Array.Fill(depth, 2f);
            sim.LoadDepth(WaterGrid.FromDense(depth, sim.Width, sim.Depth));
            sim.Advance(1);
            var sw = Stopwatch.StartNew();
            sim.Advance(20);
            var st = sim.LastStats;
            log($"Demo water: {verts}² fully wet: {sim.StepMs:0.00} ms per substep ({st.Substeps} substeps per tick, {sw.ElapsedMilliseconds} ms for 20 s)");
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

    private static double TickOf(WaterSim sim) => WaterSim.TickSeconds;

    private static ulong Hash(float[] values)
    {
        ulong h = 14695981039346656037;
        foreach (float v in values)
        {
            h ^= BitConverter.SingleToUInt32Bits(v);
            h *= 1099511628211;
        }
        return h;
    }

    private static double Volume(WaterSim sim)
    {
        double v = 0;
        foreach (float d in sim.ReadDepth().ToDense()) v += d;
        return v * sim.CellSize * sim.CellSize;
    }

    private static int[] Wet(WaterSim sim)
    {
        var d = sim.ReadDepth().ToDense();
        return Enumerable.Range(0, d.Length).Where(i => d[i] > 0.01f).ToArray();
    }
}
