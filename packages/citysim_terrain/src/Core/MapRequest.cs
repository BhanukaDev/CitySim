using CitySim.TerrainSystem.Generation;

namespace CitySim.TerrainSystem;

/// <summary>What map a <c>Terrain</c> should open with (<c>Terrain.Open</c>). Engine-agnostic.</summary>
public abstract record MapRequest
{
    /// <summary>Terrain theme id for a new map; a loaded map uses the theme saved in it. Null = the Terrain's current/default theme.</summary>
    public string? ThemeId { get; init; }
}

/// <summary>
/// A new map made by the generator (noise, a placed heightmap image, or flat). With <paramref name="ShowGenerator"/> the
/// Map Editor opens with the generator panel showing these settings. <paramref name="Map"/> is the result when it was
/// already generated (the menu does it on a worker, with progress); otherwise the Terrain generates it.
/// </summary>
public sealed record GeneratedMapRequest(GenSettings Settings, bool ShowGenerator = false, HeightMap? Map = null) : MapRequest;

/// <summary>A map already read from a file (loaded before the scene change, so errors show in the menu).</summary>
public sealed record LoadedMapRequest(HeightMap Map, SplatMap Splat, string Path, CitySim.WaterSystem.WaterData? Water = null) : MapRequest;
