using System.Linq;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem.Themes;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// One tool button in the tool panel. <see cref="Layer"/> is the theme material index for Paint buttons (-1 otherwise).
/// <see cref="Icon"/> is null for buttons without an icon (they show their name).
/// </summary>
public sealed record ToolDef(string Name, string Tooltip, TerrainTool Tool, Texture2D? Icon = null, int Layer = -1);

/// <summary>
/// A tab in the tool panel, grouping related tools. <see cref="EditorOnly"/> tabs are hidden in game mode. A
/// <see cref="PaintTab"/> gets one button per paintable material of the map's theme (see <see cref="ToolCatalog.ForMap"/>).
/// </summary>
public sealed record ToolTab(string Name, ToolDef[] Tools, Texture2D? Icon = null, bool EditorOnly = false, bool PaintTab = false);

/// <summary>A bottom-bar button. Opening it shows its tabs in the tool panel.</summary>
public sealed record ToolCategory(string Name, ToolTab[] Tabs, Texture2D? Icon = null);

public static class ToolCatalog
{
    private static Texture2D Icon(string name) => GD.Load<Texture2D>($"res://assets/icons/{name}.svg");

    public static readonly ToolCategory Terrain = new("Terrain",
    [
        new ToolTab("Terrain",
        [
            new ToolDef("Shift", "Shift: left-click raises, right-click lowers", TerrainTool.Shift, Icon("shift")),
            new ToolDef("Level", "Level: right-click picks a height, left-drag levels to it\n(no height picked: levels to the most common height under the brush)", TerrainTool.Level, Icon("level")),
            new ToolDef("Smooth", "Smooth: left-drag softens bumps and creases", TerrainTool.Smooth, Icon("smooth")),
            new ToolDef("Slope", "Slope: right-click sets the start point, left-drag builds a ramp to where you pressed", TerrainTool.Slope, Icon("slope")),
            new ToolDef("Channel", "Channel: left-drag cuts a V, U, flat-bed or box cross-section along the drag (rivers, canals, ditches).\nFollow Ground: follows the ground under the drag · Graded: click point A, then B, then click again to cut a straight grade A → B (Fill builds walls across dips)", TerrainTool.Channel, Icon("channel")),
        ]),
        new ToolTab("Paint", [], EditorOnly: true, PaintTab: true),
        new ToolTab("Water",
        [
            new ToolDef("Stream", "Stream: a constant flow of water (m³/s).\nLeft-click to place, drag a source to move it, right-click a source to remove it.", TerrainTool.WaterStream),
            new ToolDef("River", "River: holds a constant level and lets water flow in or out. Near the border it snaps onto it, like a river entering the map.\nLeft-click to place, right-click on ground to pick the target elevation, right-click a source to remove it.", TerrainTool.WaterRiver),
            new ToolDef("Lake", "Lake: fills to its level at up to a maximum flow and never drains; evaporation and outflow lower it.\nLeft-click to place, right-click on ground to pick the target elevation, right-click a source to remove it.", TerrainTool.WaterLake),
            new ToolDef("Sea", "Sea: holds the whole map border at sea level, so the sea fills every low shore.\nLeft-click to place (one per map), right-click on ground to set the sea level there.", TerrainTool.WaterSea),
        ], EditorOnly: true),
        // Later: more tabs here (e.g. Vegetation, Resources) once those tools exist.
    ], Icon("terrain"));

    /// <summary>
    /// The category as shown for the open map: only the tabs available in <paramref name="mode"/>, and Paint tabs filled
    /// with the theme's paintable materials (icons are the baked preview swatches).
    /// </summary>
    public static ToolCategory ForMap(ToolCategory category, AppMode mode, TerrainTheme? theme) => category with
    {
        Tabs = category.Tabs.Where(t => mode == AppMode.MapEditor || !t.EditorOnly)
            .Select(t => t.PaintTab ? t with { Tools = PaintTools(theme) } : t).ToArray(),
    };

    private static ToolDef[] PaintTools(TerrainTheme? theme)
    {
        if (theme is null) return [];
        return theme.Materials.Select((m, i) => (m, i)).Where(p => p.m is { Paintable: true }).Select(p =>
            new ToolDef(p.m.Label, $"Paint {p.m.Label}: left-drag paints, right-drag erases back to automatic ground",
                TerrainTool.Paint, UI.ThemePreviews.Get(theme, p.m), p.i)).ToArray();
    }

    /// <summary>Bottom-bar categories, left to right.</summary>
    public static readonly ToolCategory[] All = [Terrain];
}
