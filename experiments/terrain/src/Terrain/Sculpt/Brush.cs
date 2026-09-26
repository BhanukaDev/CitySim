namespace CitySim.TerrainSystem.Sculpt;

/// <summary>A circular sculpt brush. Radius is in world units, strength is 0 to 1.</summary>
public readonly record struct Brush(float Radius, float Strength)
{
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
