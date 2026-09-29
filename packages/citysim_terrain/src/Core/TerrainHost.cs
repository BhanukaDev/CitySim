using System;

namespace CitySim.TerrainSystem;

/// <summary>
/// What the host app tells every <c>Terrain</c> node. Set once at start-up (e.g. from a module initializer), so a Terrain
/// works the same whether the app came through a menu or a scene was run directly. Unset = a plain game view that
/// generates its map from the Terrain's exports. Engine-agnostic.
/// </summary>
public static class TerrainHost
{
    /// <summary>Hands a new Terrain the map to open (consumed: return it once). Null result = generate from the exports.</summary>
    public static Func<MapRequest?>? TakeStartupRequest { get; set; }

    /// <summary>True while the app is a map editor: the map edge shows as a line instead of the horizon ring.</summary>
    public static Func<bool>? IsMapEditor { get; set; }

    public static bool MapEditor => IsMapEditor?.Invoke() ?? false;
}
