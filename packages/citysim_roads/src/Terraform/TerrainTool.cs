using CitySim.Content;
using CitySim.UI;
using Godot;

namespace CitySim.Terraform;

/// <summary>
/// A card that picks a terrain tool (Shift, Level, Smooth, Slope, Channel, Erase). One <c>.tres</c> per tool under
/// <c>content/terrain/tools/</c>; <see cref="Tool"/> says which tool the game runs, so a mod can move, rename or hide the
/// card but not add a tool without code. The Paint cards are <see cref="TerrainPaint"/>s, made from the map's theme.
/// </summary>
[GlobalClass]
public partial class TerrainTool : BuildItem
{
    public const string ShiftTool = "shift";
    public const string LevelTool = "level";
    public const string SmoothTool = "smooth";
    public const string SlopeTool = "slope";
    public const string ChannelTool = "channel";
    public const string PaintTool = "paint";
    public const string EraseTool = "erase";

    /// <summary>The tool this card picks: <c>shift</c>, <c>level</c>, <c>smooth</c>, <c>slope</c>, <c>channel</c> or
    /// <c>erase</c> (<c>paint</c> is only made from the theme).</summary>
    [Export] public string Tool { get; set; } = "";
    /// <summary>White-on-transparent icon drawn as the card picture when there's no <see cref="BuildItem.Icon"/> (a
    /// modded tool without a baked picture; see <see cref="TerrainThumbnailBaker"/>).</summary>
    [Export] public Texture2D? Glyph { get; set; }
    /// <summary>The mouse hints shown in the options panel while the tool is picked, one per line.</summary>
    [Export(PropertyHint.MultilineText)] public string Usage { get; set; } = "";

    /// <summary>The <see cref="Glyph"/> centred on a dark ground, half the picture's height.</summary>
    protected override Control CreateThumbnail()
    {
        var ground = new ColorRect { Color = new Color(0, 0, 0, 0.22f), MouseFilter = Control.MouseFilterEnum.Ignore };
        var glyph = new TextureRect
        {
            Texture = Glyph,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = UiTheme.Text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        glyph.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        glyph.AnchorTop = 0.26f;
        glyph.AnchorBottom = 0.74f;
        ground.AddChild(glyph);
        return ground;
    }
}

/// <summary>
/// A Paint card: one paintable material of the map's theme. Not a content file: the
/// <see cref="TerrainToolController"/> makes one per paintable material whenever the theme changes, with the theme's baked
/// swatch as the picture, so a theme (authored in Godot) brings its own cards.
/// </summary>
public partial class TerrainPaint : TerrainTool
{
    /// <summary>The theme material's id.</summary>
    public string Material { get; set; } = "";
}
