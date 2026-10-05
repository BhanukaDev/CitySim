using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>
/// A card in the tray: the item's picture edge to edge with its <see cref="BuildItem.Badge"/>, then its name and a dim
/// line (<see cref="BuildItem.Summary"/>, or the tab in search results) with a Mod pill for non-base content. Picked
/// shows an accent outline and a tick. Hovering shows its <see cref="DetailCard"/>.
/// </summary>
public partial class ItemCard : Button
{
    public const float CardWidth = 128f;
    private const float CardHeight = 156f;
    private const float PictureHeight = 76f;
    private const int Radius = 7;

    private readonly string? _tabLabel;
    private readonly Control _overlay;

    public BuildItem Item { get; }

    /// <param name="tabLabel">The tab the item is listed under, for the hover card.</param>
    /// <param name="inSearch">A search result: the dim line names the tab instead of the summary.</param>
    public ItemCard(BuildItem item, string? tabLabel, bool inSearch = false)
    {
        Item = item;
        _tabLabel = tabLabel;
        ToggleMode = true;
        FocusMode = FocusModeEnum.None;
        TooltipText = item.Label; // non-empty, so Godot asks _MakeCustomTooltip for the real card
        CustomMinimumSize = new Vector2(CardWidth, CardHeight);
        // Children are clipped to the card's rounded box, so the picture can run to the edges.
        ClipChildren = ClipChildrenMode.AndDraw;

        // Opaque fills: the clip masks children by the box's alpha.
        AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.OnPanel(UiTheme.Tile), Radius, 0));
        AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.OnPanel(UiTheme.TileHover), Radius, 0));
        AddThemeStyleboxOverride("pressed", UiTheme.Box(UiTheme.OnPanel(UiTheme.AccentTint), Radius, 0));
        AddThemeStyleboxOverride("hover_pressed", UiTheme.Box(UiTheme.OnPanel(UiTheme.AccentTint), Radius, 0));

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        col.AddThemeConstantOverride("separation", 0);
        AddChild(col);

        col.AddChild(Picture(item));

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", 9);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 7);
        col.AddChild(margin);
        var text = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 2);
        margin.AddChild(text);

        var name = UiTheme.Label(item.Label, 13);
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.MaxLinesVisible = 2;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsVertical = SizeFlags.ExpandFill;
        name.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(name);

        var meta = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        meta.AddThemeConstantOverride("separation", 6);
        text.AddChild(meta);
        string line = inSearch && tabLabel is not null ? tabLabel : item.Summary;
        if (line != "")
        {
            var l = UiTheme.Label(line, 11, dim: true);
            l.MouseFilter = MouseFilterEnum.Ignore;
            meta.AddChild(l);
        }
        if (item.Source != "Base") meta.AddChild(ModPill());

        // Last child, so the outline and tick draw over the picture.
        _overlay = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlay.Draw += DrawPicked;
        AddChild(_overlay);
    }

    private static Control Picture(BuildItem item)
    {
        var frame = new Control { CustomMinimumSize = new Vector2(0, PictureHeight), MouseFilter = MouseFilterEnum.Ignore };

        var pic = item.CreatePicture();
        if (pic is TextureRect tr) tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        pic.MouseFilter = MouseFilterEnum.Ignore;
        pic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddChild(pic);

        if (item.Badge == "") return frame;

        // A soft shade at the picture's foot so the badge reads on any thumbnail.
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient { Colors = [new Color(0, 0, 0, 0), new Color(0, 0, 0, 0.45f)], Offsets = [0f, 1f] },
                FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 32,
            },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        shade.OffsetTop = -28;
        frame.AddChild(shade);

        var place = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        place.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        place.AddThemeConstantOverride("margin_left", 6);
        place.AddThemeConstantOverride("margin_bottom", 5);
        frame.AddChild(place);
        var badge = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkEnd,
        };
        var bg = UiTheme.Box(new Color(0.04f, 0.05f, 0.06f, 0.72f), 4, 0);
        bg.ContentMarginLeft = bg.ContentMarginRight = 6;
        badge.AddThemeStyleboxOverride("panel", bg);
        var bl = UiTheme.Label(item.Badge, 11);
        bl.MouseFilter = MouseFilterEnum.Ignore;
        badge.AddChild(bl);
        place.AddChild(badge);
        return frame;
    }

    private static Control ModPill()
    {
        var pill = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var bg = UiTheme.Box(new Color(UiTheme.Warn, 0.16f), 3, 0);
        bg.ContentMarginLeft = bg.ContentMarginRight = 5;
        pill.AddThemeStyleboxOverride("panel", bg);
        var l = UiTheme.Label("MOD", 10);
        l.AddThemeColorOverride("font_color", UiTheme.Warn);
        l.MouseFilter = MouseFilterEnum.Ignore;
        pill.AddChild(l);
        return pill;
    }

    // The button redraws when picked or unpicked (SetPressedNoSignal too), so the overlay follows it.
    public override void _Draw() => _overlay.QueueRedraw();

    private void DrawPicked()
    {
        if (!ButtonPressed) return;
        var outline = new StyleBoxFlat { DrawCenter = false, BorderColor = UiTheme.Accent };
        outline.SetBorderWidthAll(2);
        outline.SetCornerRadiusAll(Radius);
        _overlay.DrawStyleBox(outline, new Rect2(Vector2.Zero, Size));

        var c = new Vector2(Size.X - 15, 15);
        _overlay.DrawCircle(c, 9, UiTheme.Accent, antialiased: true);
        _overlay.DrawPolyline([c + new Vector2(-4, 0.3f), c + new Vector2(-1.2f, 3), c + new Vector2(4, -2.6f)],
            new Color(0.05f, 0.10f, 0.12f), 1.8f, antialiased: true);
    }

    public override GodotObject _MakeCustomTooltip(string forText) => DetailCard.Create(Item, _tabLabel);
}
