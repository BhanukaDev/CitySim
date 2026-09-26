namespace CitySim.TerrainSystem;

/// <summary>One ground texture layer. <see cref="Index"/> is its slice in the texture arrays and its shader index.</summary>
public sealed record TerrainLayer(int Index, string Name, string DisplayName, bool Paintable);

/// <summary>
/// The terrain's ground layers, in texture-array order. All are paintable and stored in the <see cref="SplatMap"/>.
/// The shader (terrain.gdshader) uses the same indices; keep both in sync.
/// </summary>
public static class TerrainLayers
{
    public const int Grass = 0, GrassDry = 1, GrassDirt = 2, Dirt = 3, Gravel = 4, Sand = 5, Rock = 6, Snow = 7;

    /// <summary>Number of layers the splat map stores (the paintable ones).</summary>
    public const int PaintableCount = 8;

    public static readonly TerrainLayer[] All =
    [
        new(Grass, "grass", "Grass", true),
        new(GrassDry, "grass_dry", "Dry Grass", true),
        new(GrassDirt, "grass_dirt", "Grass & Dirt", true),
        new(Dirt, "dirt", "Dirt", true),
        new(Gravel, "gravel", "Gravel", true),
        new(Sand, "sand", "Sand", true),
        new(Rock, "rock", "Rock", true),
        new(Snow, "snow", "Snow", true),
    ];
}
