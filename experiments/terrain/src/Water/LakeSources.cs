using System;
using System.Collections.Generic;
using System.Threading;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Erosion;

namespace CitySim.WaterSystem;

/// <summary>A Lake source planned for one hollow: the source, the hollow's label in <see cref="LakePlan.Labels"/>, its size.</summary>
public sealed record PlannedLake(WaterSource Source, int Label, float Area, float Volume);

/// <summary>
/// Lake sources that were added: the source lists before and after, the new sources, and the surface they were filled to
/// per terrain vertex (NaN elsewhere; null when none were added). For undo.
/// </summary>
public sealed record LakeSourcesAdded(WaterSource[] Before, WaterSource[] After, WaterSource[] Added, float[]? Levels);

/// <summary>What <see cref="LakeSources.Plan"/> found: a label per terrain vertex (-1 = dry) and the sources to add.</summary>
public sealed class LakePlan
{
    public required int Width { get; init; }
    public required int[] Labels { get; init; }
    public required List<PlannedLake> Lakes { get; init; }

    /// <summary>The water surface per terrain vertex for the planned lakes only (NaN elsewhere), to fill them at once.</summary>
    public float[] Levels()
    {
        var byLabel = new Dictionary<int, float>();
        foreach (var l in Lakes) byLabel[l.Label] = l.Source.Level;
        var levels = new float[Labels.Length];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = Labels[i] >= 0 && byLabel.TryGetValue(Labels[i], out float v) ? v : float.NaN;
        return levels;
    }
}

/// <summary>
/// Puts water in the hollows through sources the user owns: one Lake source per hollow (Priority-Flood lake,
/// <see cref="LakeMap"/>), in its most open water, holding the hollow's spill level. Deleting the source drains the
/// lake. Engine-agnostic.
/// </summary>
public static class LakeSources
{
    /// <summary>Largest radius of a planned source (m): the water spreads from it, it doesn't have to cover the lake.</summary>
    public const float MaxRadius = 150f;
    /// <summary>A lake source fills its hollow in about this many simulated seconds, if it starts empty.</summary>
    public const float FillSeconds = 600f;

    /// <summary>
    /// Plans a Lake source for every lake in <paramref name="lakes"/> that no Lake or River source in
    /// <paramref name="existing"/> sits in yet. Ids start at <paramref name="firstId"/>; radii are at least
    /// <paramref name="minRadius"/> (so a source covers a water cell). Returns null if cancelled.
    /// </summary>
    public static LakePlan? Plan(LakeMap lakes, HeightMap map, IReadOnlyList<WaterSource> existing, int firstId, float minRadius,
        float evaporationMmPerMin, CancellationToken ct = default)
    {
        int w = lakes.Width, d = lakes.Depth;
        float cell = lakes.CellSize;
        var level = lakes.Level;
        var h = map.Data;

        // Label connected lakes (4-neighbours at the same level: neighbouring hollows can spill into each other).
        var labels = new int[w * d];
        Array.Fill(labels, -1);
        var stack = new Stack<int>();
        var area = new List<float>();
        var volume = new List<double>();
        var lakeLevel = new List<float>();
        for (int start = 0; start < labels.Length; start++)
        {
            if (labels[start] >= 0 || float.IsNaN(level[start])) continue;
            if ((start & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
            int id = area.Count;
            float lv = level[start];
            int cells = 0;
            double vol = 0;
            labels[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                cells++;
                vol += Math.Max(0f, lv - h[i]);
                int x = i % w, z = i / w;
                void Visit(int n)
                {
                    if (labels[n] >= 0 || float.IsNaN(level[n]) || MathF.Abs(level[n] - lv) > 1e-3f) return;
                    labels[n] = id;
                    stack.Push(n);
                }
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (z > 0) Visit(i - w);
                if (z < d - 1) Visit(i + w);
            }
            area.Add(cells * cell * cell);
            volume.Add(vol * cell * cell);
            lakeLevel.Add(lv);
        }
        if (ct.IsCancellationRequested) return null;

        // Distance to the shore inside each lake (two-pass chamfer); the source goes where it's largest.
        var dist = new float[w * d];
        for (int i = 0; i < dist.Length; i++) dist[i] = labels[i] >= 0 ? float.MaxValue : 0f;
        float c1 = cell, c2 = cell * MathF.Sqrt(2f);
        for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                if (dist[i] == 0f) continue;
                float v = dist[i];
                v = MathF.Min(v, x > 0 ? dist[i - 1] + c1 : c1);
                v = MathF.Min(v, z > 0 ? dist[i - w] + c1 : c1);
                v = MathF.Min(v, x > 0 && z > 0 ? dist[i - w - 1] + c2 : c2);
                v = MathF.Min(v, x < w - 1 && z > 0 ? dist[i - w + 1] + c2 : c2);
                dist[i] = v;
            }
        if (ct.IsCancellationRequested) return null;
        int count = area.Count;
        var best = new int[count];
        Array.Fill(best, -1);
        for (int z = d - 1; z >= 0; z--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = z * w + x;
                if (dist[i] == 0f) continue;
                float v = dist[i];
                v = MathF.Min(v, x < w - 1 ? dist[i + 1] + c1 : c1);
                v = MathF.Min(v, z < d - 1 ? dist[i + w] + c1 : c1);
                v = MathF.Min(v, x < w - 1 && z < d - 1 ? dist[i + w + 1] + c2 : c2);
                v = MathF.Min(v, x > 0 && z < d - 1 ? dist[i + w - 1] + c2 : c2);
                dist[i] = v;
                int b = best[labels[i]];
                // Farthest from the shore; on a tie, the deeper point.
                if (b < 0 || v > dist[b] || (v == dist[b] && h[i] < h[b])) best[labels[i]] = i;
            }

        // Hollows that already have a source in them keep it.
        var taken = new bool[count];
        foreach (var s in existing)
        {
            if (s.Kind is not (WaterSourceKind.Lake or WaterSourceKind.River)) continue;
            int x = (int)MathF.Round(s.X / cell), z = (int)MathF.Round(s.Z / cell);
            if (x < 0 || z < 0 || x >= w || z >= d) continue;
            int l = labels[z * w + x];
            if (l >= 0) taken[l] = true;
        }

        float evap = evaporationMmPerMin / 1000f / 60f;
        var planned = new List<PlannedLake>();
        int nextId = firstId;
        for (int l = 0; l < count; l++)
        {
            if (taken[l] || best[l] < 0) continue;
            int i = best[l];
            float x = i % w * cell, z = i / w * cell;
            float radius = Math.Clamp(dist[i] * 0.5f, minRadius, MathF.Max(minRadius, MaxRadius));
            float maxFlow = Nice(MathF.Max(MathF.Max((float)volume[l] / FillSeconds, 3f * area[l] * evap), 1f));
            var source = new WaterSource(nextId++, WaterSourceKind.Lake, x, z, radius, lakeLevel[l], MaxFlow: maxFlow);
            planned.Add(new PlannedLake(source, l, area[l], (float)volume[l]));
        }
        return new LakePlan { Width = w, Labels = labels, Lakes = planned };
    }

    /// <summary>Rounds up to two significant digits (37.4 → 38, 1234 → 1300).</summary>
    private static float Nice(float v)
    {
        float scale = MathF.Pow(10f, MathF.Floor(MathF.Log10(v)) - 1f);
        return MathF.Ceiling(v / scale) * scale;
    }
}
