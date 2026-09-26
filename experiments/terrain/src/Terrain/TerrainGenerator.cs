using System;
using Godot;

namespace CitySim.TerrainSystem;

public readonly record struct TerrainGenSettings(
    int Seed,
    float Frequency,
    int Octaves,
    float HeightScale,
    float Flatness,
    float WarpAmplitude,
    float Gain,
    int SmoothPasses);

/// <summary>Fills a <see cref="HeightMap"/> with procedural noise shaped for city building.</summary>
public static class TerrainGenerator
{
    // Finest octave must span at least this many cells, or it aliases into spikes.
    private const float MinWavelengthCells = 8f;

    public static void Generate(HeightMap map, TerrainGenSettings s)
    {
        // With lacunarity 2, octave k has wavelength 1 / (Frequency * 2^k); drop octaves the grid can't resolve.
        float maxOctaves = 1f + MathF.Log2(1f / (s.Frequency * MinWavelengthCells * map.CellSize));
        int octaves = Math.Clamp(Math.Min(s.Octaves, (int)maxOctaves), 1, s.Octaves);

        var detail = new FastNoiseLite
        {
            Seed = s.Seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = s.Frequency,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = octaves,
            FractalLacunarity = 2f,
            // Gain below 0.5 makes each finer octave contribute less slope than the last.
            FractalGain = s.Gain,
            DomainWarpEnabled = s.WarpAmplitude > 0f,
            DomainWarpType = FastNoiseLite.DomainWarpTypeEnum.SimplexReduced,
            DomainWarpAmplitude = s.WarpAmplitude,
            DomainWarpFrequency = s.Frequency * 0.5f,
        };

        // Low-frequency mask: low values become wide, gentle lowlands to build on,
        // high values keep the full hill detail.
        var mask = new FastNoiseLite
        {
            Seed = s.Seed + 1,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = s.Frequency * 0.35f,
            FractalType = FastNoiseLite.FractalTypeEnum.None,
        };

        for (int z = 0; z < map.Depth; z++)
        {
            float wz = z * map.CellSize;
            for (int x = 0; x < map.Width; x++)
            {
                float wx = x * map.CellSize;
                float n = detail.GetNoise2D(wx, wz) * 0.5f + 0.5f;
                float m = mask.GetNoise2D(wx, wz) * 0.5f + 0.5f;

                float hills = Mathf.SmoothStep(s.Flatness, 1f, m);
                float h01 = Mathf.Lerp(n * 0.15f, n * n * 1.2f, hills);
                map[x, z] = h01 * s.HeightScale;
            }
        }

        map.Smooth(s.SmoothPasses);
    }
}
