using CitySim.Content;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// A card that picks a tool working on roads already built, instead of a road to draw (Crossings, Lane Links). One <c>.tres</c> per
/// tool under <c>content/roads/tools/</c>; <see cref="Tool"/> says which tool the game runs, so a mod can move, rename
/// or hide the card but not add a tool without code.
/// </summary>
[GlobalClass]
public partial class RoadTool : BuildItem
{
    public const string CrossingsTool = "crossings";
    public const string LaneLinksTool = "lane_links";

    /// <summary>The tool this card picks: <c>crossings</c> or <c>lane_links</c>.</summary>
    [Export] public string Tool { get; set; } = "";
    /// <summary>The mouse hints shown in the options panel while the tool is picked, one per line.</summary>
    [Export(PropertyHint.MultilineText)] public string Usage { get; set; } = "";
}
