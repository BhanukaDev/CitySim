using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.UI;

/// <summary>Full-width bar along the bottom of the screen with one button per tool category.</summary>
public partial class BottomBar : PanelContainer
{
    private readonly Dictionary<ToolCategory, Button> _buttons = new();
    private readonly HBoxContainer _row;

    public event Action<ToolCategory>? CategoryPressed;

    public BottomBar()
    {
        Name = "BottomBar";
        SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        OffsetTop = -UiTheme.BarHeight;
        GrowVertical = GrowDirection.Begin;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.BarBg, 0, 6));

        _row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _row.AddThemeConstantOverride("separation", 6);
        AddChild(_row);
    }

    public void AddCategories(IEnumerable<ToolCategory> categories)
    {
        foreach (var cat in categories)
        {
            var b = UiTheme.IconButton(cat.Name, cat.Icon, UiTheme.BarHeight - 12f, cat.Name);
            b.CustomMinimumSize = new Vector2(cat.Icon is null ? 72f : UiTheme.BarHeight - 12f, UiTheme.BarHeight - 12f);
            b.Pressed += () => CategoryPressed?.Invoke(cat);
            _row.AddChild(b);
            _buttons[cat] = b;
        }
    }

    /// <summary>Adds a plain toggle button after the categories (e.g. the Map Editor's generator panel).</summary>
    public Button AddToggle(string name, string tooltip, Action pressed)
    {
        var b = UiTheme.IconButton(name, null, UiTheme.BarHeight - 12f, tooltip);
        b.CustomMinimumSize = new Vector2(84f, UiTheme.BarHeight - 12f);
        b.Pressed += pressed;
        _row.AddChild(b);
        return b;
    }

    public void SetActive(ToolCategory? active)
    {
        foreach (var (cat, b) in _buttons)
            b.SetPressedNoSignal(cat == active);
    }
}
