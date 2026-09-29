using System;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>
/// A square greyscale brush shape (0 to 1 per texel), stretched over the brush's bounding square.
/// Replaces the round falloff for textured brushes. The baked masks already fade to 0 at the edge.
/// </summary>
public sealed class BrushMask
{
    private readonly float[] _values;

    public string Name { get; }
    public int Size { get; }

    public BrushMask(string name, int size, float[] values)
    {
        if (values.Length != size * size)
            throw new ArgumentException($"expected {size * size} values, got {values.Length}", nameof(values));
        Name = name;
        Size = size;
        _values = values;
    }

    /// <summary>Bilinear sample at (u, v) in 0..1. Outside the square the mask is 0.</summary>
    public float Sample(float u, float v)
    {
        if (u < 0f || u > 1f || v < 0f || v > 1f) return 0f;
        float fx = u * (Size - 1), fz = v * (Size - 1);
        int x0 = Math.Min((int)fx, Size - 2), z0 = Math.Min((int)fz, Size - 2);
        float tx = fx - x0, tz = fz - z0;
        int i = z0 * Size + x0;
        float a = _values[i] + (_values[i + 1] - _values[i]) * tx;
        float b = _values[i + Size] + (_values[i + Size + 1] - _values[i + Size]) * tx;
        return a + (b - a) * tz;
    }
}
