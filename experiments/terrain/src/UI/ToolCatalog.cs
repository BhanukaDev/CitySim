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
        ]),
        new ToolTab("Paint", [], EditorOnly: true, PaintTab: true),
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
