using CitySim.Splines.Godot;
using Godot;

namespace CitySim.UI;

/// <summary>
/// Shared look for the game UI, taken from the hover card: near-black panels with a hairline border, controls as faint
/// tiles, and one look for "selected" everywhere (accent tint with an accent outline). Icons are white SVGs from
/// <c>res://addons/citysim_build_ui/icons/</c>, tinted through the theme.
/// </summary>
public static class UiTheme
{
    public static readonly Color BarBg = new(0.10f, 0.11f, 0.13f, 0.96f);
    public static readonly Color Accent = new(0.27f, 0.72f, 0.86f);
    public static readonly Color AccentTint = new(Accent, 0.14f);
    public static readonly Color Text = new(0.92f, 0.94f, 0.96f);
    public static readonly Color TextDim = new(0.62f, 0.67f, 0.72f);
    public static readonly Color Warn = new(0.95f, 0.70f, 0.30f);
    public static readonly Color TooltipBg = new(0.08f, 0.09f, 0.11f, 0.97f);
    /// <summary>Faint fill for tiles on a dark panel: buttons, cards, the hover card's detail cells.</summary>
    public static readonly Color Tile = new(1f, 1f, 1f, 0.045f);
    public static readonly Color TileHover = new(1f, 1f, 1f, 0.08f);
    public static readonly Color Hairline = new(1f, 1f, 1f, 0.08f);

    public const float BarHeight = 72f;
    public const float Gap = 10f;

    private static Theme? _theme;

    public static Theme Theme => _theme ??= Build();

