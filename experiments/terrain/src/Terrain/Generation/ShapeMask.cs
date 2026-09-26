using System;

namespace CitySim.TerrainSystem.Generation;

/// <summary>
/// The land/sea mask: 1 on land, 0 on the sea floor, with a soft blend <see cref="ShapeSettings.EdgeWidth"/> wide.
/// Works in map units (-1..1 across the map) so a shape is the same at every map size and resolution. Thread-safe.
/// </summary>
public sealed class ShapeMask
{
    private readonly ShapeSettings _s;
    private readonly FastNoiseLite _coast;
    private readonly FastNoiseLite _islands;
    private readonly float _dirX, _dirZ;

    public ShapeMask(ShapeSettings settings, int seed)
    {
        _s = settings;
        // Wobble for coastlines: a few cycles across the map, with finer octaves for bays and headlands.
        _coast = new FastNoiseLite(seed + 101);
        _coast.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
        _coast.SetFrequency(1.2f);
        _coast.SetFractalType(FastNoiseLite.FractalType.FBm);
        _coast.SetFractalOctaves(4);
        _coast.SetFractalGain(0.5f);
        // Archipelago: blobs a few hundred metres to a kilometre across (on a 2 km map).
        _islands = new FastNoiseLite(seed + 202);
        _islands.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
        _islands.SetFrequency(1.6f);
        _islands.SetFractalType(FastNoiseLite.FractalType.FBm);
        _islands.SetFractalOctaves(3);
        _islands.SetFractalGain(0.45f);
        float a = settings.Direction * (MathF.PI / 180f);
        // 0° = north (z = 0 edge, i.e. -z), 90° = east (+x).
        _dirX = MathF.Sin(a);
        _dirZ = -MathF.Cos(a);
    }

    /// <summary>Land weight at map position (<paramref name="u"/>, <paramref name="v"/>), both 0..1 across the map.</summary>
    public float Sample(float u, float v)
    {
        float x = u * 2f - 1f, z = v * 2f - 1f;
        float e = MathF.Max(_s.EdgeWidth, 1e-3f);
        float wobble = _s.Roughness * 0.35f * _coast.GetNoise(x, z);
        switch (_s.Kind)
        {
            case ShapeKind.Island:
            {
                float d = MathF.Sqrt(x * x + z * z);
                return SmoothStep(-e, e, _s.Size - d + wobble);
            }
            case ShapeKind.Coast:
            {
                // Distance along the sea direction, -1 (far inland edge) to 1 (sea edge) for an axis-aligned direction.
                float along = x * _dirX + z * _dirZ;
                float line = -1f + 2f * _s.Size;
                return SmoothStep(-e, e, line - along + wobble);
            }
            case ShapeKind.Archipelago:
            {
                float n = _islands.GetNoise(x, z) + wobble * 0.5f;
                // Size 0..1 → threshold: more land at larger sizes. fBm rarely leaves ±0.6.
                float land = SmoothStep(-e, e, n - (0.5f - _s.Size) * 1.2f);
                // Fade to sea near the map border, so it reads as islands in a sea.
                float d = MathF.Max(MathF.Abs(x), MathF.Abs(z));
                return land * SmoothStep(1f, 0.75f, d);
            }
            default:
                return 1f;
        }
    }

    public static float SmoothStep(float from, float to, float x)
    {
        float t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
