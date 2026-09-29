using CitySim.TerrainSystem;

namespace CitySim.App;

/// <summary>Map Editor: every tool, for making maps. Game: the tools a player gets (no gameplay limits yet).</summary>
public enum AppMode { MapEditor, Game }

/// <summary>
/// State that outlives a scene change: the app mode, the map the next map scene should open, and the
/// file the current map was loaded from or saved to. Engine-agnostic.
/// </summary>
public static class MapSession
{
    public static AppMode Mode { get; set; } = AppMode.MapEditor;

    /// <summary>Set before switching to the map scene; <see cref="TakePending"/> consumes it.</summary>
    public static MapRequest? Pending { get; set; }

    /// <summary>Terrain theme id for the next new map (New Map's choice); a loaded map uses the theme saved in it.</summary>
    public static string? NewMapTheme { get; set; }

    /// <summary>File the open map belongs to, or null for a map that has never been saved.</summary>
    public static string? CurrentPath { get; set; }

    /// <summary>Takes the pending request; a new map gets <see cref="NewMapTheme"/>.</summary>
    public static MapRequest? TakePending()
    {
        var r = Pending;
        Pending = null;
        return r is GeneratedMapRequest g && NewMapTheme is { } theme ? g with { ThemeId = theme } : r;
    }

    /// <summary>Tells the terrain package about this app as soon as the assembly loads (before any scene, CLI runs included).</summary>
#pragma warning disable CA2255 // app code: exactly what the attribute is for
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void HookTerrain()
    {
        TerrainHost.TakeStartupRequest = TakePending;
        TerrainHost.IsMapEditor = () => Mode == AppMode.MapEditor;
    }
}
