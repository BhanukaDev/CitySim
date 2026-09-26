using System;
using System.Collections.Generic;
using Godot;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Panel centred above the bottom bar: a row of tabs with a close button, and the selected tab's
/// tool buttons below.
/// </summary>
public partial class ToolPanel : PanelContainer
{
    private const float ToolButtonSize = 64f;

    private readonly HBoxContainer _tabsRow;
    private readonly HBoxContainer _toolsRow;
    private readonly List<Button> _tabButtons = new();
    private readonly Dictionary<TerrainTool, Button> _toolButtons = new();
    private ToolCategory? _category;
    private TerrainTool _selected;

    public event Action<ToolDef>? ToolPressed;
    public event Action? CloseRequested;

    public ToolPanel()
    {
        Name = "ToolPanel";
        AnchorLeft = AnchorRight = 0.5f;
        AnchorTop = AnchorBottom = 1f;
        OffsetTop = OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Begin;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 4);
        col.AddChild(header);

        _tabsRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tabsRow.AddThemeConstantOverride("separation", 4);
        header.AddChild(_tabsRow);

        var close = new Button
        {
            Text = "×",
            Flat = true,
            TooltipText = "Close",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(28, 28),
        };
        close.AddThemeFontSizeOverride("font_size", 20);
        close.Pressed += () => CloseRequested?.Invoke();
        header.AddChild(close);

        col.AddChild(new ColorRect { Color = UiTheme.Accent, CustomMinimumSize = new Vector2(0, 2) });

        _toolsRow = new HBoxContainer();
        _toolsRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(_toolsRow);
    }

    public void ShowCategory(ToolCategory category)
    {
        _category = category;
        foreach (var b in _tabButtons) b.QueueFree();
        _tabButtons.Clear();

        for (int i = 0; i < category.Tabs.Length; i++)
        {
            var tab = category.Tabs[i];
            int index = i;
            var b = UiTheme.IconButton(tab.Name, tab.Icon, 28f, tab.Name);
            if (tab.Icon is null) b.CustomMinimumSize = new Vector2(72f, 28f);
            b.Pressed += () => SelectTab(index);
            _tabsRow.AddChild(b);
            _tabButtons.Add(b);
        }
        SelectTab(0);
    }

    public void SetSelectedTool(TerrainTool tool)
    {
        _selected = tool;
        foreach (var (t, b) in _toolButtons)
            b.SetPressedNoSignal(t == tool);
    }

    private void SelectTab(int index)
    {
        if (_category is null) return;
        for (int i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].SetPressedNoSignal(i == index);

        foreach (var b in _toolButtons.Values) b.QueueFree();
        _toolButtons.Clear();

        foreach (var def in _category.Tabs[index].Tools)
        {
            var b = UiTheme.IconButton(def.Name, def.Icon, ToolButtonSize, def.Tooltip);
            b.Pressed += () => ToolPressed?.Invoke(def);
            _toolsRow.AddChild(b);
            _toolButtons[def.Tool] = b;
        }
        SetSelectedTool(_selected);
    }
}
