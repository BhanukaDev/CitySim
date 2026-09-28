namespace CitySim.TerrainSystem.Generation;

/// <summary>Where the base heights come from.</summary>
public enum TerrainSource { Noise, Heightmap, Flat }

/// <summary>Land/sea mask laid over the base heights.</summary>
public enum ShapeKind { None, Island, Coast, Archipelago }

/// <summary>What a placed heightmap image shows outside its own square.</summary>
public enum EdgeMode { Clamp, Tile, Mirror, Fill }

/// <summary>Procedural hills: fBm noise with domain warp, plus a low-frequency mask that flattens lowlands to build on.</summary>
public sealed record NoiseSettings
{
    public int Seed { get; init; } = 1337;
    /// <summary>Cycles per metre of the largest features (smaller = bigger hills).</summary>
    public float Frequency { get; init; } = 0.0015f;
    public int Octaves { get; init; } = 6;
    /// <summary>Height of the tallest hills above <see cref="BaseHeight"/>, in metres.</summary>
    public float HeightScale { get; init; } = 250f;
    /// <summary>Lowest ground, in metres. Presets with a sea put it just above sea level.</summary>
    public float BaseHeight { get; init; }
    /// <summary>0 = hills everywhere, 1 = mostly flat lowlands.</summary>
    public float Flatness { get; init; } = 0.45f;
    public float WarpAmplitude { get; init; } = 60f;
    /// <summary>fBm gain: how much each finer octave adds (roughness).</summary>
    public float Gain { get; init; } = 0.42f;
    /// <summary>
    /// Size of the largest landforms on big maps (mountain ranges vs. plains), in metres. Only maps over 4 km use it,
    /// fully from 16 km (see <see cref="TerrainGen.RegionWeight"/>); smaller maps are one region.
    /// </summary>
    public float RegionSize { get; init; } = 10000f;
    /// <summary>0 = hills look the same everywhere, 1 = strong contrast between ranges and plains.</summary>
    public float RegionStrength { get; init; } = 0.7f;
    /// <summary>Steepest ground allowed, in degrees: steeper peaks and faces are cut down to it. 90 = no limit.</summary>
    public float MaxSlope { get; init; } = 35f;

}

/// <summary>
/// The land/sea mask. Positions are in map units: -1..1 across the map, so a shape looks the same at every map size.
/// </summary>
public sealed record ShapeSettings
{
    public ShapeKind Kind { get; init; } = ShapeKind.None;
    /// <summary>Island: radius (1 = touches the map edge). Coast: share of the map that is land. Archipelago: amount of land.</summary>
    public float Size { get; init; } = 0.7f;
    /// <summary>Coast: which side the sea is on, in degrees (0 = north, 90 = east).</summary>
    public float Direction { get; init; } = 180f;
    /// <summary>
    /// Width of the shore (map units; 0.1 on a 2 km map is ~100 m): gentle stretches get a beach about this wide and a
    /// shelf a bit wider; steep ones a fraction of it.
    /// </summary>
    public float EdgeWidth { get; init; } = 0.15f;
    /// <summary>How much the coastline wanders (0 = a clean circle or line).</summary>
    public float Roughness { get; init; } = 0.5f;
    /// <summary>Depth of the sea floor below <see cref="GenSettings.SeaLevel"/>, in metres.</summary>
    public float SeaDepth { get; init; } = 30f;
}

/// <summary>
/// How a heightmap image lies on the map. Rotation, scale and offset move the image; the map stays put.
/// Offset is in map widths (0.5 = half the map). Black is <see cref="Lowest"/>, white <see cref="Highest"/>.
/// </summary>
public sealed record ImagePlacement
{
    public float Lowest { get; init; }
    public float Highest { get; init; } = 250f;
    /// <summary>Degrees, clockwise seen from above.</summary>
    public float Rotation { get; init; }
    /// <summary>1 = the image covers the map exactly; 0.5 = half size (tiles twice across with Tile).</summary>
    public float Scale { get; init; } = 1f;
    public float OffsetX { get; init; }
    public float OffsetZ { get; init; }
    public EdgeMode Edges { get; init; } = EdgeMode.Clamp;
}

/// <summary>A map size offered in the menus: <paramref name="Cells"/> per side at <see cref="GenSettings.DefaultCellSize"/>.</summary>
public sealed record MapSize(string Label, int Cells)
{
    /// <summary>
    /// Powers of two, so the build area fills whole Terrain3D regions. 14.3 km is CS2's buildable side (4096 × 3.5 m); 28.7 km is the M6 build area (8192 × 3.5 m).
    /// </summary>
    public static readonly MapSize[] All =
    [
        new("1.8 km", 512), new("3.6 km", 1024), new("7.2 km", 2048), new("14.3 km", 4096), new("28.7 km", 8192),
    ];
}

/// <summary>
/// Everything that decides a generated map. Engine-agnostic, and cheap to copy with <c>with</c>, so the generator panel
/// can keep one per change and the generator can run it on another thread.
/// </summary>
public sealed record GenSettings
{
    /// <summary>Spacing of height vertices for new maps, in metres (as in CS2).</summary>
    public const float DefaultCellSize = 3.5f;

    /// <summary>Map size in cells per side (vertices = cells + 1).</summary>
    public int Cells { get; init; } = 1024;
    public float CellSize { get; init; } = DefaultCellSize;
    public TerrainSource Source { get; init; } = TerrainSource.Noise;
    public NoiseSettings Noise { get; init; } = new();
    public ShapeSettings Shape { get; init; } = new();
    /// <summary>The image for <see cref="TerrainSource.Heightmap"/> (kept by reference, never modified).</summary>
    public HeightmapImage? Image { get; init; }
    public string? ImageName { get; init; }
    public ImagePlacement Placement { get; init; } = new();
    public float FlatHeight { get; init; } = 40f;
    /// <summary>Height the sea shapes blend down to. No water yet: below it is sandy low ground.</summary>
    public float SeaLevel { get; init; } = 6f;
    /// <summary>
    /// Share of sea shores that are gentle beaches rather than steep banks, 0..1. Which stretches
    /// are which comes from low-frequency noise, so a coast changes character along its length.
    /// </summary>
    public float GentleShores { get; init; } = 0.6f;
    /// <summary>1-2-1 blur passes at full resolution, to soften creases.</summary>
    public int SmoothPasses { get; init; } = 2;

    /// <summary>Name of the preset these settings started from (for the UI), or null.</summary>
    public string? Preset { get; init; }

    public float WorldSize => Cells * CellSize;
}
