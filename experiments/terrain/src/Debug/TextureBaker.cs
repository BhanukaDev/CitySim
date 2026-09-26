using System;
using System.IO;
using Godot;
using CitySim.TerrainSystem;

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
