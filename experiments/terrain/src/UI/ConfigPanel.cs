using System;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Sculpt;
using CitySim.Tools;
using CitySim.WaterSystem;

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
    private readonly CheckButton _contours = new() { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Show height contour lines, every 5th line bolder (C)" };
    private readonly Control[] _contourIntervalRow;
    private readonly CheckButton _grid = new() { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Show the placement grid (G)" };
    private readonly Label _maxSlope = NewValueLabel();
    private readonly Control[] _maxSlopeRow;
    private readonly Control[] _levelRow;
    private readonly Control[] _slopeRow;
    private readonly Button[] _brushButtons = new Button[BrushLibrary.All.Length];
    private readonly Control[] _brushRow;
    private readonly Label _angle = NewValueLabel();
    private readonly Button _rotationMode = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 24) };
    private readonly Control[] _rotationRow;
    private readonly Control[] _sizeRow, _strengthRow;
    // Channel tool.
    private readonly Button _channelMode = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(110, 24),
        TooltipText = "Follow Ground: the channel follows the ground under the drag\nGraded: a straight grade from the start point (right-click) to where you press, changing from the start to the end cross-section" };
    private readonly Label _channelWidth = NewValueLabel(), _channelDepth = NewValueLabel(), _channelEndWidth = NewValueLabel(),
        _channelEndDepth = NewValueLabel(), _channelBank = NewValueLabel(), _channelIntensity = NewValueLabel();
    private readonly Button[] _channelShapes = new Button[4], _channelEndShapes = new Button[4];
    private readonly CheckButton _channelDownhill = new() { Flat = true, FocusMode = FocusModeEnum.None,
        TooltipText = "The channel bed never rises along a stroke: drag downstream, and humps are cut through" };
    private readonly Control[] _channelModeRow, _channelShapeRow, _channelWidthRow, _channelDepthRow, _channelEndShapeRow,
        _channelEndWidthRow, _channelEndDepthRow, _channelBankRow, _channelDownhillRow, _channelIntensityRow;
    // Water tools.
    private readonly Label _waterRadius = NewValueLabel(), _flowRate = NewValueLabel(), _pollution = NewValueLabel(), _waterDepth = NewValueLabel(),
        _targetLevel = NewValueLabel(), _maxFlow = NewValueLabel(), _seaLevel = NewValueLabel();
    private readonly CheckButton _snap = new() { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Rivers placed near the map border snap onto it" };
    private readonly Control[] _waterRadiusRow, _flowRateRow, _pollutionRow, _waterDepthRow, _targetLevelRow, _maxFlowRow, _seaLevelRow, _snapRow;

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

        _sizeRow = AddRow(grid, "Brush Size", Stepper(_size,
            () => Tools(t => t.BrushRadius = SizeStep(t.BrushRadius * 2f, -1) / 2f),
            () => Tools(t => t.BrushRadius = SizeStep(t.BrushRadius * 2f, +1) / 2f)));
        _strengthRow = AddRow(grid, "Brush Strength", Stepper(_strength,
            () => Tools(t => t.BrushStrength -= 0.05f),
            () => Tools(t => t.BrushStrength += 0.05f)));
        _brushRow = AddRow(grid, "Brush", BrushPicker());
        var rotation = new HBoxContainer();
        rotation.AddThemeConstantOverride("separation", 4);
        rotation.AddChild(Stepper(_angle,
            () => Tools(t => t.BrushAngle += 15f),
            () => Tools(t => t.BrushAngle -= 15f)));
        _rotationMode.TooltipText = "Fixed: set by hand · Random: new angle on each click · Follow: turns along the drag";
        _rotationMode.Pressed += () => Tools(t => t.CycleRotationMode());
        rotation.AddChild(_rotationMode);
        _rotationRow = AddRow(grid, "Rotation", rotation);
        _maxSlopeRow = AddRow(grid, "Max Slope", Stepper(_maxSlope,
            () => Tools(t => t.MaxSlopeDegrees -= 5f),
            () => Tools(t => t.MaxSlopeDegrees += 5f)));
        _contours.Toggled += on => Tools(t => t.ShowContours = on);
        AddRow(grid, "Contour Lines (C)", _contours);
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

        _channelMode.Pressed += () => Tools(t => t.ChannelMode = t.ChannelMode == ChannelMode.Graded ? ChannelMode.FollowGround : ChannelMode.Graded);
        _channelModeRow = AddRow(grid, "Mode", _channelMode);
        _channelIntensityRow = AddRow(grid, "Intensity", Stepper(_channelIntensity,
            () => Tools(t => t.ChannelIntensity -= 0.1f),
            () => Tools(t => t.ChannelIntensity += 0.1f)));
        _channelIntensityRow[0].TooltipText = _channelIntensityRow[1].TooltipText =
            "How fast the channel is cut: 100 % cuts the full profile at once; lower digs gradually while you hold or drag slowly";
        _channelShapeRow = AddRow(grid, "Shape", ShapePicker(_channelShapes, (t, sh) => t.ChannelStart = t.ChannelStart with { Shape = sh }));
        _channelWidthRow = AddRow(grid, "Width", Stepper(_channelWidth,
            () => Tools(t => t.ChannelStart = t.ChannelStart with { Width = WidthStep(t.ChannelStart.Width, -1) }),
            () => Tools(t => t.ChannelStart = t.ChannelStart with { Width = WidthStep(t.ChannelStart.Width, +1) })));
        _channelDepthRow = AddRow(grid, "Depth", Stepper(_channelDepth,
            () => Tools(t => t.ChannelStart = t.ChannelStart with { Depth = DepthStep(t.ChannelStart.Depth, -1) }),
            () => Tools(t => t.ChannelStart = t.ChannelStart with { Depth = DepthStep(t.ChannelStart.Depth, +1) })));
        _channelEndShapeRow = AddRow(grid, "End Shape", ShapePicker(_channelEndShapes, (t, sh) => t.ChannelEnd = t.ChannelEnd with { Shape = sh }));
        _channelEndWidthRow = AddRow(grid, "End Width", Stepper(_channelEndWidth,
            () => Tools(t => t.ChannelEnd = t.ChannelEnd with { Width = WidthStep(t.ChannelEnd.Width, -1) }),
            () => Tools(t => t.ChannelEnd = t.ChannelEnd with { Width = WidthStep(t.ChannelEnd.Width, +1) })));
        _channelEndDepthRow = AddRow(grid, "End Depth", Stepper(_channelEndDepth,
            () => Tools(t => t.ChannelEnd = t.ChannelEnd with { Depth = DepthStep(t.ChannelEnd.Depth, -1) }),
            () => Tools(t => t.ChannelEnd = t.ChannelEnd with { Depth = DepthStep(t.ChannelEnd.Depth, +1) })));
        _channelBankRow = AddRow(grid, "Banks", Stepper(_channelBank,
            () => Tools(t => t.ChannelBankDegrees -= 5f),
            () => Tools(t => t.ChannelBankDegrees += 5f)));
        _channelBankRow[0].TooltipText = _channelBankRow[1].TooltipText =
            "Angle the ground rises at beyond the channel's top edge, where it cuts into higher ground";
        _channelDownhill.Toggled += on => Tools(t => t.ChannelDownhillOnly = on);
        _channelDownhillRow = AddRow(grid, "Downhill Only", _channelDownhill);

        _waterRadiusRow = AddRow(grid, "Radius", Stepper(_waterRadius,
            () => Tools(t => t.Water.Radius = SizeStep(t.Water.Radius, -1)),
            () => Tools(t => t.Water.Radius = SizeStep(t.Water.Radius, +1))));
        _flowRateRow = AddRow(grid, "Flow Rate", Stepper(_flowRate,
            () => Tools(t => t.Water.FlowRate /= 1.25f),
            () => Tools(t => t.Water.FlowRate *= 1.25f)));
        _pollutionRow = AddRow(grid, "Pollution", Stepper(_pollution,
            () => Tools(t => t.Water.StepPollution(-1)),
            () => Tools(t => t.Water.StepPollution(+1))));
        _waterDepthRow = AddRow(grid, "Depth", Stepper(_waterDepth,
            () => Tools(t => t.Water.Depth -= t.Water.Depth > 10f ? 5f : 1f),
            () => Tools(t => t.Water.Depth += t.Water.Depth >= 10f ? 5f : 1f)));
        _targetLevelRow = AddRow(grid, "Target Level", ValueWithButton(_targetLevel, "Auto",
            "Forget the picked elevation: the level is the ground at the source plus the depth",
            () => Tools(t => t.Water.PickedLevel = null)));
        _maxFlowRow = AddRow(grid, "Max Flow", Stepper(_maxFlow,
            () => Tools(t => t.Water.MaxFlow /= 1.5f),
            () => Tools(t => t.Water.MaxFlow *= 1.5f)));
        _seaLevelRow = AddRow(grid, "Sea Level", Stepper(_seaLevel,
            () => Tools(t => t.Water.SeaLevel -= 0.5f),
            () => Tools(t => t.Water.SeaLevel += 0.5f)));
        _snap.Toggled += on => Tools(t => t.Water.Snap = on);
        _snapRow = AddRow(grid, "Snapping", _snap);

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
        var water = TerrainToolController.WaterKind(tool);
        var w = _tools.Water;
        _title.Text = tool == TerrainTool.Paint ? $"Paint: {_tools.PaintMaterial?.Label ?? "?"}"
            : water is { } k ? WaterSource.Label(k) + (w.SelectedSource is { } sel && sel.Kind == k ? $" #{sel.Id} (selected)" : "")
            : tool.ToString();
        bool channel = tool == TerrainTool.Channel, graded = channel && _tools.ChannelMode == ChannelMode.Graded;
        foreach (var c in _sizeRow) c.Visible = water is null && !channel;
        foreach (var c in _strengthRow) c.Visible = water is null && !channel;
        foreach (var c in _channelModeRow) c.Visible = channel;
        foreach (var c in _channelShapeRow) c.Visible = channel;
        foreach (var c in _channelIntensityRow) c.Visible = channel;
        _channelIntensity.Text = $"{_tools.ChannelIntensity * 100f:0} %";
        foreach (var c in _channelWidthRow) c.Visible = channel;
        foreach (var c in _channelDepthRow) c.Visible = channel;
        foreach (var c in _channelBankRow) c.Visible = channel;
        foreach (var c in _channelEndShapeRow) c.Visible = graded;
        foreach (var c in _channelEndWidthRow) c.Visible = graded;
        foreach (var c in _channelEndDepthRow) c.Visible = graded;
        foreach (var c in _channelDownhillRow) c.Visible = channel && !graded;
        ((Label)_channelShapeRow[0]).Text = graded ? "Start Shape" : "Shape";
        ((Label)_channelWidthRow[0]).Text = graded ? "Start Width" : "Width";
        ((Label)_channelDepthRow[0]).Text = graded ? "Start Depth" : "Depth";
        _channelMode.Text = graded ? "Graded" : "Follow Ground";
        var cs = _tools.ChannelStart;
        var ce = _tools.ChannelEnd;
        for (int i = 0; i < _channelShapes.Length; i++)
        {
            _channelShapes[i].SetPressedNoSignal(i == (int)cs.Shape);
            _channelEndShapes[i].SetPressedNoSignal(i == (int)ce.Shape);
        }
        _channelWidth.Text = $"{cs.Width:0.#} m";
        _channelDepth.Text = $"{cs.Depth:0.#} m";
        _channelEndWidth.Text = $"{ce.Width:0.#} m";
        _channelEndDepth.Text = $"{ce.Depth:0.#} m";
        _channelBank.Text = $"{_tools.ChannelBankDegrees:0}°";
        _channelDownhill.SetPressedNoSignal(_tools.ChannelDownhillOnly);
        foreach (var c in _waterRadiusRow) c.Visible = water is not null and not WaterSourceKind.Sea;
        foreach (var c in _flowRateRow) c.Visible = water == WaterSourceKind.Stream;
        foreach (var c in _pollutionRow) c.Visible = water == WaterSourceKind.Stream;
        foreach (var c in _waterDepthRow) c.Visible = water is WaterSourceKind.River or WaterSourceKind.Lake;
        foreach (var c in _targetLevelRow) c.Visible = water is WaterSourceKind.River or WaterSourceKind.Lake;
        foreach (var c in _maxFlowRow) c.Visible = water == WaterSourceKind.Lake;
        foreach (var c in _seaLevelRow) c.Visible = water == WaterSourceKind.Sea;
        foreach (var c in _snapRow) c.Visible = water == WaterSourceKind.River;
        // Shown as diameter, like the brush; the tool works in radius.
        _waterRadius.Text = $"{w.Radius * 2f:0} m";
        _flowRate.Text = $"{w.FlowRate:0.#} m³/s";
        _pollution.Text = w.Pollution > 0f ? $"{w.Pollution:0.##} kg/s" : "Clean";
        _waterDepth.Text = $"{w.Depth:0.#} m";
        _targetLevel.Text = w.SelectedSource is { Kind: WaterSourceKind.River or WaterSourceKind.Lake } src && src.Kind == water
            ? $"{src.Level:0.0} m" : w.PickedLevel is { } picked ? $"{picked:0.0} m" : "Auto";
        _maxFlow.Text = $"{w.MaxFlow:0} m³/s";
        _seaLevel.Text = $"{w.SeaLevel:0.0} m";
        _snap.SetPressedNoSignal(w.Snap);
        // Shown as diameter; the controller works in radius.
        _size.Text = $"{_tools.BrushRadius * 2f:0} m";
        _strength.Text = $"{_tools.BrushStrength * 100f:0} %";

        foreach (var c in _brushRow) c.Visible = _tools.UsesBrushShape;
        foreach (var c in _rotationRow) c.Visible = _tools.UsesBrushShape;
        for (int i = 0; i < _brushButtons.Length; i++) _brushButtons[i].SetPressedNoSignal(i == _tools.BrushIndex);
        _angle.Text = $"{_tools.BrushAngle:0}°";
        _rotationMode.Text = _tools.RotationMode.ToString();

        foreach (var c in _maxSlopeRow) c.Visible = tool == TerrainTool.Shift;
        _maxSlope.Text = $"{_tools.MaxSlopeDegrees:0}°";

        _contours.SetPressedNoSignal(_tools.ShowContours);
        _grid.SetPressedNoSignal(_tools.ShowGrid);
        foreach (var c in _contourIntervalRow) c.Visible = _tools.ShowContours;
        _contourInterval.Text = $"{_tools.ContourInterval:0} m";

        foreach (var c in _levelRow) c.Visible = tool == TerrainTool.Level;
        _levelTarget.Text = _tools.LevelTarget is { } h ? $"{h:0.0} m" : "Auto";

        foreach (var c in _slopeRow) c.Visible = _tools.UsesStartPoint;
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
            TerrainTool.Channel when graded => _tools.SlopeAnchor.HasValue
                ? "Left-press at the end point and drag along the route · Right-click: move start"
                : "Right-click to set the start point",
            TerrainTool.Channel => "Left-drag: cut a channel along the drag" + (_tools.ChannelDownhillOnly ? " (drag downstream)" : ""),
            TerrainTool.WaterStream => "Adds a constant flow of water.",
            TerrainTool.WaterRiver => "Holds a constant level; water flows in or out. Near the border it snaps onto it.",
            TerrainTool.WaterLake => "Fills to its level at up to Max Flow; never drains.",
            TerrainTool.WaterSea => "Holds the whole map border at sea level. One per map.",
            _ => "",
        } + (water is not null
            ? "\nLeft-click: place · click a source: select, drag: move · Right-click a source: remove"
              + (water == WaterSourceKind.Stream ? "" : " · Right-click ground: pick elevation")
              + "\n[ ] or Shift+wheel: radius · Esc: deselect"
            : channel ? "\n[ ] or Shift+wheel: width · Alt+wheel: intensity · Only cuts down, never fills"
            : "\n[ ] or Shift+wheel: size · Alt+wheel: strength")
          + (_tools.UsesBrushShape ? "\nCtrl+move mouse: rotate · Ctrl+Q/E: 15° steps" : "")
          + "\nC: contours · G: grid · Ctrl/Cmd+Z: undo";

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

    /// <summary>Channel width steps: 2 m up to 20 m, 5 m up to 50 m, 10 m up to 200 m, then 20 m.</summary>
    private static float WidthStep(float w, int dir) => Snap(w, dir, w < 20f || (dir < 0 && w <= 20f) ? 2f
        : w < 50f || (dir < 0 && w <= 50f) ? 5f : w < 200f || (dir < 0 && w <= 200f) ? 10f : 20f);

    /// <summary>Channel depth steps: 0.5 m up to 5 m, 1 m up to 20 m, then 5 m.</summary>
    private static float DepthStep(float d, int dir) => Snap(d, dir, d < 5f || (dir < 0 && d <= 5f) ? 0.5f
        : d < 20f || (dir < 0 && d <= 20f) ? 1f : 5f);

    private static float Snap(float v, int dir, float step) =>
        dir > 0 ? Mathf.Floor(v / step + 1.001f) * step : Mathf.Ceil(v / step - 1.001f) * step;

    /// <summary>One toggle per <see cref="ChannelShape"/>, drawn as its cross-section.</summary>
    private Control ShapePicker(Button[] buttons, Action<TerrainToolController, ChannelShape> pick)
    {
        (string Icon, string Tip)[] shapes =
        [
            ("channel_v", "V: straight sides meeting at the bottom"),
            ("channel_rounded", "U: a rounded bed, like a natural river"),
            ("channel_flatbed", "Flat bed: a flat bottom with sloped sides"),
            ("channel_box", "Box: a flat bottom with vertical walls, like a canal"),
        ];
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < buttons.Length; i++)
        {
            var shape = (ChannelShape)i;
            var b = new Button { ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(44, 32),
                Icon = GD.Load<Texture2D>($"res://assets/icons/{shapes[i].Icon}.svg"), ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center, TooltipText = shapes[i].Tip };
            b.Pressed += () => Tools(t => pick(t, shape));
            buttons[i] = b;
            row.AddChild(b);
        }
        return row;
    }

    /// <summary>A wrapping row of thumbnail toggles, one per <see cref="BrushLibrary"/> entry.</summary>
    private Control BrushPicker()
    {
        BrushLibrary.Load();
        var flow = new HFlowContainer { CustomMinimumSize = new Vector2(200, 0) };
        flow.AddThemeConstantOverride("h_separation", 4);
        flow.AddThemeConstantOverride("v_separation", 4);
        for (int i = 0; i < BrushLibrary.All.Length; i++)
        {
            var e = BrushLibrary.All[i];
            int index = i;
            var b = new Button
            {
                ToggleMode = true,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(36, 36),
                Icon = e.Texture,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                TooltipText = e.DisplayName,
                Text = e.Texture is null ? e.DisplayName[..1] : "",
            };
            b.Pressed += () => Tools(t => t.BrushIndex = index);
            _brushButtons[i] = b;
            flow.AddChild(b);
        }
        return flow;
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
