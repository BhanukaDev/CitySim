using CitySim.Roads;

namespace CitySim.UI;

/// <summary>
/// The roads package's part of the <see cref="GameHud"/>: its Roads and Terrain categories and their options panels.
/// A panel is added to the HUD the first time it's asked for, so it doesn't matter which node's <c>_Ready</c> runs first.
/// </summary>
public static class RoadsHud
{
    public const string RoadsCategory = "roads";
    public const string TerrainCategory = "terrain";

    public static RoadOptionsPanel RoadOptions(this GameHud hud)
    {
        if (hud.OptionsPanel<RoadOptionsPanel>() is { } panel) return panel;
        hud.AddOptionsPanel(RoadsCategory, panel = new RoadOptionsPanel());
        return panel;
    }

    public static TerrainOptionsPanel TerrainOptions(this GameHud hud)
    {
        if (hud.OptionsPanel<TerrainOptionsPanel>() is { } panel) return panel;
        hud.AddOptionsPanel(TerrainCategory, panel = new TerrainOptionsPanel());
        return panel;
    }

    /// <summary>Whether the Roads tray is open (the road tools only work while it is).</summary>
    public static bool RoadsOpen(this GameHud hud) => hud.IsOpen(RoadsCategory);
    /// <summary>Whether the Terrain tray is open (the terrain tools only work while it is).</summary>
    public static bool TerrainOpen(this GameHud hud) => hud.IsOpen(TerrainCategory);
    /// <summary>The road tool picked in the open Roads tray (Crossings), if any.</summary>
    public static RoadTool? PickedRoadTool(this GameHud hud) => hud.RoadsOpen() ? hud.Tray.Picked as RoadTool : null;
}
