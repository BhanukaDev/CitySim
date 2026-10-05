using Godot;

namespace CitySim.Content;

/// <summary>
/// A bottom-bar button (Roads, Zones, Police, ...). One <c>.tres</c> per category. Categories with no tabs or items are
/// not shown, so a category file can ship before its content does.
/// </summary>
[GlobalClass]
public partial class BuildCategory : Resource
{
    /// <summary>Stable id that tabs point at. A mod file with the same id replaces this one.</summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    /// <summary>White-on-transparent icon, tinted by the UI.</summary>
    [Export] public Texture2D? Icon { get; set; }
    /// <summary>Left-to-right position on the bar (lower first).</summary>
    [Export] public int Order { get; set; }

    /// <summary>Where the file came from: "Base" or the mod folder's name. Set when loaded.</summary>
    public string Source { get; internal set; } = "Base";
    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
}
