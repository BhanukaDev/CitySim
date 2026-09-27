using System;
using System.Collections.Generic;
using System.Threading;
using CitySim.TerrainSystem;

namespace CitySim.WaterSystem;

/// <summary>
/// The water a level source would hold, on the water grid: cells [X0..X1] × [Z0..Z1] (inclusive), one byte each in
/// <see cref="Mask"/> (1 = under water), the <see cref="Surface"/> it settles at, and whether it
/// <see cref="Spills"/> over a rim (or off the map) before reaching the source's level.
/// </summary>
public sealed record WaterFloodResult(int X0, int Z0, int X1, int Z1, byte[] Mask, float Surface, bool Spills,
    float Area, float Volume)
{
    public int Width => X1 - X0 + 1;
    public int Depth => Z1 - Z0 + 1;
}

/// <summary>Works out what a Lake, River or Sea source would flood, for the placement preview. Engine-agnostic.</summary>
public static class WaterFlood
{
    /// <summary>The ground under each water cell (the terrain vertex it sits on).</summary>
    public static float[] Ground(HeightMap map, int factor, int width, int depth)
    {
        var g = new float[width * depth];
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) g[z * width + x] = map.Data[z * factor * map.Width + x * factor];
        return g;
    }

    /// <summary>
    /// Priority-Flood (Barnes 2014) from the map edge: per cell, the height water there fills up to before it can run off
    /// the map (its own ground when it drains). Null if cancelled.
    /// </summary>
    public static float[]? Filled(float[] ground, int width, int depth, CancellationToken ct = default)
    {
        var filled = new float[ground.Length];
        var seen = new bool[ground.Length];
        var open = new PriorityQueue<int, float>();
        for (int i = 0; i < ground.Length; i++)
        {
            int x = i % width, z = i / width;
            if (x != 0 && z != 0 && x != width - 1 && z != depth - 1) continue;
            seen[i] = true;
            filled[i] = ground[i];
            open.Enqueue(i, ground[i]);
        }
        int n = 0;
        while (open.TryDequeue(out int i, out float h))
        {
            if ((++n & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
            foreach (int m in Neighbours(i, width, depth))
            {
                if (seen[m]) continue;
                seen[m] = true;
                filled[m] = MathF.Max(ground[m], h);
                open.Enqueue(m, filled[m]);
            }
        }
        return filled;
    }

    /// <summary>
    /// Lake/River: the hollow around the source's lowest cell filled to its level, or only to where it spills over its
    /// rim (<paramref name="filled"/>, <see cref="Filled"/>) when the level is higher. Sea: every cell below sea level
    /// connected to the border. Null if cancelled or nothing floods (e.g. a source on a slope: the water runs downhill).
    /// </summary>
    public static WaterFloodResult? Compute(float[] ground, float[] filled, int width, int depth, float cell, WaterSource s,
        CancellationToken ct = default)
    {
        var inside = new List<int>();
        var seen = new bool[ground.Length];
        var stack = new Stack<int>();
        float surface = s.Level;
        bool spills = false;
        if (s.Kind == WaterSourceKind.Sea)
        {
            for (int i = 0; i < ground.Length; i++)
            {
                int x = i % width, z = i / width;
                bool border = x == 0 || z == 0 || x == width - 1 || z == depth - 1;
                if (border && ground[i] < s.Level) { seen[i] = true; stack.Push(i); }
            }
        }
        else
        {
            float r = MathF.Max(s.Radius, cell * 0.5f);
            int cx0 = Math.Max(0, (int)MathF.Floor((s.X - r) / cell)), cx1 = Math.Min(width - 1, (int)MathF.Ceiling((s.X + r) / cell));
            int cz0 = Math.Max(0, (int)MathF.Floor((s.Z - r) / cell)), cz1 = Math.Min(depth - 1, (int)MathF.Ceiling((s.Z + r) / cell));
            int lowest = Math.Clamp((int)MathF.Round(s.Z / cell), 0, depth - 1) * width + Math.Clamp((int)MathF.Round(s.X / cell), 0, width - 1);
            for (int z = cz0; z <= cz1; z++)
                for (int x = cx0; x <= cx1; x++)
                {
                    float dx = x * cell - s.X, dz = z * cell - s.Z;
                    int i = z * width + x;
                    if (dx * dx + dz * dz < r * r && ground[i] < ground[lowest]) lowest = i;
                }
            // Water gathers in the hollow under the source's lowest point and rises to the level or the rim.
            if (filled[lowest] < s.Level - 0.01f)
            {
                surface = filled[lowest];
                spills = true;
            }
            if (ground[lowest] < surface) { seen[lowest] = true; stack.Push(lowest); }
        }
        while (stack.Count > 0)
        {
            if ((inside.Count & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
            int i = stack.Pop();
            inside.Add(i);
            foreach (int n in Neighbours(i, width, depth))
                if (!seen[n] && ground[n] < surface) { seen[n] = true; stack.Push(n); }
        }
        if (inside.Count == 0) return null;

        int x0 = width, z0 = depth, x1 = -1, z1 = -1;
        double volume = 0;
        foreach (int i in inside)
        {
            int x = i % width, z = i / width;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            volume += surface - ground[i];
        }
        int w = x1 - x0 + 1;
        var mask = new byte[w * (z1 - z0 + 1)];
        foreach (int i in inside) mask[(i / width - z0) * w + i % width - x0] = 255;
        return new WaterFloodResult(x0, z0, x1, z1, mask, surface, spills, inside.Count * cell * cell, (float)(volume * cell * cell));
    }

    private static IEnumerable<int> Neighbours(int i, int width, int depth)
    {
        int x = i % width, z = i / width;
        if (x > 0) yield return i - 1;
        if (x < width - 1) yield return i + 1;
        if (z > 0) yield return i - width;
        if (z < depth - 1) yield return i + width;
    }
}
