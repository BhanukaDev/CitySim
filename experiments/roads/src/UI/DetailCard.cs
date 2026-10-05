using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The hover card for a tray item: name with its tab, a large picture, the description, its
/// <see cref="BuildItem.Details"/> as a two-column grid of tiles (<see cref="UiTheme.Cell"/>), and where it came from. Shown as the card's tooltip,
/// so it only informs and never acts.
/// </summary>
public static partial class DetailCard
{
    /// <summary>Content width; the tooltip panel's padding adds to it.</summary>
    public const float Width = 316f;

    public static Control Create(BuildItem item, string? tabLabel = null)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0) };
        col.AddThemeConstantOverride("separation", 10);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        var name = UiTheme.Label(item.Label, 17);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // A wrapping label measures its height at the width it has. Godot sizes the tooltip window once, before layout,
        // when that width is 0 (one letter per line), and never shrinks it. So give it the width it will end up with.
        float chipWidth = tabLabel is null ? 0 : ThemeDB.FallbackFont.GetStringSize(tabLabel, fontSize: 11).X + 14 + 8;
        name.CustomMinimumSize = new Vector2(Width - chipWidth, 0);
        head.AddChild(name);
        if (tabLabel is not null)
        {
            head.AddChild(UiTheme.TagChip(tabLabel));
        }
        col.AddChild(head);

        // The picture at the thumbnails' own 12:7, clipped to rounded corners (the mask is the box's alpha, so opaque).
        var hero = UiTheme.Chip(Colors.Black, 5, 0, 0);
        hero.ClipChildren = CanvasItem.ClipChildrenMode.AndDraw;
        var pic = item.CreatePicture();
        if (pic is TextureRect tr) tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        pic.CustomMinimumSize = new Vector2(Width, Mathf.Round(Width * 7f / 12f));
        hero.AddChild(pic);
        col.AddChild(hero);

        // Description and details fit their content up to a cap, then fade out at the cut (a tooltip can't scroll).
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        var capped = new CappedBox(body);
        col.AddChild(capped);

        if (item.Description != "")
        {
            var d = UiTheme.Label(item.Description, 13, dim: true);
            d.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            d.CustomMinimumSize = new Vector2(Width, 0);
            d.MaxLinesVisible = 6; // so a long description can't push the details out of view
            d.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            body.AddChild(d);
        }

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var (label, value) in item.Details()) grid.AddChild(UiTheme.Cell(label, value));
        if (grid.GetChildCount() > 0) body.AddChild(grid);
        if (body.GetChildCount() == 0) capped.Visible = false;

        col.AddChild(UiTheme.Rule());
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 4);
        foot.AddChild(UiTheme.Label("Source", 11, dim: true));
        bool baseGame = item.Source == "Base";
        var src = UiTheme.Label(baseGame ? "Base game" : item.Source, 11);
        if (!baseGame) src.AddThemeColorOverride("font_color", UiTheme.Warn);
        foot.AddChild(src);
        if (!baseGame) foot.AddChild(UiTheme.Label("(mod)", 11, dim: true));
        col.AddChild(foot);
        return col;
    }

    /// <summary>
    /// Holds one child at its own height up to <see cref="MaxHeight"/>; past that it clips the child and fades the cut
    /// edge into the card's background. The cap is a share of the window less the card's fixed parts, so the whole card
    /// stays on screen.
    /// </summary>
    private partial class CappedBox : Container
    {
        private const float ScreenShare = 0.7f;
        private const float FixedParts = 300f; // header, picture, footer and gaps
        private const float MinCap = 120f;
        private const float FadeHeight = 36f;

        private readonly Control _content;
        private readonly TextureRect _fade;

        public CappedBox(Control content)
        {
            _content = content;
            ClipContents = true;
            AddChild(content);
            _fade = new TextureRect
            {
                Texture = new GradientTexture2D
                {
                    Gradient = new Gradient { Colors = [new Color(UiTheme.TooltipBg, 0), new Color(UiTheme.TooltipBg, 1)], Offsets = [0f, 1f] },
                    FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 32,
                },
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            AddChild(_fade);
        }

        private float MaxHeight => Mathf.Max(MinCap,
            (IsInsideTree() ? GetTree().Root.GetVisibleRect().Size.Y : 720f) * ScreenShare - FixedParts);

        public override Vector2 _GetMinimumSize()
        {
            var m = _content.GetCombinedMinimumSize();
            return new Vector2(m.X, Mathf.Min(m.Y, MaxHeight));
        }

        public override void _Notification(int what)
        {
            if (what == NotificationEnterTree) UpdateMinimumSize();
            if (what != NotificationSortChildren) return;
            float full = _content.GetCombinedMinimumSize().Y;
            FitChildInRect(_content, new Rect2(0, 0, Size.X, full));
            _fade.Visible = full > Size.Y + 0.5f;
            FitChildInRect(_fade, new Rect2(0, Size.Y - FadeHeight, Size.X, FadeHeight));
        }
    }
}
