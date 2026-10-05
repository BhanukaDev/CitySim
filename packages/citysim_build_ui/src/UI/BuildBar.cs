using System;
using System.Collections.Generic;
using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>Full-width bar along the bottom of the screen: one button per <see cref="BuildCategory"/> that has content.</summary>
public partial class BuildBar : PanelContainer
{
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly HBoxContainer _row = new() { Alignment = BoxContainer.AlignmentMode.Center };

    public event Action<BuildCategory>? CategoryPressed;

    public BuildBar()
    {
        Name = "BuildBar";
        SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        OffsetTop = -UiTheme.BarHeight;
        GrowVertical = GrowDirection.Begin;
        var bg = UiTheme.Box(UiTheme.BarBg, 0, 6);
        bg.BorderWidthTop = 1;
        bg.BorderColor = UiTheme.Hairline;
        AddThemeStyleboxOverride("panel", bg);
        _row.AddThemeConstantOverride("separation", 4);
        AddChild(_row);
    }

    public void SetCategories(IEnumerable<BuildCategory> categories)
    {
        foreach (var c in categories)
        {
            var b = new Button
            {
                Text = c.Label,
                Icon = c.Icon,
                ToggleMode = true,
                FocusMode = FocusModeEnum.None,
                IconAlignment = HorizontalAlignment.Center,
                VerticalIconAlignment = VerticalAlignment.Top,
                ExpandIcon = true,
                CustomMinimumSize = new Vector2(84, UiTheme.BarHeight - 14),
            };
            b.AddThemeConstantOverride("icon_max_width", 26);
            b.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            b.Pressed += () => CategoryPressed?.Invoke(c);
            _row.AddChild(b);
            _buttons[c.Id] = b;
        }
    }

    public void SetActive(BuildCategory? active)
    {
        foreach (var (id, b) in _buttons)
            b.SetPressedNoSignal(id == active?.Id);
    }
}
