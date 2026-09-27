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
        var shores = new ShoreStyle(s.GentleShores, s.Noise.Seed);
        float flatBase = s.Source == TerrainSource.Heightmap ? s.Placement.Lowest : s.FlatHeight;
        // Smoothing is in cells, so it only applies at full resolution.
        bool smooth = MathF.Abs(spacing - s.CellSize) < 1e-3f && s.SmoothPasses > 0;
        float coarseShare = 0.05f, fillShare = smooth ? 0.85f : 1f;

        // Coarse grid: every g-th vertex, plus one past the far edge so every cell has four corners.
        // Fields: the noise's own, then shore style, the coast's signed distance, and each basin's distance and level.
        int g = CoarseStep(spacing);
        int cw = (w - 1) / g + 2, cd = (d - 1) / g + 2;
        int fields = noise is null ? 0 : NoiseSource.CoarseFields;
        int styleField = fields++;
        int shapeField = shape is not null ? fields++ : -1;
        bool basins = noise is not null && noise.HasBasins;
        int basinField = basins ? fields : -1;
        if (basins) fields += 2;
        var coarse = new float[cw * cd * fields];
        Parallel.For(0, cd, new ParallelOptions { CancellationToken = ct }, cz =>
        {
            for (int cx = 0; cx < cw; cx++)
            {
                var f = coarse.AsSpan((cz * cw + cx) * fields, fields);
                float x = cx * g * spacing, z = cz * g * spacing;
                noise?.Coarse(x, z, f);
                f[styleField] = shores.Sample(x, z);
                if (shape is not null) f[shapeField] = shape.Signed(cx * g / (w - 1f), cz * g / (d - 1f));
            }
        });
        // Basins stay clear of the sea: only ground a little above sea level holds one.
        float basinFloor = shape is not null ? s.SeaLevel + 2f : float.MinValue;
        if (basins) FindBasins(coarse, cw, cd, fields, g * spacing, basinField, noise!, basinFloor, ct, (f, carve) => Surface(f, carve));
        progress?.Invoke(coarseShare);

        // Height before basins: the base (with channels if carve), then the coast.
        float Surface(ReadOnlySpan<float> f, bool carve)
        {
            float h = noise is not null ? noise.Fine(f, f[styleField], carve) : flatBase;
            return shape is not null ? Coast(h, f[shapeField], f[styleField], s) : h;
        }

        int rowsDone = 0;
        Parallel.For(0, d, new ParallelOptions { CancellationToken = ct }, () => new float[fields], (z, _, f) =>
        {
            var row = map.Row(z);
            float v = z / (d - 1f);
            int cz = z / g;
            float tz = (z - cz * g) / (float)g;
            for (int x = 0; x < w; x++)
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
                float h;
                if (image is not null)
                {
                    h = image.Sample(x / (w - 1f), v);
                    if (shape is not null) h = Coast(h, f[shapeField], f[styleField], s);
                }
                else h = Surface(f, true);
                if (basins)
                {
                    // Level floor inside the basin; the bank blends the land back in over a width set by the shore style.
                    float dist = f[basinField], level = f[basinField + 1];
                    float bank = Lerp(12f, 160f, f[styleField]);
                    if (dist < bank) h = MathF.Min(h, level + (h - level) * ShapeMask.SmoothStep(0f, bank, dist));
                }
                row[x] = h;
            }
            int done = Interlocked.Increment(ref rowsDone);
            if (progress is not null && (done & 63) == 0) progress(coarseShare + (fillShare - coarseShare) * done / d);
            return f;
        }, _ => { });
        map.Invalidate();
        ct.ThrowIfCancellationRequested();

        // Uses the map's own spacing, so the preview is cut to the same angle as the full map.
        if (noise is not null) map.LimitSlope(s.Noise.MaxSlope, ct);

        if (smooth) map.Smooth(s.SmoothPasses, ct, progress is null ? null : f => progress(fillShare + (1f - fillShare) * f));
        progress?.Invoke(1f);
    }

    /// <summary>
    /// Blends a height into the sea around a shape. <paramref name="signed"/> is the distance to the coastline in map
    /// units (land positive). Gentle stretches (<paramref name="style"/> near 1) get a wide beach that eases down to sea
    /// level and a shallow shelf under water; steep ones keep the land up to the water and drop quickly to the sea floor.
    /// </summary>
    private static float Coast(float h, float signed, float style, GenSettings s)
    {
        float e = MathF.Max(s.Shape.EdgeWidth, 1e-3f);
        if (signed >= 0f)
        {
            float beach = e * Lerp(0.08f, 1.2f, style);
            return s.SeaLevel + (h - s.SeaLevel) * ShapeMask.SmoothStep(0f, beach, signed);
        }
        float shelf = e * Lerp(0.25f, 1.5f, style);
        float t = Math.Clamp(1f + signed / shelf, 0f, 1f);
        // Steep: steepest at the waterline (t²). Gentle: flat at the waterline, so the beach runs out into the sea.
        float p = Lerp(t * t, t * t * (3f - 2f * t), style);
        return s.SeaLevel - s.Shape.SeaDepth * (1f - p);
    }

    /// <summary>
    /// Finds the basins on the coarse grid and gives each a level: connected lowland nodes where the basin noise is
    /// high, lowered to the lowest ground in them minus <see cref="NoiseSettings.BasinDepth"/>. Then writes, for every
    /// node, the distance to the nearest basin (metres) and that basin's level, into fields <paramref name="field"/>
    /// and +1. Runs on the coarse grid, so it's cheap even at 28 km.
    /// </summary>
    private static void FindBasins(float[] coarse, int cw, int cd, int fields, float step, int field, NoiseSource noise,
        float minGround, CancellationToken ct, Func<float[], bool, float> surface)
    {
        int n = cw * cd;
        var ground = new float[n];
        var core = new bool[n];
        Parallel.For(0, cd, new ParallelOptions { CancellationToken = ct }, () => new float[fields], (cz, _, f) =>
        {
            for (int cx = 0; cx < cw; cx++)
            {
                int i = cz * cw + cx;
                coarse.AsSpan(i * fields, fields).CopyTo(f);
                ground[i] = surface(f, false);
                core[i] = noise.IsBasinCore(f) && ground[i] > minGround;
            }
            return f;
        }, _ => { });

        // Label connected cores (4-neighbour flood fill) and take each one's lowest ground.
        var label = new int[n];
        var levels = new System.Collections.Generic.List<float> { 0f };
        var stack = new System.Collections.Generic.Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (!core[i] || label[i] != 0) continue;
            int id = levels.Count;
            float low = float.MaxValue;
            label[i] = id;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int j = stack.Pop();
                low = MathF.Min(low, ground[j]);
                int x = j % cw;
                if (x > 0) Visit(j - 1);
                if (x < cw - 1) Visit(j + 1);
                if (j >= cw) Visit(j - cw);
                if (j < n - cw) Visit(j + cw);
            }
            levels.Add(low - noise.BasinDepth);

            void Visit(int k)
            {
                if (core[k] && label[k] == 0) { label[k] = id; stack.Push(k); }
            }
        }

        // Distance to the nearest basin, carrying its level: two-pass 8-neighbour chamfer.
        var dist = new float[n];
        var level = new float[n];
        for (int i = 0; i < n; i++)
        {
            dist[i] = label[i] != 0 ? 0f : 1e9f;
            level[i] = levels[label[i]];
        }
        float a = step, b = step * MathF.Sqrt(2f);
        for (int z = 0; z < cd; z++)
            for (int x = 0; x < cw; x++)
            {
                int i = z * cw + x;
                if (x > 0) Relax(i, i - 1, a);
                if (z > 0)
                {
                    Relax(i, i - cw, a);
                    if (x > 0) Relax(i, i - cw - 1, b);
                    if (x < cw - 1) Relax(i, i - cw + 1, b);
                }
            }
        for (int z = cd - 1; z >= 0; z--)
            for (int x = cw - 1; x >= 0; x--)
            {
                int i = z * cw + x;
                if (x < cw - 1) Relax(i, i + 1, a);
                if (z < cd - 1)
                {
                    Relax(i, i + cw, a);
                    if (x < cw - 1) Relax(i, i + cw + 1, b);
                    if (x > 0) Relax(i, i + cw - 1, b);
                }
            }
        for (int i = 0; i < n; i++)
        {
            coarse[i * fields + field] = dist[i];
            coarse[i * fields + field + 1] = level[i];
        }

        void Relax(int i, int from, float cost)
        {
            if (dist[from] + cost < dist[i]) { dist[i] = dist[from] + cost; level[i] = level[from]; }
        }
    }

    /// <summary>Coarse grid step in vertices: about <see cref="CoarseSpacing"/> metres, or every vertex on coarse maps.</summary>
    private static int CoarseStep(float spacing) => Math.Max(1, (int)(CoarseSpacing / spacing));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Where shores (sea, basins, channels) are gentle beaches (1) or steep banks (0): low-frequency noise in metres,
    /// so a coast changes character every kilometre or so. <see cref="GenSettings.GentleShores"/> sets the balance.
    /// </summary>
    private sealed class ShoreStyle
    {
        private readonly FastNoiseLite _noise;
        private readonly float _bias;

        public ShoreStyle(float gentleShare, int seed)
        {
            _noise = new FastNoiseLite(seed + 5);
            _noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            _noise.SetFrequency(1f / 1200f);
            _noise.SetFractalType(FastNoiseLite.FractalType.FBm);
            _noise.SetFractalOctaves(2);
            // Noise sits mostly within ±0.5: share 0 → almost all steep, 1 → almost all gentle.
            _bias = (Math.Clamp(gentleShare, 0f, 1f) - 0.5f) * 1.2f;
        }

        public float Sample(float wx, float wz) => ShapeMask.SmoothStep(-0.15f, 0.15f, _noise.GetNoise(wx, wz) + _bias);
    }

    /// <summary>
    /// The original CitySim hills: fBm with domain warp, shaped by a low-frequency mask so some areas become wide gentle
    /// lowlands. The warp matches Godot's FastNoiseLite wrapper (a separate progressive-fractal warp noise), minus the
    /// octaves too fine for the map's cells (they only added sub-cell jitter that smoothing removed).
    /// Large maps add a region layer (see <see cref="NoiseSettings.RegionSize"/>): mountain ranges, highlands and wide
    /// plains kilometres across, so the hills don't read as the same bumps repeated over the whole map.
    /// </summary>
    private sealed class NoiseSource
    {
        /// <summary>Values <see cref="Coarse"/> writes: warped x and z, lowland mask, region, basin noise.</summary>
        public const int CoarseFields = 5;
        private const int WarpOctaves = 5, WarpLacunarity = 6;

        private readonly NoiseSettings _s;
        private readonly FastNoiseLite _detail, _mask;
        private readonly FastNoiseLite? _warp, _region, _channels, _basins;
        private readonly float _regionStrength, _channelHalfWidth, _basinThreshold;

        public bool HasBasins => _basins is not null;
        public float BasinDepth => _s.BasinDepth;
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

            if (s.ChannelDepth > 0f && s.ChannelSpacing > 0f)
            {
                // Channels follow the zero lines of fBm: thin, winding lines that fork where the finer octaves cross.
                _channels = new FastNoiseLite(s.Seed + 3);
                _channels.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
                _channels.SetFrequency(1f / s.ChannelSpacing);
                _channels.SetFractalType(FastNoiseLite.FractalType.FBm);
                _channels.SetFractalOctaves(3);
                _channels.SetFractalGain(0.5f);
                // The noise changes by roughly 2 * frequency per metre near a zero line, so metres * 2 / spacing is noise units.
                _channelHalfWidth = s.ChannelWidth / s.ChannelSpacing;
            }
            if (s.BasinAmount > 0f && s.BasinSize > 0f)
            {
                _basins = new FastNoiseLite(s.Seed + 4);
                _basins.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
                _basins.SetFrequency(1f / s.BasinSize);
                _basins.SetFractalType(FastNoiseLite.FractalType.FBm);
                _basins.SetFractalOctaves(2);
                _basins.SetFractalGain(0.4f);
                // Noise rarely leaves ±0.6: amount 0 puts the threshold above that (no basins), 1 near the middle.
                _basinThreshold = 0.6f - Math.Clamp(s.BasinAmount, 0f, 1f) * 0.6f;
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
            f[4] = _basins?.GetNoise(wx, wz) ?? 0f;
            _warp?.DomainWarpProgressiveOctaves(ref wx, ref wz, 0, _coarseWarpOctaves);
            f[0] = wx;
            f[1] = wz;
        }

        /// <summary>The height from interpolated coarse values.</summary>
        public float Fine(ReadOnlySpan<float> f, float style, bool carveChannels)
        {
            float wx = f[0], wz = f[1];
            var (flatness, scale, uplift) = Region(f);
            _warp?.DomainWarpProgressiveOctaves(ref wx, ref wz, _coarseWarpOctaves, _fineWarpOctaves);
            float n = _detail.GetNoise(wx, wz) * 0.5f + 0.5f;
            float hills = Hills(f[2], flatness);
            float h01 = n * 0.1f + (n * n * 1.2f - n * 0.1f) * hills;
            float h = _s.BaseHeight + uplift + h01 * scale;
            // Channels cut the lowlands and fade out going up into the hills. A flat bed, then banks that are
            // steep or gentle with the shore style.
            float low = 1f - hills;
            if (carveChannels && _channels is not null && low > 1e-3f)
            {
                float c = MathF.Abs(_channels.GetNoise(wx, wz));
                float bank = Lerp(2f, 25f, style) * 2f / _s.ChannelSpacing;
                h -= _s.ChannelDepth * low * (1f - ShapeMask.SmoothStep(_channelHalfWidth * 0.4f, _channelHalfWidth + bank, c));
            }
            return h;
        }

        /// <summary>A basin sits where the basin noise is high, on ground that's mostly lowland.</summary>
        public bool IsBasinCore(ReadOnlySpan<float> f) =>
            _basins is not null && f[4] > _basinThreshold && Hills(f[2], Region(f).Flatness) < 0.5f;

        /// <summary>Hills weight 0..1 from the lowland mask. Squared: hills rise out of the lowlands more slowly, so more of
        /// the map stays flat enough to build on.</summary>
        private static float Hills(float mask, float flatness)
        {
            float hills = ShapeMask.SmoothStep(flatness, 1f, mask);
            return hills * hills;
        }

        /// <summary>Flatness, height scale and uplift after the region layer (plains vs. mountain ranges).</summary>
        private (float Flatness, float Scale, float Uplift) Region(ReadOnlySpan<float> f)
        {
            float flatness = _s.Flatness, scale = _s.HeightScale, uplift = 0f;
            if (_region is not null)
            {
                float r = f[3], k = _regionStrength;
                flatness = Math.Clamp(flatness + (0.5f - r) * 0.9f * k, 0f, 0.95f);
                scale *= 1f + k * (ShapeMask.SmoothStep(0.25f, 0.85f, r) * 2.2f - 0.6f);
                uplift = k * _s.HeightScale * 0.9f * ShapeMask.SmoothStep(0.3f, 0.9f, r);
            }
            return (flatness, scale, uplift);
        }
    }
}
