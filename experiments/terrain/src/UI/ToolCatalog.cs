using Godot;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>One tool button in the tool panel. <see cref="Icon"/> is null until real icons are added.</summary>
public sealed record ToolDef(string Name, string Tooltip, TerrainTool Tool, Texture2D? Icon = null);

/// <summary>A tab in the tool panel, grouping related tools.</summary>
public sealed record ToolTab(string Name, ToolDef[] Tools, Texture2D? Icon = null);

/// <summary>A bottom-bar button. Opening it shows its tabs in the tool panel.</summary>
public sealed record ToolCategory(string Name, ToolTab[] Tabs, Texture2D? Icon = null);

public static class ToolCatalog
{
    public static readonly ToolCategory Terrain = new("Terrain",
    [
        new ToolTab("Terrain",
        [
            new ToolDef("Shift", "Shift: left-click raises, right-click lowers", TerrainTool.Shift),
            new ToolDef("Level", "Level: right-click picks a height, left-drag levels to it\n(no height picked: levels to the most common height under the brush)", TerrainTool.Level),
            new ToolDef("Smooth", "Smooth: left-drag softens bumps and creases", TerrainTool.Smooth),
            new ToolDef("Slope", "Slope: right-click sets the start point, left-drag builds a ramp to where you pressed", TerrainTool.Slope),
        ]),
        // Later: more tabs here (e.g. Vegetation, Resources) once those tools exist.
    ]);

    /// <summary>Bottom-bar categories, left to right.</summary>
    public static readonly ToolCategory[] All = [Terrain];
}
