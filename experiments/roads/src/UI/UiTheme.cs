using Godot;

namespace CitySim.UI;

/// <summary>
/// Shared look for the game UI: dark translucent panels, rounded buttons, teal accent for selection (same palette as the
/// terrain experiment's UI). Icons are white SVGs from <c>res://assets/icons/</c>, tinted through the theme.
/// </summary>
public static class UiTheme
{
    public static readonly Color PanelBg = new(0.13f, 0.15f, 0.18f, 0.94f);
    public static readonly Color BarBg = new(0.10f, 0.11f, 0.13f, 0.96f);
    public static readonly Color ButtonBg = new(0.22f, 0.25f, 0.29f);
    public static readonly Color ButtonHover = new(0.29f, 0.33f, 0.38f);
    public static readonly Color Accent = new(0.27f, 0.72f, 0.86f);
    public static readonly Color AccentHover = new(0.38f, 0.80f, 0.92f);
    public static readonly Color AccentDim = new(0.17f, 0.33f, 0.40f);
    public static readonly Color Text = new(0.92f, 0.94f, 0.96f);
    public static readonly Color TextDim = new(0.62f, 0.67f, 0.72f);
    public static readonly Color Warn = new(0.95f, 0.70f, 0.30f);
    public static readonly Color TooltipBg = new(0.08f, 0.09f, 0.11f, 0.97f);
    /// <summary>Faint fill for tiles on a dark panel (the hover card's detail cells).</summary>
    public static readonly Color Tile = new(1f, 1f, 1f, 0.045f);
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

    public static Texture2D? Icon(string name)
    {
        string path = $"res://assets/icons/{name}.svg";
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
        var l = Label(title.ToUpperInvariant(), 11, dim: true);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(l);
        if (hint != "") row.AddChild(Label(hint, 11, dim: true));
        return row;
    }

    private static Theme Build()
    {
        var t = new Theme();
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, 8, 10));
        var tip = Box(TooltipBg, 8, 12);
        tip.SetBorderWidthAll(1);
        tip.BorderColor = Hairline;
        t.SetStylebox("panel", "TooltipPanel", tip);

        t.SetStylebox("normal", "Button", Box(ButtonBg, 5, 6));
        t.SetStylebox("hover", "Button", Box(ButtonHover, 5, 6));
        t.SetStylebox("pressed", "Button", Box(Accent, 5, 6));
        t.SetStylebox("hover_pressed", "Button", Box(AccentHover, 5, 6));
        t.SetStylebox("disabled", "Button", Box(ButtonBg.Darkened(0.3f), 5, 6));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        foreach (var name in new[] { "font_color", "font_hover_color", "font_focus_color", "icon_normal_color", "icon_hover_color", "icon_focus_color" })
            t.SetColor(name, "Button", Text);
        foreach (var name in new[] { "font_pressed_color", "font_hover_pressed_color", "icon_pressed_color", "icon_hover_pressed_color" })
            t.SetColor(name, "Button", new Color(0.05f, 0.10f, 0.12f));
        t.SetFontSize("font_size", "Button", 13);

        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", 14);

        t.SetStylebox("normal", "LineEdit", Box(new Color(0.07f, 0.08f, 0.10f, 0.9f), 5, 7));
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
