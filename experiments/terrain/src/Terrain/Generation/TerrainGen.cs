using System;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.TerrainSystem.Generation;

/// <summary>
/// Builds heights from <see cref="GenSettings"/> in two layers: a base (noise, a placed heightmap image, or flat)
/// and an optional land/sea shape blended over it. Can sample at any resolution: the generator panel's preview
/// uses a small grid over the same world area, so it shows what the full map will look like. Engine-agnostic and
/// multi-threaded (rows run in parallel).
///
/// Everything that varies slowly (the lowland mask, regions, the land/sea shape and the broad octaves of the domain
/// warp) is sampled on a coarse grid about <see cref="CoarseSpacing"/> apart and interpolated; only the fine warp
/// octave and the hill detail run per vertex. That keeps a 28 km (8193²) map to a few seconds.
/// </summary>
public static class TerrainGen
{
    // Finest octave must span at least this many cells of the full map, or it aliases into spikes.
    private const float MinWavelengthCells = 8f;

    /// <summary>Target spacing of the coarse grid, in metres.</summary>
    private const float CoarseSpacing = 14f;

    /// <summary>A warp octave goes on the coarse grid when its wavelength spans at least this many coarse cells.</summary>
    private const float CoarseWavelengthCells = 12f;

    /// <summary>
    /// A full-resolution map (<see cref="GenSettings.Cells"/> + 1 vertices per side). <paramref name="progress"/> gets 0..1
    /// from worker threads.
    /// </summary>
    public static HeightMap Create(GenSettings s, CancellationToken ct = default, Action<float>? progress = null)
    {
        var map = new HeightMap(s.Cells + 1, s.Cells + 1, s.CellSize);
        Fill(map, s, ct, progress);
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

    /// <summary>
    /// How much of <see cref="NoiseSettings.RegionStrength"/> a map of <paramref name="worldSize"/> metres gets: none up to
    /// 4 km (the presets are tuned there and stay as they were), all of it from 16 km.
    /// </summary>
    public static float RegionWeight(float worldSize) => Math.Clamp((worldSize - 4000f) / 12000f, 0f, 1f);

    /// <summary>
    /// Fills <paramref name="map"/>, which spans the settings' world size whatever its vertex count. Rows are written
    /// straight into the map (no full-size temporary).
    /// </summary>
    public static void Fill(HeightMap map, GenSettings s, CancellationToken ct = default, Action<float>? progress = null)
    {
        int w = map.Width, d = map.Depth;
        float spacing = s.WorldSize / (w - 1);
        var noise = s.Source == TerrainSource.Noise ? new NoiseSource(s.Noise, s.CellSize, s.WorldSize, spacing * CoarseStep(spacing)) : null;
        var image = s.Source == TerrainSource.Heightmap && s.Image is not null ? new HeightmapSampler(s.Image, s.Placement, w) : null;
        var shape = s.Shape.Kind != ShapeKind.None ? new ShapeMask(s.Shape, s.Noise.Seed, s.WorldSize) : null;
        float seaFloor = s.SeaLevel - s.Shape.SeaDepth;
        float flatBase = s.Source == TerrainSource.Heightmap ? s.Placement.Lowest : s.FlatHeight;
        // Smoothing is in cells, so it only applies at full resolution.
        bool smooth = MathF.Abs(spacing - s.CellSize) < 1e-3f && s.SmoothPasses > 0;
        float coarseShare = 0.05f, fillShare = smooth ? 0.85f : 1f;

        // Coarse grid: every g-th vertex, plus one past the far edge so every cell has four corners.
        int g = CoarseStep(spacing);
        int cw = (w - 1) / g + 2, cd = (d - 1) / g + 2;
        int fields = noise is null ? 0 : NoiseSource.CoarseFields;
        int shapeField = fields;
        if (shape is not null) fields++;
        float[]? coarse = fields > 0 ? new float[cw * cd * fields] : null;
        if (coarse is not null)
        {
            Parallel.For(0, cd, new ParallelOptions { CancellationToken = ct }, cz =>
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    var f = coarse.AsSpan((cz * cw + cx) * fields, fields);
                    float x = cx * g, z = cz * g;
                    noise?.Coarse(x * spacing, z * spacing, f);
                    if (shape is not null) f[shapeField] = shape.Sample(x / (w - 1f), z / (d - 1f));
                }
            });
            progress?.Invoke(coarseShare);
        }

