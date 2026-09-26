using System;
using System.IO;
using Godot;
using CitySim.TerrainSystem;
using CitySim.Tools;

namespace CitySim.Debug;

/// <summary>
/// Packs the downloaded layer textures (assets/textures/terrain/&lt;layer&gt;/*_Color|NormalGL|Displacement.jpg)
/// into two vertical strips, one slice per layer in <see cref="TerrainLayers"/> order. Godot then imports
/// them as compressed Texture2DArrays (see the committed .import files):
///   terrain_albedo_height.png  RGB = colour normalised to a neutral grey average (the shader tints it), A = height
///   terrain_normal.png         RGB = OpenGL-convention normal
/// Run with: Godot --headless --path . -- --bake-terrain-textures  (tools/fetch_textures.sh does this).
/// </summary>
public static class TextureBaker
{
    public const int SliceSize = 1024;
    public const string Dir = "res://assets/textures/terrain";

    /// <summary>Average brightness (0-1, sRGB) each layer's colour is normalised to. The shader divides by the same value.</summary>
    public const float NeutralGrey = 0.4f;

    public static bool Bake()
    {
        string dir = ProjectSettings.GlobalizePath(Dir);
        int n = TerrainLayers.All.Length;
        int slice = SliceSize * SliceSize;
        var albedo = new byte[slice * n * 4];
        var normal = new byte[slice * n * 3];

        foreach (var layer in TerrainLayers.All)
        {
            string folder = Path.Combine(dir, layer.Name);
            string? color = Find(folder, "_Color.jpg");
            string? nrm = Find(folder, "_NormalGL.jpg");
            string? disp = Find(folder, "_Displacement.jpg");
            if (color is null || nrm is null || disp is null)
            {
                GD.PushError($"TextureBaker: missing textures in {folder}. Run tools/fetch_textures.sh first.");
                return false;
            }

            byte[] c = Load(color, Image.Format.Rgb8);
            byte[] h = Load(disp, Image.Format.L8);
            byte[] nm = Load(nrm, Image.Format.Rgb8);

            // Scale each colour channel so its average is neutral grey: the texture keeps its light/dark
            // and relative colour detail, and the shader's per-layer tint decides the actual colour.
            var mean = new double[3];
            for (int i = 0; i < slice; i++)
                for (int ch = 0; ch < 3; ch++)
                    mean[ch] += c[i * 3 + ch];
            var scale = new float[3];
            for (int ch = 0; ch < 3; ch++)
                scale[ch] = NeutralGrey * 255f * slice / (float)Math.Max(mean[ch], 1.0);

            // Stretch height to the full 0-1 range (1st to 99th percentile), so every layer blends alike.
            var hist = new int[256];
            foreach (byte v in h) hist[v]++;
            int lo = Percentile(hist, slice, 0.01), hi = Math.Max(Percentile(hist, slice, 0.99), lo + 1);

            int baseA = layer.Index * slice * 4;
            for (int i = 0; i < slice; i++)
            {
                for (int ch = 0; ch < 3; ch++)
                    albedo[baseA + i * 4 + ch] = (byte)Math.Clamp((int)(c[i * 3 + ch] * scale[ch] + 0.5f), 0, 255);
                // Alpha never reaches 0: the PNG writer drops the colour of fully transparent pixels.
                albedo[baseA + i * 4 + 3] = (byte)Math.Clamp((h[i] - lo) * 255 / (hi - lo), 1, 255);
            }
            Array.Copy(nm, 0, normal, layer.Index * slice * 3, slice * 3);
            GD.Print($"TextureBaker: {layer.Name} <- {Path.GetFileName(color)} (height {lo}-{hi})");
        }

        var a = Image.CreateFromData(SliceSize, SliceSize * n, false, Image.Format.Rgba8, albedo);
        var nImg = Image.CreateFromData(SliceSize, SliceSize * n, false, Image.Format.Rgb8, normal);
        var e1 = a.SavePng(Path.Combine(dir, "terrain_albedo_height.png"));
        var e2 = nImg.SavePng(Path.Combine(dir, "terrain_normal.png"));
        GD.Print($"TextureBaker: wrote {n} slices of {SliceSize}² to {dir} ({e1}, {e2})");
        return e1 == Error.Ok && e2 == Error.Ok;
    }

    /// <summary>
    /// Bakes the brush masks listed in <see cref="BrushLibrary.All"/> to 256² greyscale PNGs in
    /// <see cref="BrushLibrary.Dir"/>. Stamps are read from assets/brushes/src (downloaded by
    /// tools/fetch_brushes.sh), cropped to their content and normalised; the paint alphas are generated
    /// from noise. Every mask gets a radial edge fade, so no brush shows its square or circular border.
    /// Run with: Godot --headless --path . -- --bake-brushes
    /// </summary>
    public static bool BakeBrushes()
    {
        string dir = ProjectSettings.GlobalizePath(BrushLibrary.Dir);
        const int n = BrushLibrary.MaskSize;
        bool ok = true;
        foreach (var e in BrushLibrary.All)
        {
            float[]? v = e.Source switch
            {
                BrushLibrary.Source.Stamp => LoadStamp(Path.Combine(dir, "src", e.StampFile!), n),
                BrushLibrary.Source.Splatter => Splatter(n),
                BrushLibrary.Source.Noise => NoisePatch(n),
                BrushLibrary.Source.Streaks => Streaks(n),
                _ => null,
            };
            if (v is null)
            {
                if (e.Source == BrushLibrary.Source.Stamp)
                {
                    GD.PushError($"TextureBaker: missing stamp {e.StampFile}. Run tools/fetch_brushes.sh first.");
                    ok = false;
                }
                continue;
            }

            Normalise(v);
            var bytes = new byte[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    // Fade the outer 30% of the radius to 0, and cut everything outside the circle.
                    float dx = (x + 0.5f) / n * 2f - 1f, dz = (z + 0.5f) / n * 2f - 1f;
                    float fade = Math.Clamp((1f - MathF.Sqrt(dx * dx + dz * dz)) / 0.3f, 0f, 1f);
                    fade = fade * fade * (3f - 2f * fade);
                    bytes[z * n + x] = (byte)Math.Clamp((int)(v[z * n + x] * fade * 255f + 0.5f), 0, 255);
                }
            var err = Image.CreateFromData(n, n, false, Image.Format.L8, bytes).SavePng(Path.Combine(dir, e.Id + ".png"));
            GD.Print($"TextureBaker: brush {e.Id} ({e.Source}) -> {err}");
            ok &= err == Error.Ok;
        }
        return ok;
    }