    public static StyleBoxFlat Box(Color bg, int radius = 6, int padding = 8)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.SetCornerRadiusAll(radius);
        sb.SetContentMarginAll(padding);
        return sb;
    }

    /// <summary>The selected look: accent tint with a 1 px accent outline.</summary>
    public static StyleBoxFlat Selected(int radius = 5, int padding = 6, float tint = 0.14f)
    {
        var sb = Box(new Color(Accent, tint), radius, padding);
        sb.SetBorderWidthAll(1);
        sb.BorderColor = Accent;
        return sb;
    }

    /// <summary>A panel sized to its content with its own padding (chips, tiles, badges).</summary>
    public static PanelContainer Chip(Color bg, int radius, int padX, int padY)
    {
        var sb = Box(bg, radius, 0);
        sb.ContentMarginLeft = sb.ContentMarginRight = padX;
        sb.ContentMarginTop = sb.ContentMarginBottom = padY;
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", sb);
        return p;
    }

    /// <summary>One label/value tile: a small upper-case label over the value. "2 · two-way" shows "two-way" smaller
    /// and dim; "None" is dimmed so the item's actual features stand out.</summary>
    public static Control Cell(string label, string value)
    {
        var cell = Chip(Tile, 5, 9, 6);
        cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        cell.AddChild(v);
        v.AddChild(Label(label.ToUpperInvariant(), 10, dim: true));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        int split = value.IndexOf(" · ", System.StringComparison.Ordinal);
        string main = split < 0 ? value : value[..split];
        row.AddChild(Label(main, 14, dim: main == "None"));
        if (split >= 0)
        {
            var rest = Label(value[(split + 3)..], 12, dim: true);
            rest.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
            rest.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            rest.ClipText = true;
            rest.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            row.AddChild(rest);
        }
        v.AddChild(row);
        return cell;
    }

    /// <summary>A small dim chip, like the hover card's tab chip.</summary>
    public static PanelContainer TagChip(string text)
    {
        var chip = Chip(Tile, 4, 7, 2);
        chip.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        chip.AddChild(Label(text, 11, dim: true));
        return chip;
    }

    /// <summary>An input (<c>"Ctrl+A"</c>, <c>"Shift+click"</c>) as keycaps with the key's name, and the splines addon's
    /// icons for mouse buttons. <c>+</c> joins a combo, <c>·</c> separates alternatives (drawn as <c>/</c>).</summary>
    public static Control Keys(string key, int px = 16)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        row.AddThemeConstantOverride("separation", 3);
        var alternatives = key.Split(" · ");
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) row.AddChild(Joiner("/"));
            var parts = alternatives[a].Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) row.AddChild(Joiner("+"));
                string part = parts[i].Trim();
                row.AddChild(KeyGlyphs.IsMouse(part) ? MouseIcon(part, px) : Keycap(part));
            }
        }
        return row;
    }

    private static Control Joiner(string text)
    {
        var l = Label(text, 11, dim: true);
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return l;
    }

    private static Control Keycap(string name)
    {
        var sb = Box(Tile, 3, 0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 5;
        sb.ContentMarginTop = 0;
        sb.ContentMarginBottom = 1;
        sb.SetBorderWidthAll(1);
        sb.BorderWidthBottom = 2;
        sb.BorderColor = new Color(1f, 1f, 1f, 0.22f);
        var cap = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        cap.AddThemeStyleboxOverride("panel", sb);
        cap.AddChild(Label(name, 11, dim: true));
        return cap;
    }

    private static Control MouseIcon(string part, int px)
    {
        var l = new RichTextLabel
        {
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ScrollActive = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        l.AddThemeFontSizeOverride("normal_font_size", 12);
        KeyGlyphs.Append(l, part, px + 4, Text); // bigger and brighter than keycaps, so the pressed button reads
        return l;
    }

    public static Control Rule() => new ColorRect { Color = Hairline, CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>An opaque colour that looks like <paramref name="tint"/> laid over a panel. For boxes that clip their
    /// children (<see cref="CanvasItem.ClipChildrenMode.AndDraw"/> masks by the box's alpha, so it must be opaque).</summary>
    public static Color OnPanel(Color tint)
    {
        var c = TooltipBg.Lerp(tint, tint.A);
        c.A = 1f;
        return c;
    }

    /// <summary>An icon rasterised at <paramref name="px"/>, for places that draw a texture at its own size
    /// (<see cref="LineEdit.RightIcon"/>).</summary>
    public static Texture2D? Icon(string name, int px)
    {
        string path = $"res://addons/citysim_build_ui/icons/{name}.svg";
        var image = new Image();
        return FileAccess.FileExists(path) && image.LoadSvgFromString(FileAccess.GetFileAsString(path), px / 64f) == Error.Ok
            ? ImageTexture.CreateFromImage(image) : null;
    }

    public static Texture2D? Icon(string name)
    {
        string path = $"res://addons/citysim_build_ui/icons/{name}.svg";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public static Label Label(string text, int size = 14, bool dim = false)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        if (dim) l.AddThemeColorOverride("font_color", TextDim);
        return l;
    }

    /// <summary>Small upper-case section heading with an optional dim hint on the right (e.g. a hotkey).</summary>
    public static Control Section(string title, string hint = "")
    {
        var row = new HBoxContainer();
        var l = Label(title.ToUpperInvariant(), 10, dim: true);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(l);
        if (hint != "") row.AddChild(Label(hint, 11, dim: true));
        return row;
    }

    private static Theme Build()
    {
        var t = new Theme();
        // Panels and the hover card are the same box.
        var panel = Box(TooltipBg, 8, 12);
        panel.SetBorderWidthAll(1);
        panel.BorderColor = Hairline;
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetStylebox("panel", "TooltipPanel", panel);

        t.SetStylebox("normal", "Button", Box(Tile, 5, 6));
        t.SetStylebox("hover", "Button", Box(TileHover, 5, 6));
        t.SetStylebox("pressed", "Button", Selected());
        t.SetStylebox("hover_pressed", "Button", Selected(tint: 0.22f));
        t.SetStylebox("disabled", "Button", Box(new Color(1f, 1f, 1f, 0.02f), 5, 6));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        foreach (var name in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color",
                     "icon_normal_color", "icon_hover_color", "icon_focus_color" })
            t.SetColor(name, "Button", Text);
        t.SetColor("font_disabled_color", "Button", new Color(TextDim, 0.5f));
        t.SetColor("icon_disabled_color", "Button", new Color(TextDim, 0.5f));
        foreach (var name in new[] { "icon_pressed_color", "icon_hover_pressed_color" })
            t.SetColor(name, "Button", Accent);
        t.SetFontSize("font_size", "Button", 13);

        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", 14);

        t.SetStylebox("normal", "LineEdit", Box(Tile, 5, 7));
        t.SetStylebox("focus", "LineEdit", Outline(Accent));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", TextDim);
        t.SetFontSize("font_size", "LineEdit", 14);

        t.SetColor("font_color", "CheckButton", Text);
        t.SetColor("font_hover_color", "CheckButton", Text);
        t.SetColor("font_pressed_color", "CheckButton", Text);
        t.SetColor("font_hover_pressed_color", "CheckButton", Text);
        t.SetFontSize("font_size", "CheckButton", 13);
        foreach (var s in new[] { "normal", "hover", "pressed", "hover_pressed", "focus" })
            t.SetStylebox(s, "CheckButton", new StyleBoxEmpty());

        t.SetConstant("separation", "HSeparator", 10);
        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Hairline, Thickness = 1 });
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Hairline, Thickness = 1, Vertical = true });
        return t;
    }

    private static StyleBoxFlat Outline(Color c)
    {
        var sb = Box(new Color(0, 0, 0, 0), 5, 7);
        sb.SetBorderWidthAll(1);
        sb.BorderColor = c;
        sb.DrawCenter = false;
        return sb;
    }
}
