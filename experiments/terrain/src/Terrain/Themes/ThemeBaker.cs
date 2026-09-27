using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace CitySim.TerrainSystem.Themes;

/// <summary>
/// Turns a <see cref="TerrainTheme"/>'s source textures into what the game loads, in the theme's folder:
/// <list type="bullet">
/// <item><c>baked/albedo_height.png</c>: one <see cref="SliceSize"/>² slice per material, stacked vertically. RGB = colour
///   normalised to a <see cref="NeutralGrey"/> average (the shader tints it), A = height stretched to the full range.</item>
/// <item><c>baked/normal.png</c>: the normal maps, same layout.</item>
/// <item>Their <c>.import</c> files, so Godot imports both as BC7 <c>Texture2DArray</c>s with mipmaps.</item>
/// <item><c>baked/preview_&lt;id&gt;.png</c>: a small lit swatch per material for the game's UI.</item>
/// <item><c>materials.gdshaderinc</c>: the material and slot defines the theme's shader includes.</item>
/// </list>
/// Used by the editor plugin (addons/citysim_themes) and headless by <c>--bake-theme=&lt;id&gt;</c>. Godot must import the
/// files afterwards (the plugin rescans; headless, run <c>--import</c>).
/// </summary>
public static class ThemeBaker
{
    public const int SliceSize = 1024;
    public const int PreviewSize = 128;
    /// <summary>Average brightness (0-1, sRGB) each material's colour is normalised to. The shader divides by the same value.</summary>
    public const float NeutralGrey = 0.4f;