        int rowsDone = 0;
        Parallel.For(0, d, new ParallelOptions { CancellationToken = ct }, () => new float[Math.Max(fields, 1)], (z, _, f) =>
        {
            var row = map.Row(z);
            float v = z / (d - 1f);
            int cz = z / g;
            float tz = (z - cz * g) / (float)g;
            for (int x = 0; x < w; x++)
            {
                if (coarse is not null)
                {
                    int cx = x / g;
                    float tx = (x - cx * g) / (float)g;
                    int i00 = (cz * cw + cx) * fields, i10 = i00 + fields, i01 = i00 + cw * fields, i11 = i01 + fields;
                    for (int k = 0; k < fields; k++)
                    {
                        float a = coarse[i00 + k] + (coarse[i10 + k] - coarse[i00 + k]) * tx;
                        float b = coarse[i01 + k] + (coarse[i11 + k] - coarse[i01 + k]) * tx;
                        f[k] = a + (b - a) * tz;
                    }
                }
                float u = x / (w - 1f);
                float h = noise is not null ? noise.Fine(f)
                        : image is not null ? image.Sample(u, v)
                        : flatBase;
                if (shape is not null)
                {
                    // Land fades to sea level first, then drops to the sea floor, so hills flatten into beaches
                    // instead of being cut off at the coast.
                    float m = f[shapeField];
                    h = Lerp(seaFloor, Lerp(s.SeaLevel, h, m), m);
                }
                row[x] = h;
            }
            int done = Interlocked.Increment(ref rowsDone);
            if (progress is not null && (done & 63) == 0) progress(coarseShare + (fillShare - coarseShare) * done / d);
            return f;
        }, _ => { });
        map.Invalidate();
        ct.ThrowIfCancellationRequested();

