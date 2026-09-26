using System;
using Godot;
using CitySim.TerrainSystem.Sculpt;

namespace CitySim.Tools;

/// <summary>
/// The brush shapes offered by the terrain tools. Entry 0 is the plain round brush (no mask); the rest are
/// greyscale masks baked into <see cref="Dir"/> by <c>tools/fetch_brushes.sh</c> (see <c>TextureBaker.BakeBrushes</c>).
/// </summary>
public static class BrushLibrary
{
    public const string Dir = "res://assets/brushes";
    public const int MaskSize = 256;

    /// <summary>How a mask is made. <see cref="Stamp"/> files come from Roland09/Terrain-Stamps (MIT).</summary>
    public enum Source { Round, Stamp, Splatter, Noise, Streaks }

    public sealed record Entry(string Id, string DisplayName, Source Source, string? StampFile = null)
    {
        public BrushMask? Mask { get; internal set; }
        public Texture2D? Texture { get; internal set; }
        public string ResPath => $"{Dir}/{Id}.png";
    }

    public static readonly Entry[] All =
    [
        new("round", "Soft Round", Source.Round),
        new("hills", "Hills", Source.Stamp, "Stamp 001 - Hills.png"),
        new("ridged", "Ridged", Source.Stamp, "Stamp 005 - Ridged.png"),
        new("plateau", "Plateau", Source.Stamp, "Stamp 009 - Plateaus.png"),
        new("talus", "Plateau Talus", Source.Stamp, "Stamp 013 - Plateaus, Talus.png"),
        new("terrace", "Terraces", Source.Stamp, "Stamp 017 - Terrace Smooth.png"),
        new("splatter", "Splatter", Source.Splatter),
        new("noise", "Noise Patch", Source.Noise),
        new("streaks", "Streaks", Source.Streaks),
    ];

    private static bool _loaded;

    /// <summary>Loads every baked mask. A missing file leaves that entry without a mask (it then acts round).</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        int ok = 0;
        foreach (var e in All)
        {
            if (e.Source == Source.Round)
            {
                e.Texture = RoundThumbnail();
                ok++;
                continue;
            }
            if (!ResourceLoader.Exists(e.ResPath))
            {
                GD.PushWarning($"BrushLibrary: {e.ResPath} is missing. Run tools/fetch_brushes.sh.");
                continue;
            }
            var tex = ResourceLoader.Load<Texture2D>(e.ResPath);
            var img = tex.GetImage();
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.L8);
            if (img.GetWidth() != MaskSize || img.GetHeight() != MaskSize)
                img.Resize(MaskSize, MaskSize, Image.Interpolation.Bilinear);
            byte[] data = img.GetData();
            var values = new float[data.Length];
            for (int i = 0; i < data.Length; i++) values[i] = data[i] / 255f;
            e.Mask = new BrushMask(e.Id, MaskSize, values);
            e.Texture = tex;
            ok++;
        }
        GD.Print($"BrushLibrary: {ok}/{All.Length} brushes");
    }

    /// <summary>UI thumbnail for the round brush: its falloff as a 64² image.</summary>
    private static ImageTexture RoundThumbnail()
    {
        const int n = 64;
        var bytes = new byte[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dz = (z + 0.5f) / n * 2f - 1f;
                bytes[z * n + x] = (byte)(Brush.Falloff(MathF.Sqrt(dx * dx + dz * dz)) * 255f);
            }
        return ImageTexture.CreateFromImage(Image.CreateFromData(n, n, false, Image.Format.L8, bytes));
    }

    public static Entry Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];
}
