using System.Linq;

namespace CitySim.TerrainSystem.Generation;

/// <summary>A named starting point for the generator: hills, shape and sea level. The user tweaks from there.</summary>
public sealed record GenPreset(string Name, string Description, NoiseSettings Noise, ShapeSettings Shape, float SeaLevel = 6f)
{
    /// <summary>Applies the preset's look, keeping the size, seed, source and heightmap placement.</summary>
    public GenSettings ApplyTo(GenSettings s) => s with
    {
        Noise = Noise with { Seed = s.Noise.Seed },
        Shape = Shape,
        SeaLevel = SeaLevel,
        Preset = Name,
    };
}

public static class GenPresets
{
    // Coastal maps sit low (base 8 m, sea level 6 m); the shader puts sand along the sea (ground masks).
    private static readonly NoiseSettings Coastal = new() { BaseHeight = 8f, HeightScale = 200f, Flatness = 0.5f };

    public static readonly GenPreset[] All =
    [
        new("Rolling Hills", "Hills and wide buildable lowlands", new(), new()),
        new("Mountains", "High, rugged ranges with narrow valleys",
            new() { HeightScale = 480f, Flatness = 0.15f, Frequency = 0.0012f, Gain = 0.46f, WarpAmplitude = 120f }, new()),
        new("Flat Lowlands", "Mostly flat land with gentle rises",
            new NoiseSettings { HeightScale = 70f, Flatness = 0.75f, Frequency = 0.001f, Gain = 0.4f }, new()),
        new("Island", "One island surrounded by sea", Coastal,
            new() { Kind = ShapeKind.Island, Size = 0.72f, EdgeWidth = 0.14f, Roughness = 0.6f, SeaDepth = 35f }),
        new("Coast", "Land on one side, sea on the other",
            Coastal,
            new() { Kind = ShapeKind.Coast, Size = 0.65f, Direction = 180f, EdgeWidth = 0.12f, Roughness = 0.6f, SeaDepth = 35f }),
        new("Archipelago", "A scatter of small islands",
            Coastal with { HeightScale = 140f }, new() { Kind = ShapeKind.Archipelago, Size = 0.4f, EdgeWidth = 0.1f, Roughness = 0.4f, SeaDepth = 30f }),
    ];

    public static GenPreset Default => All[0];

    /// <summary>Finds a preset by name, ignoring case, spaces and dashes (so <c>--preset=rolling-hills</c> works).</summary>
    public static GenPreset? Find(string name)
    {
        static string Key(string n) => new(n.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return All.FirstOrDefault(p => Key(p.Name) == Key(name));
    }
}