        if (smooth) map.Smooth(s.SmoothPasses, ct, progress is null ? null : f => progress(fillShare + (1f - fillShare) * f));
        progress?.Invoke(1f);
    }

    /// <summary>Coarse grid step in vertices: about <see cref="CoarseSpacing"/> metres, or every vertex on coarse maps.</summary>
    private static int CoarseStep(float spacing) => Math.Max(1, (int)(CoarseSpacing / spacing));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// The original CitySim hills: fBm with domain warp, shaped by a low-frequency mask so some areas become wide gentle
    /// lowlands. The warp matches Godot's FastNoiseLite wrapper (a separate progressive-fractal warp noise), minus the
    /// octaves too fine for the map's cells (they only added sub-cell jitter that smoothing removed).
    /// Large maps add a region layer (see <see cref="NoiseSettings.RegionSize"/>): mountain ranges, highlands and wide
    /// plains kilometres across, so the hills don't read as the same bumps repeated over the whole map.
    /// </summary>
    private sealed class NoiseSource
    {
        /// <summary>Values <see cref="Coarse"/> writes: warped x and z, lowland mask, region.</summary>
        public const int CoarseFields = 4;
        private const int WarpOctaves = 5, WarpLacunarity = 6;

        private readonly NoiseSettings _s;
        private readonly FastNoiseLite _detail, _mask;
        private readonly FastNoiseLite? _warp, _region;
        private readonly float _regionStrength;
        private readonly int _coarseWarpOctaves, _fineWarpOctaves;

        /// <param name="coarseSpacing">Spacing of the coarse grid in metres; decides which warp octaves run on it.</param>
        public NoiseSource(NoiseSettings s, float cellSize, float worldSize, float coarseSpacing)
        {
            _s = s;
            _regionStrength = s.RegionStrength * RegionWeight(worldSize);
            if (_regionStrength > 0f && s.RegionSize > 0f)
            {
                _region = new FastNoiseLite(s.Seed + 2);
                _region.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
                _region.SetFrequency(1f / s.RegionSize);
                _region.SetFractalType(FastNoiseLite.FractalType.FBm);
                _region.SetFractalOctaves(3);
                _region.SetFractalGain(0.5f);
            }
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
                float warpFreq = s.Frequency * 0.5f;
                _warp = new FastNoiseLite(s.Seed);
                _warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2Reduced);
                _warp.SetDomainWarpAmp(s.WarpAmplitude);
                _warp.SetFrequency(warpFreq);
                _warp.SetFractalType(FastNoiseLite.FractalType.DomainWarpProgressive);
                _warp.SetFractalOctaves(WarpOctaves);
                _warp.SetFractalLacunarity(WarpLacunarity);
                _warp.SetFractalGain(0.5f);
                // Octave k has wavelength 1 / (warpFreq * 6^k).
                float Wavelength(int k) => 1f / (warpFreq * MathF.Pow(WarpLacunarity, k));
                while (_coarseWarpOctaves < WarpOctaves && Wavelength(_coarseWarpOctaves) >= CoarseWavelengthCells * coarseSpacing)
                    _coarseWarpOctaves++;
                int resolvable = _coarseWarpOctaves;
                while (resolvable < WarpOctaves && Wavelength(resolvable) >= MinWavelengthCells * cellSize)
                    resolvable++;
                _fineWarpOctaves = resolvable - _coarseWarpOctaves;
            }

            // Low-frequency mask: low values become wide, gentle lowlands to build on, high values keep the full hill detail.
            _mask = new FastNoiseLite(s.Seed + 1);
            _mask.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            _mask.SetFrequency(s.Frequency * 0.35f);
            _mask.SetFractalType(FastNoiseLite.FractalType.None);
        }

        /// <summary>The slowly varying part at world position (wx, wz), into f[0..<see cref="CoarseFields"/>).</summary>
        public void Coarse(float wx, float wz, Span<float> f)
        {
            f[2] = _mask.GetNoise(wx, wz) * 0.5f + 0.5f;
            // r: 0 = plains (flatter, lower hills), 1 = mountain ranges (rugged, taller, raised).
            f[3] = _region is null ? 0.5f : Math.Clamp(_region.GetNoise(wx, wz) * 0.75f + 0.5f, 0f, 1f);
            _warp?.DomainWarpProgressiveOctaves(ref wx, ref wz, 0, _coarseWarpOctaves);
            f[0] = wx;
            f[1] = wz;
        }

        /// <summary>The height from interpolated coarse values.</summary>
        public float Fine(ReadOnlySpan<float> f)
        {
            float wx = f[0], wz = f[1], m = f[2];
            float flatness = _s.Flatness, scale = _s.HeightScale, uplift = 0f;
            if (_region is not null)
            {
                float r = f[3], k = _regionStrength;
                flatness = Math.Clamp(flatness + (0.5f - r) * 0.9f * k, 0f, 0.95f);
                scale *= 1f + k * (ShapeMask.SmoothStep(0.25f, 0.85f, r) * 2.2f - 0.6f);
                uplift = k * _s.HeightScale * 0.9f * ShapeMask.SmoothStep(0.3f, 0.9f, r);
            }
            _warp?.DomainWarpProgressiveOctaves(ref wx, ref wz, _coarseWarpOctaves, _fineWarpOctaves);
            float n = _detail.GetNoise(wx, wz) * 0.5f + 0.5f;
            float hills = ShapeMask.SmoothStep(flatness, 1f, m);
            float h01 = n * 0.15f + (n * n * 1.2f - n * 0.15f) * hills;
            return _s.BaseHeight + uplift + h01 * scale;
        }
    }
}