    /// <summary>Loads a stamp, crops it to a square around its non-black content and scales it to n².</summary>
    private static float[]? LoadStamp(string path, int n)
    {
        if (!File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        img.Convert(Image.Format.L8);
        int w = img.GetWidth(), h = img.GetHeight();
        byte[] d = img.GetData();
        int max = 0;
        foreach (byte b in d) max = Math.Max(max, b);
        int threshold = Math.Max(1, max / 25);
        int x0 = w, z0 = h, x1 = -1, z1 = -1;
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                if (d[z * w + x] > threshold)
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                }
        if (x1 >= x0)
        {
            // Square around the content's centre, with a little margin so the edge fade doesn't eat it.
            int side = (int)(Math.Max(x1 - x0, z1 - z0) * 1.1f) + 1;
            int cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
            int left = Math.Clamp(cx - side / 2, 0, Math.Max(0, w - side));
            int top = Math.Clamp(cz - side / 2, 0, Math.Max(0, h - side));
            side = Math.Min(side, Math.Min(w, h));
            img = img.GetRegion(new Rect2I(left, top, side, side));
        }
        img.Resize(n, n, Image.Interpolation.Lanczos);
        byte[] s = img.GetData();
        var v = new float[n * n];
        for (int i = 0; i < v.Length; i++) v[i] = s[i] / 255f;
        return v;
    }

    /// <summary>Dozens of soft dots of mixed sizes, denser toward the middle.</summary>
    private static float[] Splatter(int n)
    {
        var v = new float[n * n];
        var rng = new Random(7);
        for (int i = 0; i < 70; i++)
        {
            float r = MathF.Sqrt((float)rng.NextDouble()) * 0.75f, a = (float)rng.NextDouble() * MathF.Tau;
            float cx = (0.5f + 0.5f * r * MathF.Cos(a)) * n, cz = (0.5f + 0.5f * r * MathF.Sin(a)) * n;
            float rad = n * (0.02f + 0.1f * MathF.Pow((float)rng.NextDouble(), 2.5f) * (1f - r));
            for (int z = Math.Max(0, (int)(cz - rad)); z < Math.Min(n, (int)(cz + rad) + 1); z++)
                for (int x = Math.Max(0, (int)(cx - rad)); x < Math.Min(n, (int)(cx + rad) + 1); x++)
                {
                    float t = 1f - MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) / rad;
                    if (t > 0f) v[z * n + x] = MathF.Max(v[z * n + x], MathF.Min(1f, t * 2.5f));
                }
        }
        return v;
    }

    /// <summary>A cloudy blob with ragged holes: fBm noise, softly thresholded.</summary>
    private static float[] NoisePatch(int n)
    {
        var noise = new FastNoiseLite { Seed = 11, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 6f / n,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 4 };
        var v = new float[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dz = (z + 0.5f) / n * 2f - 1f;
                float centre = 1f - MathF.Sqrt(dx * dx + dz * dz);
                float t = noise.GetNoise2D(x, z) * 1.6f + centre * 1.2f - 0.25f;
                v[z * n + x] = Math.Clamp(t * 2f, 0f, 1f);
            }
        return v;
    }

    /// <summary>Parallel streaks along X: noise stretched 8× along the stroke. Makes rotation visible.</summary>
    private static float[] Streaks(int n)
    {
        var noise = new FastNoiseLite { Seed = 23, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 16f / n,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3 };
        var v = new float[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                v[z * n + x] = Math.Clamp(noise.GetNoise2D(x / 8f, z) * 2.2f + 0.35f, 0f, 1f);
        return v;
    }

    private static void Normalise(float[] v)
    {
        float max = 0f;
        foreach (float f in v) max = MathF.Max(max, f);
        if (max <= 0f) return;
        for (int i = 0; i < v.Length; i++) v[i] /= max;
    }

    private static string? Find(string folder, string suffix)
    {
        if (!Directory.Exists(folder)) return null;
        var files = Directory.GetFiles(folder, "*" + suffix);
        return files.Length > 0 ? files[0] : null;
    }

    private static byte[] Load(string path, Image.Format format)
    {
        var img = Image.LoadFromFile(path);
        img.Convert(format);
        if (img.GetWidth() != SliceSize || img.GetHeight() != SliceSize)
            img.Resize(SliceSize, SliceSize, Image.Interpolation.Lanczos);
        return img.GetData();
    }

    private static int Percentile(int[] hist, int total, double p)
    {
        long target = (long)(total * p), acc = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            acc += hist[v];
            if (acc > target) return v;
        }
        return hist.Length - 1;
    }
}
