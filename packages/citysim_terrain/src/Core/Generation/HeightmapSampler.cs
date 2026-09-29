using System;

namespace CitySim.TerrainSystem.Generation;

/// <summary>
/// Samples a <see cref="HeightmapImage"/> placed on the map by an <see cref="ImagePlacement"/> (rotate, scale, offset,
/// edge mode). A non-square image uses its centre square. Bilinear when a map vertex covers less than ~1.5 pixels,
/// otherwise the average of the pixels under the vertex (so shrinking a big DEM doesn't alias). Thread-safe.
/// </summary>
public sealed class HeightmapSampler
{
    private readonly HeightmapImage _image;
    private readonly ImagePlacement _p;
    private readonly int _side, _ox, _oz;
    private readonly float _cos, _sin, _heightScale;

    /// <param name="vertices">Map vertices per side the sampler is used at (sets the averaging footprint).</param>
    public HeightmapSampler(HeightmapImage image, ImagePlacement placement, int vertices)
    {
        _image = image;
        _p = placement;
        _side = Math.Min(image.Width, image.Height);
        _ox = (image.Width - _side) / 2;
        _oz = (image.Height - _side) / 2;
        float a = placement.Rotation * (MathF.PI / 180f);
        _cos = MathF.Cos(a);
        _sin = MathF.Sin(a);
        _heightScale = (placement.Highest - placement.Lowest) / 65535f;
        float scale = MathF.Max(placement.Scale, 1e-3f);
        // Pixels per map vertex; above 1.5 each vertex averages a box of pixels.
        Step = (_side - 1f) / ((vertices - 1) * scale);
    }

    public float Step { get; }

    /// <summary>Height in metres at map position (<paramref name="u"/>, <paramref name="v"/>), both 0..1 across the map.</summary>
    public float Sample(float u, float v)
    {
        // Map → image: undo the offset, rotation and scale about the map centre.
        float x = u - 0.5f - _p.OffsetX, z = v - 0.5f - _p.OffsetZ;
        float rx = x * _cos + z * _sin, rz = -x * _sin + z * _cos;
        float s = MathF.Max(_p.Scale, 1e-3f);
        float iu = rx / s + 0.5f, iv = rz / s + 0.5f;

        if (_p.Edges == EdgeMode.Fill && (iu < 0f || iu > 1f || iv < 0f || iv > 1f)) return _p.Lowest;
        if (_p.Edges == EdgeMode.Clamp)
        {
            iu = Math.Clamp(iu, 0f, 1f);
            iv = Math.Clamp(iv, 0f, 1f);
        }

        // Clamp/Fill: corners on the corner pixels' centres (matches a plain stretch). Tile/Mirror: the image repeats
        // every `side` pixels, so pixel edges sit on tile edges.
        bool repeats = _p.Edges is EdgeMode.Tile or EdgeMode.Mirror;
        float px = repeats ? iu * _side - 0.5f : iu * (_side - 1);
        float pz = repeats ? iv * _side - 0.5f : iv * (_side - 1);
        float value = Step > 1.5f ? Box(px, pz, Step * 0.5f) : Bilinear(px, pz);
        return _p.Lowest + value * _heightScale;
    }

    private float Pixel(int x, int z) => _image.Pixels[(_oz + Wrap(z)) * _image.Width + _ox + Wrap(x)];

    /// <summary>Folds a pixel index into the image by the edge mode (Clamp and Fill clamp; Fill's outside is handled earlier).</summary>
    private int Wrap(int i)
    {
        switch (_p.Edges)
        {
            case EdgeMode.Tile:
                i %= _side;
                return i < 0 ? i + _side : i;
            case EdgeMode.Mirror:
                int period = 2 * _side;
                i %= period;
                if (i < 0) i += period;
                return i < _side ? i : period - 1 - i;
            default:
                return Math.Clamp(i, 0, _side - 1);
        }
    }

    private float Bilinear(float u, float v)
    {
        int x0 = (int)MathF.Floor(u), z0 = (int)MathF.Floor(v);
        // Keep the identity placement identical to the old importer: the last pixel isn't blended past.
        if (_p.Edges is EdgeMode.Clamp or EdgeMode.Fill)
        {
            x0 = Math.Clamp(x0, 0, _side - 2);
            z0 = Math.Clamp(z0, 0, _side - 2);
        }
        float tx = u - x0, tz = v - z0;
        float p00 = Pixel(x0, z0), p10 = Pixel(x0 + 1, z0), p01 = Pixel(x0, z0 + 1), p11 = Pixel(x0 + 1, z0 + 1);
        float a = p00 + (p10 - p00) * tx;
        float b = p01 + (p11 - p01) * tx;
        return a + (b - a) * tz;
    }

    private float Box(float u, float v, float r)
    {
        int x0 = (int)MathF.Ceiling(u - r), x1 = (int)MathF.Floor(u + r);
        int z0 = (int)MathF.Ceiling(v - r), z1 = (int)MathF.Floor(v + r);
        if (_p.Edges is EdgeMode.Clamp or EdgeMode.Fill)
        {
            x0 = Math.Max(0, x0); z0 = Math.Max(0, z0);
            x1 = Math.Min(_side - 1, x1); z1 = Math.Min(_side - 1, z1);
        }
        // Very small scales can make the box huge: cap it at ~32 samples per side.
        int stride = Math.Max(1, Math.Max(x1 - x0, z1 - z0) / 32);
        double sum = 0;
        int n = 0;
        for (int z = z0; z <= z1; z += stride)
            for (int x = x0; x <= x1; x += stride)
            {
                sum += Pixel(x, z);
                n++;
            }
        return n == 0 ? Bilinear(u, v) : (float)(sum / n);
    }
}
