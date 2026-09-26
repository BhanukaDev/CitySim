using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>
/// Brush operations on a <see cref="HeightMap"/>. Positions and heights are in the map's local space
/// (world units). Each op is one simulation tick of <c>dt</c> seconds and returns the vertices it changed.
/// </summary>
public static class SculptOps
{
    /// <summary>Raise/lower speed in metres per second per metre of brush radius, at full strength.</summary>
    public const float ShiftRate = 0.3f;
    /// <summary>How fast level/slope converge on their target, per second at full strength.</summary>
    public const float PullRate = 10f;
    /// <summary>How fast smoothing converges on the local average, per second at full strength.</summary>
    public const float SmoothRate = 15f;

    public static VertexRect Shift(HeightMap map, Vector2 center, Brush brush, float sign, float dt)
    {
        float amount = sign * ShiftRate * brush.Radius * brush.Strength * dt;
        return Apply(map, center, brush, (x, z, d) => map[x, z] += amount * Brush.Falloff(d));
    }

    public static VertexRect Level(HeightMap map, Vector2 center, Brush brush, float target, float dt)
    {
        float k = PullFactor(PullRate, brush.Strength, dt);
        return Apply(map, center, brush, (x, z, d) =>
        {
            float h = map[x, z];
            map[x, z] = h + (target - h) * k * Brush.PlateauFalloff(d);
        });
    }

    /// <summary>Pulls ground toward the straight ramp from (a, heightA) to (b, heightB).</summary>
    public static VertexRect Slope(HeightMap map, Vector2 center, Brush brush,
        Vector2 a, float heightA, Vector2 b, float heightB, float dt)
    {
        float k = PullFactor(PullRate, brush.Strength, dt);
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        return Apply(map, center, brush, (x, z, d) =>
        {
            var p = new Vector2(x * map.CellSize, z * map.CellSize);
            float t = lenSq > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f) : 0f;
            float target = heightA + (heightB - heightA) * t;
            float h = map[x, z];
            map[x, z] = h + (target - h) * k * Brush.PlateauFalloff(d);
        });
    }

    public static VertexRect Smooth(HeightMap map, Vector2 center, Brush brush, float dt)
    {
        var rect = map.CircleRect(center.X, center.Y, brush.Radius);
        if (rect.IsEmpty) return rect;

        // Read neighbours from an unmodified copy (one vertex wider), so the blur doesn't smear
        // in scan order.
        var src = new VertexRect(
            Math.Max(0, rect.MinX - 1), Math.Max(0, rect.MinZ - 1),
            Math.Min(map.Width - 1, rect.MaxX + 1), Math.Min(map.Depth - 1, rect.MaxZ + 1));
        float[] copy = map.CopyRegion(src);
        float Get(int x, int z)
        {
            x = Math.Clamp(x, src.MinX, src.MaxX);
            z = Math.Clamp(z, src.MinZ, src.MaxZ);
            return copy[(z - src.MinZ) * src.Width + (x - src.MinX)];
        }

        float k = PullFactor(SmoothRate, brush.Strength, dt);
        return Apply(map, center, brush, (x, z, d) =>
        {
            float avg = (4f * Get(x, z) +
                         2f * (Get(x - 1, z) + Get(x + 1, z) + Get(x, z - 1) + Get(x, z + 1)) +
                         Get(x - 1, z - 1) + Get(x + 1, z - 1) + Get(x - 1, z + 1) + Get(x + 1, z + 1)) / 16f;
            float h = map[x, z];
            map[x, z] = h + (avg - h) * k * Brush.Falloff(d);
        });
    }

    /// <summary>
    /// The height most of the brush area already sits at: a falloff-weighted histogram of heights,
    /// taking the heaviest bin (with its neighbours) and returning the weighted mean inside it.
    /// Used by Level when no target height has been picked.
    /// </summary>
    public static float DominantHeight(HeightMap map, Vector2 center, Brush brush)
    {
        var samples = new List<(float H, float W)>();
        float min = float.MaxValue, max = float.MinValue;
        Visit(map, center, brush, (x, z, d) =>
        {
            float h = map[x, z];
            samples.Add((h, Brush.Falloff(d) + 1e-3f));
            if (h < min) min = h;
            if (h > max) max = h;
        });
        if (samples.Count == 0)
            return map.SampleHeight(center.X, center.Y);

        // Bins at least 0.5 m wide, and never more than 32 across the range.
        float binSize = MathF.Max(0.5f, (max - min) / 32f);
        int bins = (int)((max - min) / binSize) + 1;
        var weights = new float[bins];
        foreach (var (h, w) in samples)
            weights[(int)((h - min) / binSize)] += w;

        int best = 0;
        float bestScore = -1f;
        for (int i = 0; i < bins; i++)
        {
            float score = weights[i] + (i > 0 ? weights[i - 1] : 0f) + (i < bins - 1 ? weights[i + 1] : 0f);
            if (score > bestScore) { bestScore = score; best = i; }
        }

        float lo = min + (best - 1) * binSize, hi = min + (best + 2) * binSize;
        float sum = 0f, wsum = 0f;
        foreach (var (h, w) in samples)
            if (h >= lo && h < hi) { sum += h * w; wsum += w; }
        return wsum > 0f ? sum / wsum : map.SampleHeight(center.X, center.Y);
    }

    /// <summary>Frame-rate independent blend factor for exponential convergence.</summary>
    private static float PullFactor(float rate, float strength, float dt) => 1f - MathF.Exp(-rate * strength * dt);

    private static VertexRect Apply(HeightMap map, Vector2 center, Brush brush, Action<int, int, float> op)
    {
        Visit(map, center, brush, op);
        return map.CircleRect(center.X, center.Y, brush.Radius);
    }

    /// <summary>Calls <paramref name="op"/>(x, z, normalisedDistance) for every vertex inside the brush circle.</summary>
    private static void Visit(HeightMap map, Vector2 center, Brush brush, Action<int, int, float> op)
    {
        if (brush.Radius <= 0f) return;
        var r = map.CircleRect(center.X, center.Y, brush.Radius);
        float inv = 1f / brush.Radius;
        for (int z = r.MinZ; z <= r.MaxZ; z++)
        {
            float dz = z * map.CellSize - center.Y;
            for (int x = r.MinX; x <= r.MaxX; x++)
            {
                float dx = x * map.CellSize - center.X;
                float d = MathF.Sqrt(dx * dx + dz * dz) * inv;
                if (d < 1f) op(x, z, d);
            }
        }
    }
}
