using System;
using System.Numerics;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>
/// Brush operations on a <see cref="SplatMap"/>. Like <see cref="SculptOps"/>: positions are in the map's
/// local space, each op is one tick of <c>dt</c> seconds and returns the vertices it changed.
/// </summary>
public static class PaintOps
{
    /// <summary>How fast painting converges on full coverage, per second at full strength.</summary>
    public const float PaintRate = 12f;

    /// <summary>
    /// Pulls <paramref name="layer"/> toward full weight and every other layer toward zero.
    /// The weights keep summing to at most 1.
    /// </summary>
    public static VertexRect Paint(SplatMap splat, Vector2 center, Brush brush, int layer, float dt)
    {
        float k = PullFactor(brush.Strength, dt);
        return Apply(splat, center, brush, (w, f) =>
        {
            float t = k * f;
            for (int i = 0; i < w.Length; i++)
                w[i] += ((i == layer ? 1f : 0f) - w[i]) * t;
        });
    }

    /// <summary>Fades every painted weight out, handing the ground back to the automatic rules.</summary>
    public static VertexRect Erase(SplatMap splat, Vector2 center, Brush brush, float dt)
    {
        float k = PullFactor(brush.Strength, dt);
        return Apply(splat, center, brush, (w, f) =>
        {
            float keep = 1f - k * f;
            for (int i = 0; i < w.Length; i++)
                w[i] = w[i] * keep < 1e-3f ? 0f : w[i] * keep;
        });
    }

    private static float PullFactor(float strength, float dt) => 1f - MathF.Exp(-PaintRate * strength * dt);

    private delegate void WeightOp(Span<float> weights, float falloff);

    private static VertexRect Apply(SplatMap splat, Vector2 center, Brush brush, WeightOp op)
    {
        if (brush.Radius <= 0f) return VertexRect.Empty;
        var r = splat.CircleRect(center.X, center.Y, brush.Radius);
        for (int z = r.MinZ; z <= r.MaxZ; z++)
        {
            float dz = z * splat.CellSize - center.Y;
            for (int x = r.MinX; x <= r.MaxX; x++)
            {
                float w = brush.Weight(x * splat.CellSize - center.X, dz);
                if (w > 0f) op(splat.At(x, z), w);
            }
        }
        return r;
    }
}