    /// <summary>Bakes <paramref name="theme"/>. Returns the files written (for reimport), or null on failure (errors are logged).</summary>
    public static List<string>? Bake(TerrainTheme theme, Action<string>? log = null)
    {
        log ??= GD.Print;
        AddSlotMaterials(theme, log);
        var problems = theme.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) GD.PushError($"ThemeBaker ({theme.Id}): {p}");
            return null;
        }

        string dir = theme.Dir;
        string bakedDir = ProjectSettings.GlobalizePath(dir + "/baked");
        Directory.CreateDirectory(bakedDir);
        int n = theme.Materials.Count, slice = SliceSize * SliceSize;
        var albedo = new byte[slice * n * 4];
        var normal = new byte[slice * n * 3];
        var written = new List<string>();

        for (int li = 0; li < n; li++)
        {
            var m = theme.Materials[li];
            byte[]? c = LoadSlice(m.Albedo, Image.Format.Rgb8);
            if (c is null)
            {
                GD.PushError($"ThemeBaker ({theme.Id}): can't read '{m.Albedo}' for material '{m.Id}'.");
                return null;
            }
            byte[] h = LoadSlice(m.Height, Image.Format.L8) ?? Luminance(c);
            byte[] nm = LoadSlice(m.Normal, Image.Format.Rgb8) ?? FlatNormal();

            // Scale each colour channel so its average is neutral grey: the texture keeps its light/dark and relative
            // colour detail, and the material's tint decides the actual colour.
            var mean = new double[3];
            for (int i = 0; i < slice; i++)
                for (int ch = 0; ch < 3; ch++)
                    mean[ch] += c[i * 3 + ch];
            var scale = new float[3];
            for (int ch = 0; ch < 3; ch++)
                scale[ch] = NeutralGrey * 255f * slice / (float)Math.Max(mean[ch], 1.0);

            // Stretch height to the full 0-1 range (1st to 99th percentile), so every material blends alike.
            var hist = new int[256];
            foreach (byte v in h) hist[v]++;
            int lo = Percentile(hist, slice, 0.01), hi = Math.Max(Percentile(hist, slice, 0.99), lo + 1);

            int baseA = li * slice * 4;
            for (int i = 0; i < slice; i++)
            {
                for (int ch = 0; ch < 3; ch++)
                    albedo[baseA + i * 4 + ch] = (byte)Math.Clamp((int)(c[i * 3 + ch] * scale[ch] + 0.5f), 0, 255);
                // Alpha never reaches 0: the PNG writer drops the colour of fully transparent pixels.
                albedo[baseA + i * 4 + 3] = (byte)Math.Clamp((h[i] - lo) * 255 / (hi - lo), 1, 255);
            }
            Array.Copy(nm, 0, normal, li * slice * 3, slice * 3);

            string preview = theme.PreviewPath(m);
            if (MakePreview(albedo.AsSpan(baseA, slice * 4), nm, m).SavePng(preview) != Error.Ok)
            {
                GD.PushError($"ThemeBaker ({theme.Id}): can't write {preview}.");
                return null;
            }
            written.Add(preview);
            log($"ThemeBaker ({theme.Id}): {m.Id} <- {Path.GetFileName(m.Albedo)} (height {lo}-{hi})");
        }

        var a = Image.CreateFromData(SliceSize, SliceSize * n, false, Image.Format.Rgba8, albedo);
        var nImg = Image.CreateFromData(SliceSize, SliceSize * n, false, Image.Format.Rgb8, normal);
        if (a.SavePng(theme.AlbedoArrayPath) != Error.Ok || nImg.SavePng(theme.NormalArrayPath) != Error.Ok)
        {
            GD.PushError($"ThemeBaker ({theme.Id}): can't write the texture arrays in {dir}/baked.");
            return null;
        }
        WriteArrayImport(theme.AlbedoArrayPath, n);
        WriteArrayImport(theme.NormalArrayPath, n);
        written.Add(theme.AlbedoArrayPath);
        written.Add(theme.NormalArrayPath);

        using (var f = Godot.FileAccess.Open(theme.IncludePath, Godot.FileAccess.ModeFlags.Write))
        {
            if (f is null)
            {
                GD.PushError($"ThemeBaker ({theme.Id}): can't write {theme.IncludePath}.");
                return null;
            }
            f.StoreString(theme.MakeInclude());
        }
        written.Add(theme.IncludePath);
        log($"ThemeBaker ({theme.Id}): wrote {n} materials of {SliceSize}² to {dir}/baked and {TerrainTheme.IncludeName}");
        return written;
    }

    /// <summary>True when <c>materials.gdshaderinc</c> matches the theme (materials and slots haven't changed since the last bake).</summary>
    public static bool IsIncludeCurrent(TerrainTheme theme) =>
        Godot.FileAccess.FileExists(theme.IncludePath) && Godot.FileAccess.GetFileAsString(theme.IncludePath) == theme.MakeInclude();

    /// <summary>Adds slot materials that aren't in the theme's material list yet (a slot can only lay a listed material).</summary>
    public static void AddSlotMaterials(TerrainTheme theme, Action<string>? log = null)
    {
        foreach (var slot in theme.Slots)
            if (slot?.Material is { } m && theme.SlotMaterialIndex(slot) < 0 && theme.Materials.Count < TerrainTheme.MaxMaterials)
            {
                theme.Materials.Add(m);
                log?.Invoke($"ThemeBaker ({theme.Id}): added slot material '{m.Id}' to Materials");
            }
    }

    /// <summary>A source texture as a <see cref="SliceSize"/>² byte array in <paramref name="format"/>, or null if missing.</summary>
    private static byte[]? LoadSlice(string path, Image.Format format)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string file = path.StartsWith("res://") || path.StartsWith("user://") ? ProjectSettings.GlobalizePath(path) : path;
        if (!File.Exists(file)) return null;
        var img = Image.LoadFromFile(file);
        if (img is null || img.IsEmpty()) return null;
        if (img.IsCompressed()) img.Decompress();
        img.Convert(format);
        if (img.GetWidth() != SliceSize || img.GetHeight() != SliceSize)
            img.Resize(SliceSize, SliceSize, Image.Interpolation.Lanczos);
        return img.GetData();
    }

    private static byte[] Luminance(byte[] rgb)
    {
        var l = new byte[rgb.Length / 3];
        for (int i = 0; i < l.Length; i++)
            l[i] = (byte)((rgb[i * 3] * 77 + rgb[i * 3 + 1] * 150 + rgb[i * 3 + 2] * 29) >> 8);
        return l;
    }

    private static byte[] FlatNormal()
    {
        var n = new byte[SliceSize * SliceSize * 3];
        for (int i = 0; i < n.Length; i += 3) { n[i] = 128; n[i + 1] = 128; n[i + 2] = 255; }
        return n;
    }

    /// <summary>
    /// A small swatch: the baked colour tinted like the shader does (detail contrast 0.6, saturation 0.65 by default) and
    /// lit from the upper left through the normal map.
    /// </summary>
    private static Image MakePreview(ReadOnlySpan<byte> albedoSlice, byte[] normalSlice, TerrainMaterial m)
    {
        const int p = PreviewSize, step = SliceSize / PreviewSize;
        var bytes = new byte[p * p * 3];
        var tint = m.Tint.SrgbToLinear();
        float contrast = 0.6f * m.DetailContrast * 2.2f;
        var light = new System.Numerics.Vector3(-0.45f, 0.5f, 0.75f);
        light = System.Numerics.Vector3.Normalize(light);
        for (int y = 0; y < p; y++)
            for (int x = 0; x < p; x++)
            {
                // Average a step² block of the slice.
                float r = 0, g = 0, b = 0, nx = 0, ny = 0, nz = 0;
                for (int dy = 0; dy < step; dy++)
                    for (int dx = 0; dx < step; dx++)
                    {
                        int i = (y * step + dy) * SliceSize + x * step + dx;
                        r += albedoSlice[i * 4]; g += albedoSlice[i * 4 + 1]; b += albedoSlice[i * 4 + 2];
                        nx += normalSlice[i * 3]; ny += normalSlice[i * 3 + 1]; nz += normalSlice[i * 3 + 2];
                    }
                float k = 1f / (step * step * 255f);
                var ratio = new System.Numerics.Vector3(r, g, b) * k / NeutralGrey;
                float lum = ratio.X * 0.299f + ratio.Y * 0.587f + ratio.Z * 0.114f;
                ratio = System.Numerics.Vector3.Lerp(new System.Numerics.Vector3(lum), ratio, 0.65f);
                var nrm = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(nx, ny, nz) * k * 2f - System.Numerics.Vector3.One);
                // Normal map: x right, y up (OpenGL), z out of the swatch.
                float shade = 0.55f + 0.6f * Math.Max(System.Numerics.Vector3.Dot(nrm, light), 0f);
                var lin = new Color(
                    tint.R * MathF.Pow(Math.Max(ratio.X, 0f), contrast) * shade,
                    tint.G * MathF.Pow(Math.Max(ratio.Y, 0f), contrast) * shade,
                    tint.B * MathF.Pow(Math.Max(ratio.Z, 0f), contrast) * shade).LinearToSrgb();
                int o = (y * p + x) * 3;
                bytes[o] = (byte)Math.Clamp((int)(lin.R * 255f + 0.5f), 0, 255);
                bytes[o + 1] = (byte)Math.Clamp((int)(lin.G * 255f + 0.5f), 0, 255);
                bytes[o + 2] = (byte)Math.Clamp((int)(lin.B * 255f + 0.5f), 0, 255);
            }
        return Image.CreateFromData(p, p, false, Image.Format.Rgb8, bytes);
    }

    /// <summary>Writes the import settings that make Godot import a strip as a BC7 texture array of <paramref name="slices"/>.</summary>
    private static void WriteArrayImport(string pngPath, int slices)
    {
        string importPath = ProjectSettings.GlobalizePath(pngPath + ".import");
        // Keep the uid of an earlier import, so references to the array survive a rebake.
        string uidLine = "";
        if (File.Exists(importPath))
            foreach (var line in File.ReadAllLines(importPath))
                if (line.StartsWith("uid=")) uidLine = line + "\n";
        File.WriteAllText(importPath,
            "[remap]\n\nimporter=\"2d_array_texture\"\ntype=\"CompressedTexture2DArray\"\n" + uidLine +
            "\n[params]\n\ncompress/mode=2\ncompress/high_quality=true\ncompress/lossy_quality=0.7\ncompress/uastc_level=0\n" +
            "compress/rdo_quality_loss=0.0\ncompress/hdr_compression=1\ncompress/channel_pack=1\nmipmaps/generate=true\n" +
            $"mipmaps/limit=-1\nslices/horizontal=1\nslices/vertical={slices}\n");
    }

    private static int Percentile(int[] hist, int total, double p)
    {
        long target = (long)(total * p), sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += hist[i];
            if (sum > target) return i;
        }
        return 255;
    }
}
