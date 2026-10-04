using System.Collections.Generic;
using Godot;

namespace CitySim.Content;

/// <summary>
/// One card in a tray: a road type, later a building or an upgrade. Content types derive from this and add their own
/// properties, the rows the hover card shows (<see cref="Details"/>) and their own thumbnail.
/// </summary>
[GlobalClass]
public partial class BuildItem : Resource
{
    /// <summary>Stable id, saved with whatever is built from it. A mod file with the same id replaces this one.</summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";
    /// <summary>The <see cref="BuildTab.Id"/> this item is listed under.</summary>
    [Export] public string Tab { get; set; } = "";
    /// <summary>Left-to-right position in its tab (lower first).</summary>
    [Export] public int Order { get; set; }
    /// <summary>Card picture. Leave empty to let the item draw its own (roads draw their cross-section).</summary>
    [Export] public Texture2D? Icon { get; set; }

    public string Source { get; internal set; } = "Base";
    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>Label/value rows for the hover card, in order.</summary>
    public virtual IEnumerable<(string Label, string Value)> Details() => [];

    /// <summary>The card picture: <see cref="Icon"/> if set, else <see cref="CreateThumbnail"/>.</summary>
    public Control CreatePicture() => Icon is not null
        ? new TextureRect { Texture = Icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered }
        : CreateThumbnail();

    /// <summary>A drawn picture for items without an <see cref="Icon"/>. The base version is blank.</summary>
    protected virtual Control CreateThumbnail() => new Control();
}
