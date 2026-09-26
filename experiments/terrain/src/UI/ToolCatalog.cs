using System.IO;
using System.Linq;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// One tool button in the tool panel. <see cref="Layer"/> is the ground layer for Paint buttons (-1 otherwise).
/// <see cref="Icon"/> is null for buttons without an icon (they show their name).
/// </summary>
public sealed record ToolDef(string Name, string Tooltip, TerrainTool Tool, Texture2D? Icon = null, int Layer = -1);

/// <summary>A tab in the tool panel, grouping related tools. <see cref="EditorOnly"/> tabs are hidden in game mode.</summary>
public sealed record ToolTab(string Name, ToolDef[] Tools, Texture2D? Icon = null, bool EditorOnly = false);

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
        new ToolTab("Paint", TerrainLayers.All.Where(l => l.Paintable).Select(l =>
            new ToolDef(l.DisplayName, $"Paint {l.DisplayName}: left-drag paints, right-drag erases back to automatic ground",
                TerrainTool.Paint, LayerIcon(l), l.Index)).ToArray(), EditorOnly: true),
        // Later: more tabs here (e.g. Vegetation, Resources) once those tools exist.
    ], Icon("terrain"));

    /// <summary>
    /// The layer's ground texture as a button icon, read straight from the downloaded source colour map.
    /// Null (button falls back to its name) if the textures haven't been fetched yet.
    /// </summary>
    private static Texture2D? LayerIcon(TerrainLayer layer)
    {
        string dir = ProjectSettings.GlobalizePath($"res://assets/textures/terrain/{layer.Name}");
        if (!Directory.Exists(dir)) return null;
        string? file = Directory.EnumerateFiles(dir, "*_Color.jpg").FirstOrDefault();
        if (file is null) return null;
        var image = Image.LoadFromFile(file);
        if (image is null) return null;
        image.Resize(128, 128, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The category with only the tabs available in <paramref name="mode"/>.</summary>
    public static ToolCategory ForMode(ToolCategory category, AppMode mode) => mode == AppMode.MapEditor
        ? category
        : category with { Tabs = category.Tabs.Where(t => !t.EditorOnly).ToArray() };

    /// <summary>Bottom-bar categories, left to right.</summary>
    public static readonly ToolCategory[] All = [Terrain];
}
