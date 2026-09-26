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

    // Changes each tick, so the dithered rounding below differs from tick to tick.
    private static uint _tick;

    /// <summary>
    /// Pulls <paramref name="layer"/> toward full coverage and everything else (the other painted layer and the
    /// automatic ground) toward zero. A vertex holds two painted layers: painting a third replaces the lighter one.
    /// </summary>
    public static VertexRect Paint(SplatMap splat, Vector2 center, Brush brush, int layer, float dt)
    {
        float k = PullFactor(brush.Strength, dt);
        uint tick = ++_tick;
        return Apply(splat, center, brush, (x, z, c, f) =>
        {
            float t = k * f;
            int cov8 = SplatMap.Coverage(c);
            int b = SplatMap.Base(c), o = SplatMap.Overlay(c);
            float cov = cov8 / 255f, blend = SplatMap.Blend(c) / 255f;
            if (cov8 == 0) { b = o = layer; blend = 0f; }
            else if (b != layer && o != layer)
            {
                // Replace the lighter layer; its share goes back to automatic ground.
                if (blend <= 0.5f) { cov *= 1f - blend; o = layer; blend = 0f; }
                else { cov *= blend; b = layer; blend = 1f; }
            }

            float wb = cov * (1f - blend), wo = cov * blend;
            if (b == layer) { wb += (1f - wb) * t; wo *= 1f - t; }
            else { wo += (1f - wo) * t; wb *= 1f - t; }
            float newCov = wb + wo;
            return SplatMap.Encode(b, o, Quantize(wo / newCov, x, z, tick), Quantize(newCov, x, z, tick ^ 0x9E3779B9u));
        });
    }

    /// <summary>Fades the painted layers out, handing the ground back to the automatic rules.</summary>
    public static VertexRect Erase(SplatMap splat, Vector2 center, Brush brush, float dt)
    {
        float k = PullFactor(brush.Strength, dt);
        uint tick = ++_tick;
        return Apply(splat, center, brush, (x, z, c, f) =>
        {
            int cov8 = SplatMap.Coverage(c);
            if (cov8 == 0) return c;
            int cov = Quantize(cov8 / 255f * (1f - k * f), x, z, tick);
            return SplatMap.Encode(SplatMap.Base(c), SplatMap.Overlay(c), SplatMap.Blend(c), cov);
        });
    }

    private static float PullFactor(float strength, float dt) => 1f - MathF.Exp(-PaintRate * strength * dt);

    /// <summary>
    /// 0..1 → 0..255, rounded up or down at random (a hash of the vertex and tick) in proportion to the remainder.
    /// Plain rounding would drop the small per-tick changes at a soft brush edge, so the edge would never fill in.
    /// </summary>
    private static int Quantize(float v, int x, int z, uint tick)
    {
        float s = Math.Clamp(v, 0f, 1f) * 255f;
        int q = (int)s;
        uint h = (uint)x * 0x8DA6B343u ^ (uint)z * 0xD8163841u ^ tick * 0xCB1AB31Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return q + ((h & 0xFFFF) / 65536f < s - q ? 1 : 0);
    }

    private delegate uint ControlOp(int x, int z, uint control, float falloff);

    private static VertexRect Apply(SplatMap splat, Vector2 center, Brush brush, ControlOp op)
    {
        if (brush.Radius <= 0f) return VertexRect.Empty;
        var r = splat.CircleRect(center.X, center.Y, brush.Radius);
        for (int z = r.MinZ; z <= r.MaxZ; z++)
        {
            float dz = z * splat.CellSize - center.Y;
            for (int x = r.MinX; x <= r.MaxX; x++)
            {
                float w = brush.Weight(x * splat.CellSize - center.X, dz);
                if (w <= 0f) continue;
                uint c = splat.Get(x, z);
                uint n = op(x, z, c, w);
                if (n != c) splat.Set(x, z, n);
            }
        }
        return r;
    }
}
