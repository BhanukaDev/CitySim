using System;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>
/// A sculpt/paint brush. Radius is in world units, strength is 0 to 1. With a <see cref="Mask"/> the brush
/// takes the mask's shape, turned by <see cref="Angle"/> (radians, counter-clockwise seen from above);
/// without one it's round with a smooth falloff.
/// </summary>
public readonly record struct Brush(float Radius, float Strength, BrushMask? Mask = null, float Angle = 0f)
{
    /// <summary>The same brush without its mask: round, as tools that need a clean shape (slope) use it.</summary>
    public Brush Round => this with { Mask = null };

    /// <summary>Weight (0 to 1) at offset (dx, dz) from the centre, in world units. 0 outside the radius.</summary>
    public float Weight(float dx, float dz)
    {
        float d = MathF.Sqrt(dx * dx + dz * dz) / Radius;
        if (d >= 1f) return 0f;
        if (Mask is null) return Falloff(d);
        // Undo the brush rotation, then map the bounding square [-r, r]² onto the mask's 0..1 UVs.
        var (s, c) = MathF.SinCos(Angle);
        float lx = c * dx - s * dz, lz = s * dx + c * dz;
        return Mask.Sample(0.5f + 0.5f * lx / Radius, 0.5f + 0.5f * lz / Radius);
    }

    /// <summary><see cref="Weight"/> with a wider full-strength core, like <see cref="PlateauFalloff"/>.</summary>
    public float PlateauWeight(float dx, float dz) => MathF.Min(Weight(dx, dz) * 2f, 1f);

    /// <summary>Weight at a normalised distance from the centre: 1 at the centre, easing to 0 at the edge.</summary>
    public static float Falloff(float distance01)
    {
        if (distance01 >= 1f) return 0f;
        float t = 1f - distance01;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Falloff with a wider full-strength core. Used by tools that pull toward a target height
    /// (level, slope), so the middle of the brush actually reaches the target.
    /// </summary>
    public static float PlateauFalloff(float distance01)
    {
        float f = Falloff(distance01) * 2f;
        return f > 1f ? 1f : f;
    }
}
