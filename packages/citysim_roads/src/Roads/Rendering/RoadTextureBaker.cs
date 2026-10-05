using System;
using System.Threading.Tasks;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// <c>--bake-road-textures</c>: writes the tiling noise textures the road shaders sample
/// (<c>content/roads/textures/</c>). Run once after changing this file; the PNGs are checked in, so the game never
/// builds noise at load. Every texture tiles exactly (each noise wraps on its lattice). The shaders read them in world
/// metres, so they're seamless across segments and junctions.
/// <list type="bullet">
/// <item><c>road_detail.png</c> (one tile = 4 m on asphalt): R stone height, G stone brightness, B fine binder noise,
/// A paint breakup.</item>
/// <item><c>road_detail_normal.png</c>: the normal map of R (stones), same tiling.</item>
/// <item><c>road_macro.png</c> (one tile = 16 m): R distance to the nearest crack (jagged cells, 0 on a crack, 1 at
/// 10 cm or more), G where cracking happens, B stains and patches, A oil spots.</item>
/// </list>
/// </summary>
public static class RoadTextureBaker
{
    public const string Folder = "res://addons/citysim_roads/content/roads/textures";
    private const int Size = 1024;

    public static void Bake()
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Folder));
        var detail = new float[4][];
        var stones = Worley(Size, 140, seed: 11);   // ~3 cm stones over 4 m
        var fine = Fbm(Size, 8, 5, seed: 12);
        var paint = Fbm(Size, 24, 4, seed: 13);
        detail[0] = new float[Size * Size];
        detail[1] = new float[Size * Size];
        detail[2] = fine;
        detail[3] = paint;
        var grit = Fbm(Size, 256, 2, seed: 14);
        for (int i = 0; i < Size * Size; i++)
        {
            // A stone is a dome inside its cell; the binder between them sits low.
            float dome = Math.Clamp(1f - stones.F1[i] * 1.6f, 0, 1);
            detail[0][i] = Math.Clamp(MathF.Sqrt(dome) * 0.85f + grit[i] * 0.15f, 0, 1);
            detail[1][i] = dome > 0.05f ? stones.Id[i] : 0f;
        }
        Save("road_detail.png", detail);
        SaveNormal("road_detail_normal.png", detail[0], strength: 3f);

        var macro = new float[4][];
        // Cracks: cell edges (F2 − F1) of a domain-warped Worley, so the edges wander like real cracks.
        // A broad warp bends the cells; a fine one (~10 cm) makes their edges zig-zag.
        var warpX = Fbm(Size, 6, 4, seed: 21);
        var warpY = Fbm(Size, 6, 4, seed: 22);
        var jitterX = Fbm(Size, 96, 3, seed: 28);
        var jitterY = Fbm(Size, 96, 3, seed: 29);
        for (int i = 0; i < Size * Size; i++)
        {
            warpX[i] += (jitterX[i] - 0.5f) * 0.12f;
            warpY[i] += (jitterY[i] - 0.5f) * 0.12f;
        }
        var cracks = Worley(Size, 11, seed: 23, warpX, warpY, warp: 0.06f); // ~1.5 m cells over 16 m
        macro[0] = new float[Size * Size];
        const float metresPerUnit = 16f / 11f; // Worley distances are in cells
        for (int i = 0; i < Size * Size; i++)
            macro[0][i] = Math.Clamp((cracks.F2[i] - cracks.F1[i]) * metresPerUnit / 0.10f, 0, 1);
        macro[1] = Fbm(Size, 3, 4, seed: 24);
        macro[2] = Fbm(Size, 2, 5, seed: 25);
        var spots = Worley(Size, 24, seed: 26);
        var spotNoise = Fbm(Size, 32, 3, seed: 27);
        macro[3] = new float[Size * Size];
        for (int i = 0; i < Size * Size; i++)
            macro[3][i] = Math.Clamp((0.35f - spots.F1[i]) * 4f * (spots.Id[i] > 0.6f ? 1 : 0) + (spotNoise[i] - 0.5f) * 0.4f, 0, 1);
        Save("road_macro.png", macro);
        GD.Print($"Road textures: baked into {Folder}");
    }

    private static void Save(string name, float[][] channels)
    {
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var data = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++)
            for (int c = 0; c < 4; c++) data[i * 4 + c] = (byte)Math.Clamp(channels[c][i] * 255f + 0.5f, 0, 255);
        img.SetData(Size, Size, false, Image.Format.Rgba8, data);
        img.SavePng($"{Folder}/{name}");
    }

    private static void SaveNormal(string name, float[] height, float strength)
    {
        var data = new byte[Size * Size * 4];
        float H(int x, int y) => height[((y + Size) % Size) * Size + (x + Size) % Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = (H(x + 1, y) - H(x - 1, y)) * strength, dy = (H(x, y + 1) - H(x, y - 1)) * strength;
                var n = new Vector3(-dx, -dy, 1).Normalized();
                int i = (y * Size + x) * 4;
                data[i] = (byte)((n.X * 0.5f + 0.5f) * 255);
                data[i + 1] = (byte)((n.Y * 0.5f + 0.5f) * 255);
                data[i + 2] = (byte)((n.Z * 0.5f + 0.5f) * 255);
                data[i + 3] = 255;
            }
        var img = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, data);
        img.SavePng($"{Folder}/{name}");
    }

    private sealed record WorleyResult(float[] F1, float[] F2, float[] Id);

    /// <summary>Tiling Worley noise with <paramref name="cells"/> cells across: distances to the nearest and second
    /// nearest feature point, in cells, and a random 0..1 per nearest cell. Optional periodic domain warp (in tiles).</summary>
    private static WorleyResult Worley(int size, int cells, int seed, float[]? warpX = null, float[]? warpY = null, float warp = 0)
    {
        var px = new float[cells * cells];
        var py = new float[cells * cells];
        var id = new float[cells * cells];
        var rng = new Random(seed);
        for (int i = 0; i < px.Length; i++) { px[i] = (float)rng.NextDouble(); py[i] = (float)rng.NextDouble(); id[i] = (float)rng.NextDouble(); }
        var f1 = new float[size * size];
        var f2 = new float[size * size];
        var ids = new float[size * size];
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                if (warpX is not null && warpY is not null) { u += (warpX[i] - 0.5f) * warp; v += (warpY[i] - 0.5f) * warp; }
                float fx = u * cells, fy = v * cells;
                int cx = (int)MathF.Floor(fx), cy = (int)MathF.Floor(fy);
                float d1 = float.MaxValue, d2 = float.MaxValue, best = 0;
                for (int oy = -2; oy <= 2; oy++)
                    for (int ox = -2; ox <= 2; ox++)
                    {
                        int gx = cx + ox, gy = cy + oy;
                        int k = Mod(gy, cells) * cells + Mod(gx, cells);
                        float dx = gx + px[k] - fx, dy = gy + py[k] - fy;
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d < d1) { d2 = d1; d1 = d; best = id[k]; }
                        else if (d < d2) d2 = d;
                    }
                f1[i] = d1;
                f2[i] = d2;
                ids[i] = best;
            }
        });
        return new WorleyResult(f1, f2, ids);
    }

    /// <summary>Tiling value-noise fBm, 0..1: <paramref name="period"/> lattice cells across the first octave, doubling.</summary>
    private static float[] Fbm(int size, int period, int octaves, int seed)
    {
        var result = new float[size * size];
        float amp = 0.5f, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            int p = period << o;
            var lattice = new float[p * p];
            var rng = new Random(seed * 131 + o);
            for (int i = 0; i < lattice.Length; i++) lattice[i] = (float)rng.NextDouble();
            float a = amp;
            Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = (x + 0.5f) / size * p, fy = (y + 0.5f) / size * p;
                    int x0 = (int)fx, y0 = (int)fy;
                    float tx = Smooth(fx - x0), ty = Smooth(fy - y0);
                    float L(int gx, int gy) => lattice[Mod(gy, p) * p + Mod(gx, p)];
                    float top = Lerp(L(x0, y0), L(x0 + 1, y0), tx), bottom = Lerp(L(x0, y0 + 1), L(x0 + 1, y0 + 1), tx);
                    result[y * size + x] += Lerp(top, bottom, ty) * a;
                }
            });
            total += amp;
            amp *= 0.5f;
        }
        // Stretch to the full 0..1 range so the shaders' thresholds mean the same at every octave count.
        float min = float.MaxValue, max = float.MinValue;
        foreach (float f in result) { min = MathF.Min(min, f); max = MathF.Max(max, f); }
        for (int i = 0; i < result.Length; i++) result[i] = (result[i] - min) / MathF.Max(1e-6f, max - min);
        return result;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
