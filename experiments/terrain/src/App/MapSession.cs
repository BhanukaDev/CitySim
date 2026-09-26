using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;

namespace CitySim.App;

/// <summary>Map Editor: every tool, for making maps. Game: the tools a player gets (no gameplay limits yet).</summary>
public enum AppMode { MapEditor, Game }

/// <summary>What map the map scene should start with.</summary>
public abstract record MapRequest;

/// <summary>
/// A new map made by the generator (noise, a placed heightmap image, or flat). With <paramref name="ShowGenerator"/> the
/// Map Editor opens with the generator panel showing these settings. <paramref name="Map"/> is the result when it was
/// already generated (the menu does it on a worker, with progress); otherwise the map scene generates it.
/// </summary>
public sealed record GeneratedMapRequest(GenSettings Settings, bool ShowGenerator = false, HeightMap? Map = null) : MapRequest;

/// <summary>A map already read from a file (loaded before the scene change, so errors show in the menu).</summary>
public sealed record LoadedMapRequest(HeightMap Map, SplatMap Splat, string Path) : MapRequest;

/// <summary>
/// State that outlives a scene change: the app mode, the map the next map scene should open, and the
/// file the current map was loaded from or saved to. Engine-agnostic.
/// </summary>
public static class MapSession
{
    public static AppMode Mode { get; set; } = AppMode.MapEditor;

    /// <summary>Set before switching to the map scene; <see cref="TakePending"/> consumes it.</summary>
    public static MapRequest? Pending { get; set; }

    /// <summary>File the open map belongs to, or null for a map that has never been saved.</summary>
    public static string? CurrentPath { get; set; }

    public static MapRequest? TakePending()
    {
        var r = Pending;
        Pending = null;
        return r;
    }
}
