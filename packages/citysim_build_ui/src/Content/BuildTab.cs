using Godot;

namespace CitySim.Content;

/// <summary>A tab in a category's tray (Small, Medium, Large, Highways for Roads). One <c>.tres</c> per tab.</summary>
[GlobalClass]
public partial class BuildTab : Resource
{
    /// <summary>Stable id that items point at. A mod file with the same id replaces this one.</summary>
    [Export] public string Id { get; set; } = "";
    /// <summary>The <see cref="BuildCategory.Id"/> this tab belongs to.</summary>
    [Export] public string Category { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    /// <summary>Left-to-right position in the tray (lower first).</summary>
    [Export] public int Order { get; set; }
    /// <summary>Draws a divider before this tab, to start a new group (e.g. upgrade tabs after the build tabs).</summary>
    [Export] public bool DividerBefore { get; set; }

    public string Source { get; internal set; } = "Base";
    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
}
