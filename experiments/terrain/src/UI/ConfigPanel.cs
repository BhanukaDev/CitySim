using System;
using Godot;
using CitySim.TerrainSystem;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>Bottom-left panel with the selected tool's settings: brush size and strength, plus per-tool options.</summary>
public partial class ConfigPanel : PanelContainer
{
    private TerrainToolController? _tools;

    private readonly Label _title = new();
    private readonly Label _size = NewValueLabel();
    private readonly Label _strength = NewValueLabel();
    private readonly Label _levelTarget = NewValueLabel();
    private readonly Label _slopeAnchor = NewValueLabel();
    private readonly Label _hint = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
    private readonly Label _contourInterval = NewValueLabel();
    private readonly CheckButton _contours = new() { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Show height contour lines (every 5th line is bolder)" };
    private readonly Control[] _contourIntervalRow;
    private readonly CheckButton _grid = new() { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Show the placement grid (G)" };
    private readonly Label _maxSlope = NewValueLabel();
    private readonly Control[] _maxSlopeRow;
    private readonly Control[] _levelRow;
    private readonly Control[] _slopeRow;

    public ConfigPanel()
    {
        Name = "ConfigPanel";
        AnchorLeft = AnchorRight = 0f;
        AnchorTop = AnchorBottom = 1f;
        OffsetLeft = OffsetRight = 16f;
        OffsetTop = OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);
        GrowHorizontal = GrowDirection.End;
        GrowVertical = GrowDirection.Begin;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        _title.AddThemeFontSizeOverride("font_size", 15);
        col.AddChild(_title);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 6);
        col.AddChild(grid);

        AddRow(grid, "Brush Size", Stepper(_size,
            () => Tools(t => t.BrushRadius = SizeStep(t.BrushRadius * 2f, -1) / 2f),
            () => Tools(t => t.BrushRadius = SizeStep(t.BrushRadius * 2f, +1) / 2f)));
        AddRow(grid, "Brush Strength", Stepper(_strength,
            () => Tools(t => t.BrushStrength -= 0.05f),
            () => Tools(t => t.BrushStrength += 0.05f)));
        _maxSlopeRow = AddRow(grid, "Max Slope", Stepper(_maxSlope,
            () => Tools(t => t.MaxSlopeDegrees -= 5f),
            () => Tools(t => t.MaxSlopeDegrees += 5f)));
        _contours.Toggled += on => Tools(t => t.ShowContours = on);
        AddRow(grid, "Contour Lines", _contours);
        _grid.Toggled += on => Tools(t => t.ShowGrid = on);
        AddRow(grid, "Grid (G)", _grid);
        _contourIntervalRow = AddRow(grid, "Contour Spacing", Stepper(_contourInterval,
            () => Tools(t => t.StepContourInterval(-1)),
            () => Tools(t => t.StepContourInterval(+1))));
        _levelRow = AddRow(grid, "Target Height", ValueWithButton(_levelTarget, "Auto",
            "Forget the picked height and level to the most common height under the brush",
            () => Tools(t => t.LevelTarget = null)));
        _slopeRow = AddRow(grid, "Start Point", ValueWithButton(_slopeAnchor, "Clear",
            "Clear the slope start point", () => Tools(t => t.SlopeAnchor = null)));

        _hint.AddThemeColorOverride("font_color", UiTheme.TextDim);
        _hint.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_hint);
    }

    public void Bind(TerrainToolController tools)
    {
        _tools = tools;
        Refresh();
    }

    public void Refresh()
    {
        if (_tools is null) return;
        var tool = _tools.Tool;
        _title.Text = tool == TerrainTool.Paint
            ? $"Paint: {TerrainLayers.All[_tools.PaintLayer].DisplayName}"
            : tool.ToString();
        // Shown as diameter; the controller works in radius.
        _size.Text = $"{_tools.BrushRadius * 2f:0} m";
        _strength.Text = $"{_tools.BrushStrength * 100f:0} %";

        foreach (var c in _maxSlopeRow) c.Visible = tool == TerrainTool.Shift;
        _maxSlope.Text = $"{_tools.MaxSlopeDegrees:0}°";

        _contours.SetPressedNoSignal(_tools.ShowContours);
        _grid.SetPressedNoSignal(_tools.ShowGrid);
        foreach (var c in _contourIntervalRow) c.Visible = _tools.ShowContours;
        _contourInterval.Text = $"{_tools.ContourInterval:0} m";

        foreach (var c in _levelRow) c.Visible = tool == TerrainTool.Level;
        _levelTarget.Text = _tools.LevelTarget is { } h ? $"{h:0.0} m" : "Auto";

        foreach (var c in _slopeRow) c.Visible = tool == TerrainTool.Slope;
        _slopeAnchor.Text = _tools.SlopeAnchor is { } a ? $"{a.Y:0.0} m" : "Not set";

        _hint.Text = tool switch
        {
            TerrainTool.Shift => "Left-click: raise · Right-click: lower",
            TerrainTool.Level => "Right-click: pick height · Left-drag: level",
            TerrainTool.Smooth => "Left-drag: smooth",
            TerrainTool.Paint => "Left-drag: paint · Right-drag: erase (back to automatic ground)",
            TerrainTool.Slope => _tools.SlopeAnchor.HasValue
                ? "Left-press at the end point and drag along the ramp · Right-click: move start"
                : "Right-click to set the start point",
            _ => "",
        } + "\n[ ] or Shift+wheel: size · Alt+wheel: strength · Ctrl/Cmd+Z: undo";

        // Rows appear and disappear per tool; shrink back to the content, keeping the bottom-left corner fixed.
        Callable.From(ShrinkToFit).CallDeferred();
    }

    private void ShrinkToFit()
    {
        OffsetRight = OffsetLeft;
        OffsetTop = OffsetBottom;
    }

    private void Tools(Action<TerrainToolController> act)
    {
        if (_tools is not null) act(_tools);
    }

    /// <summary>Round diameter steps that grow with the brush: 10 m up to 200 m, then 20 m, then 50 m.</summary>
    private static float SizeStep(float r, int dir)
    {
        float probe = dir > 0 ? r : r - 0.01f;
        float step = probe < 200f ? 10f : probe < 400f ? 20f : 50f;
        float snapped = dir > 0 ? Mathf.Floor(r / step + 1.001f) * step : Mathf.Ceil(r / step - 1.001f) * step;
        return snapped;
    }

    private static Label NewValueLabel() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        CustomMinimumSize = new Vector2(64, 0),
    };

    private static Control[] AddRow(GridContainer grid, string name, Control value)
    {
        var label = new Label { Text = name, VerticalAlignment = VerticalAlignment.Center };
        grid.AddChild(label);
        grid.AddChild(value);
        return [label, value];
    }

    private static Control Stepper(Label value, Action down, Action up)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(SmallButton("<", down));
        row.AddChild(value);
        row.AddChild(SmallButton(">", up));
        return row;
    }

    private static Control ValueWithButton(Label value, string text, string tooltip, Action act)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(value);
        var b = SmallButton(text, act);
        b.TooltipText = tooltip;
        b.CustomMinimumSize = new Vector2(48, 24);
        row.AddChild(b);
        return row;
    }

    private static Button SmallButton(string text, Action act)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(24, 24), FocusMode = FocusModeEnum.None };
        b.Pressed += act;
        return b;
    }
}
