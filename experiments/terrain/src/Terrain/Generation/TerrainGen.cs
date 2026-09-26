using System;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.TerrainSystem.Generation;

/// <summary>
/// Builds heights from <see cref="GenSettings"/> in two layers: a base (noise, a placed heightmap image, or flat)
/// and an optional land/sea shape blended over it. Can sample at any resolution: the generator panel's preview
/// uses a small grid over the same world area, so it shows what the full map will look like. Engine-agnostic and
/// multi-threaded (rows run in parallel).
/// </summary>
public static class TerrainGen
{
    // Finest octave must span at least this many cells of the full map, or it aliases into spikes.
    private const float MinWavelengthCells = 8f;

    /// <summary>A full-resolution map (<see cref="GenSettings.Cells"/> + 1 vertices per side).</summary>
    public static HeightMap Create(GenSettings s, CancellationToken ct = default)
    {
        var map = new HeightMap(s.Cells + 1, s.Cells + 1, s.CellSize);
        Fill(map, s, ct);
        return map;
    }

    /// <summary>
    /// A <paramref name="vertices"/>² map covering the same area as the full map (larger cells), for previews.
    /// Skips smoothing, which only matters at full resolution.
    /// </summary>
    public static HeightMap Preview(GenSettings s, int vertices, CancellationToken ct = default)
    {
        var map = new HeightMap(vertices, vertices, s.WorldSize / (vertices - 1));
        Fill(map, s, ct);
        return map;
    }

    /// <summary>Fills <paramref name="map"/>, which spans the settings' world size whatever its vertex count.</summary>
    public static void Fill(HeightMap map, GenSettings s, CancellationToken ct = default)
    {
        int w = map.Width, d = map.Depth;
        float spacing = s.WorldSize / (w - 1);
        var noise = s.Source == TerrainSource.Noise ? new NoiseSource(s.Noise, s.CellSize) : null;
        var image = s.Source == TerrainSource.Heightmap && s.Image is not null ? new HeightmapSampler(s.Image, s.Placement, w) : null;
        var shape = s.Shape.Kind != ShapeKind.None ? new ShapeMask(s.Shape, s.Noise.Seed) : null;
        float seaFloor = s.SeaLevel - s.Shape.SeaDepth;
        float[] heights = new float[w * d];

        Parallel.For(0, d, new ParallelOptions { CancellationToken = ct }, z =>
        {
            float v = z / (d - 1f);
            for (int x = 0; x < w; x++)
            {
                float u = x / (w - 1f);
                float h = noise is not null ? noise.Sample(x * spacing, z * spacing)
                        : image is not null ? image.Sample(u, v)
                        : s.Source == TerrainSource.Heightmap ? s.Placement.Lowest
                        : s.FlatHeight;
                if (shape is not null)
                {
                    // Land fades to sea level first, then drops to the sea floor, so hills flatten into beaches
                    // instead of being cut off at the coast.
                    float m = shape.Sample(u, v);
                    h = Lerp(seaFloor, Lerp(s.SeaLevel, h, m), m);
                }
                heights[z * w + x] = h;
            }
        });
        heights.CopyTo(map.Data);
        ct.ThrowIfCancellationRequested();

        // Smoothing is in cells, so it only applies at full resolution.
        if (MathF.Abs(spacing - s.CellSize) < 1e-3f) map.Smooth(s.SmoothPasses);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// The original CitySim hills: fBm with domain warp, shaped by a low-frequency mask so some areas become wide gentle
    /// lowlands. The warp matches Godot's FastNoiseLite wrapper (a separate progressive-fractal warp noise).
    /// </summary>
    private sealed class NoiseSource
    {
        private readonly NoiseSettings _s;
        private readonly FastNoiseLite _detail, _mask;
        private readonly FastNoiseLite? _warp;

        public NoiseSource(NoiseSettings s, float cellSize)
        {
            _s = s;
            // With lacunarity 2, octave k has wavelength 1 / (Frequency * 2^k); drop octaves the full map can't resolve.
            float maxOctaves = 1f + MathF.Log2(1f / (s.Frequency * MinWavelengthCells * cellSize));
            int octaves = Math.Clamp(Math.Min(s.Octaves, (int)maxOctaves), 1, Math.Max(1, s.Octaves));

            _detail = new FastNoiseLite(s.Seed);
            _detail.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            _detail.SetFrequency(s.Frequency);
            _detail.SetFractalType(FastNoiseLite.FractalType.FBm);
            _detail.SetFractalOctaves(octaves);
            _detail.SetFractalLacunarity(2f);
            // Gain below 0.5 makes each finer octave contribute less slope than the last.
            _detail.SetFractalGain(s.Gain);

            if (s.WarpAmplitude > 0f)
            {
                // Godot's defaults for its domain warp: progressive fractal, 5 octaves, lacunarity 6, gain 0.5.
                _warp = new FastNoiseLite(s.Seed);
                _warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2Reduced);
                _warp.SetDomainWarpAmp(s.WarpAmplitude);
                _warp.SetFrequency(s.Frequency * 0.5f);
                _warp.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);
                _warp.SetFractalOctaves(5);
                _warp.SetFractalLacunarity(6f);
                _warp.SetFractalGain(0.5f);
            }

            // Low-frequency mask: low values become wide, gentle lowlands to build on, high values keep the full hill detail.
            _mask = new FastNoiseLite(s.Seed + 1);
            _mask.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            _mask.SetFrequency(s.Frequency * 0.35f);
            _mask.SetFractalType(FastNoiseLite.FractalType.None);
        }

        public float Sample(float wx, float wz)
        {
            float m = _mask.GetNoise(wx, wz) * 0.5f + 0.5f;
            _warp?.DomainWarp(ref wx, ref wz);
            float n = _detail.GetNoise(wx, wz) * 0.5f + 0.5f;
            float hills = ShapeMask.SmoothStep(_s.Flatness, 1f, m);
            float h01 = n * 0.15f + (n * n * 1.2f - n * 0.15f) * hills;
            return _s.BaseHeight + h01 * _s.HeightScale;
        }
    }
}
