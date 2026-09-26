using Godot;

namespace CitySim.UI;

/// <summary>Shared look for the game UI: dark translucent panels, rounded buttons, teal accent for selection.</summary>
public static class UiTheme
{
    public static readonly Color PanelBg = new(0.13f, 0.15f, 0.18f, 0.92f);
    public static readonly Color BarBg = new(0.10f, 0.11f, 0.13f, 0.95f);
    public static readonly Color ButtonBg = new(0.24f, 0.27f, 0.31f);
    public static readonly Color ButtonHover = new(0.31f, 0.35f, 0.40f);
    public static readonly Color Accent = new(0.27f, 0.72f, 0.86f);
    public static readonly Color AccentHover = new(0.38f, 0.80f, 0.92f);
    public static readonly Color Text = new(0.92f, 0.94f, 0.96f);
    public static readonly Color TextDim = new(0.65f, 0.70f, 0.75f);

    public const float BarHeight = 56f;
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

    private static Theme Build()
    {
        var t = new Theme();
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, 6, 10));

        t.SetStylebox("normal", "Button", Box(ButtonBg, 5, 6));
        t.SetStylebox("hover", "Button", Box(ButtonHover, 5, 6));
        t.SetStylebox("pressed", "Button", Box(Accent, 5, 6));
        t.SetStylebox("hover_pressed", "Button", Box(AccentHover, 5, 6));
        t.SetStylebox("disabled", "Button", Box(ButtonBg.Darkened(0.3f), 5, 6));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        foreach (var name in new[] { "font_color", "font_hover_color", "font_focus_color" })
            t.SetColor(name, "Button", Text);
        t.SetColor("font_pressed_color", "Button", Colors.White);
        t.SetColor("font_hover_pressed_color", "Button", Colors.White);
        foreach (var name in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color" })
            t.SetColor(name, "Button", Colors.White); // icons are full-colour art: never tint them

        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", 14);
        t.SetFontSize("font_size", "Button", 13);
        return t;
    }

    /// <summary>
    /// A square toggle button showing <paramref name="icon"/> if given, else a text placeholder.
    /// Real icons will be dropped in later.
    /// </summary>
    public static Button IconButton(string label, Texture2D? icon, float size, string tooltip)
    {
        var b = new Button
        {
            ToggleMode = true,
            CustomMinimumSize = new Vector2(size, size),
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None,
            ClipText = true,
        };
        if (icon is not null)
        {
            b.Icon = icon;
            b.ExpandIcon = true;
            b.IconAlignment = HorizontalAlignment.Center;
        }
        else
        {
            // Placeholder until icons arrive: wrap longer names onto two lines instead of clipping.
            b.Text = label;
            b.ClipText = false;
            b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
        return b;
    }
}
