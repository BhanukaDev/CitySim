using System;
using System.Collections.Generic;
using CitySim.Terraform;
using CitySim.TerrainSystem.Sculpt;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The always-open panel next to the Terrain tray, in the roads panel's look: the picked tool's name and tab chip, its
/// usage as tiles, then its settings (Shift's Raise · Lower, brush size and strength, Shift's max slope, Level's height, Slope's start point,
/// Channel's shape, size, mode and fill) and the contour lines. Reads and writes the <see cref="TerrainToolController"/>.
/// </summary>
public partial class TerrainOptionsPanel : PanelContainer, IOptionsPanel
{
    private readonly Label _name = UiTheme.Label("", 17);
    private readonly HBoxContainer _head = new();
    private PanelContainer? _chip;
    private readonly VBoxContainer _usage = new();
    private readonly VBoxContainer _settings = new();
    private readonly CheckButton _contours = new() { FocusMode = FocusModeEnum.None, TooltipText = "Height lines every 2 m" };
    private readonly List<Action> _refresh = new();
    private Action? _live; // follows the cursor (the graded channel's numbers)
    private double _liveWait;
    private TerrainToolController? _tools;
    private TerrainTool? _item;

    public TerrainOptionsPanel()
    {
        Name = "TerrainOptionsPanel";
        CustomMinimumSize = new Vector2(RoadOptionsPanel.PanelWidth, 0);
        SizeFlagsVertical = SizeFlags.ShrinkEnd;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        _head.AddThemeConstantOverride("separation", 8);
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _name.ClipText = true;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _head.AddChild(_name);
        col.AddChild(_head);
        _usage.AddThemeConstantOverride("separation", 4);
        col.AddChild(_usage);
        col.AddChild(new HSeparator());
        _settings.AddThemeConstantOverride("separation", 8);
        col.AddChild(_settings);
        col.AddChild(new HSeparator());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        col.AddChild(row);
        var l = UiTheme.Label("Contours", 13);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(l);
        row.AddChild(UiTheme.Keys("C"));
        _contours.Toggled += on => { if (_tools is not null) _tools.ShowContours = on; };
        row.AddChild(_contours);

        SetItem(null);
    }

    public void Bind(TerrainToolController tools)
    {
        _tools = tools;
        tools.StateChanged += Refresh;
        SetItem(_item);
    }

    /// <summary>Shows the picked card's tool (null = nothing picked).</summary>
    public void SetItem(Content.BuildItem? item, string? tabLabel = null)
    {
        _item = item as TerrainTool;
        _name.Text = _item?.Label ?? "No tool";
        _name.AddThemeColorOverride("font_color", _item is null ? UiTheme.TextDim : UiTheme.Text);
        _chip?.QueueFree();
        _chip = _item is null || tabLabel is null ? null : UiTheme.TagChip(tabLabel);
        if (_chip is not null) _head.AddChild(_chip);
        RoadOptionsPanel.ShowUsage(_usage, _item?.Usage ?? "");
        _usage.Visible = _item is not null;
        BuildSettings();
    }

    private void BuildSettings()
    {
        foreach (var child in _settings.GetChildren()) child.QueueFree();
        _refresh.Clear();
        _live = null;
        var kind = _item is null ? TerrainToolKind.None : TerrainToolController.KindOf(_item.Tool);
        _settings.Visible = kind != TerrainToolKind.None && _tools is not null;
        if (!_settings.Visible) return;
        var t = _tools!;

        if (kind == TerrainToolKind.Shift)
        {
            _settings.AddChild(UiTheme.Section("Mode", "1–2"));
            var modes = new HBoxContainer();
            modes.AddThemeConstantOverride("separation", 6);
            _settings.AddChild(modes);
            var group = new ButtonGroup();
            foreach (var (mode, label, icon) in new[] { (ShiftMode.Raise, "Raise", "arrow-big-up"), (ShiftMode.Lower, "Lower", "arrow-big-down") })
            {
                var b = IconToggle(label, icon, group);
                b.TooltipText = $"{label} ({(int)mode + 1}) · Alt: the other way";
                b.Pressed += () => t.ShiftMode = mode;
                modes.AddChild(b);
                _refresh.Add(() => b.SetPressedNoSignal(t.ShiftMode == mode));
            }
        }

        if (kind == TerrainToolKind.Channel)
        {
            _settings.AddChild(UiTheme.Section("Shape"));
            var shapes = new HBoxContainer();
            shapes.AddThemeConstantOverride("separation", 6);
            _settings.AddChild(shapes);
            var group = new ButtonGroup();
            (ChannelShape Shape, string Label, string Icon)[] all =
            [
                (ChannelShape.V, "V", "channel-v"),
                (ChannelShape.Rounded, "U", "channel-u"),
                (ChannelShape.FlatBed, "Flat", "channel-flat"),
                (ChannelShape.Box, "Box", "channel-box"),
            ];
            foreach (var (shape, label, icon) in all)
            {
                var b = IconToggle(label, icon, group);
                b.Pressed += () => t.Channel = t.Channel with { Shape = shape };
                shapes.AddChild(b);
                _refresh.Add(() => b.SetPressedNoSignal(t.Channel.Shape == shape));
            }
            _settings.AddChild(UiTheme.Section("Size", "[ ]"));
            _settings.AddChild(Slider("Width", 4, 300, 1, () => t.Channel.Width, v => t.Channel = t.Channel with { Width = v }, v => $"{v:0} m"));
            _settings.AddChild(Slider("Depth", 0.5f, 40, 0.5f, () => t.Channel.Depth, v => t.Channel = t.Channel with { Depth = v }, v => $"{v:0.#} m"));
            _settings.AddChild(Slider("Speed", 0.1f, 1, 0.05f, () => t.ChannelIntensity, v => t.ChannelIntensity = v, v => $"{v * 100:0} %"));

            _settings.AddChild(UiTheme.Section("Mode"));
            var modes = new HBoxContainer();
            modes.AddThemeConstantOverride("separation", 4);
            _settings.AddChild(modes);
            var modeGroup = new ButtonGroup();
            foreach (var (mode, label, tip) in new[]
                     {
                         (ChannelMode.FollowGround, "Follow", "Follows the ground under the drag, always downhill"),
                         (ChannelMode.Graded, "Graded", "A straight grade from A to B"),
                     })
            {
                var b = new Button { Text = label, ToggleMode = true, ButtonGroup = modeGroup, TooltipText = tip, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                b.AddThemeFontSizeOverride("font_size", 12);
                b.Pressed += () => { t.ChannelMode = mode; BuildSettings(); };
                modes.AddChild(b);
                _refresh.Add(() => b.SetPressedNoSignal(t.ChannelMode == mode));
            }
            if (t.ChannelMode == ChannelMode.Graded)
            {
                _settings.AddChild(Toggle("Fill", "Walls up to the grade over dips", () => t.ChannelFill, v => t.ChannelFill = v));
                var facts = new GridContainer { Columns = 3 };
                facts.AddThemeConstantOverride("h_separation", 6);
                _settings.AddChild(facts);
                _live = () =>
                {
                    foreach (var c in facts.GetChildren()) c.QueueFree();
                    var m = t.GradeMeasure;
                    facts.AddChild(UiTheme.Cell("Grade", m is { } g ? $"{g.GradePercent:+0.0;-0.0} %" : "None"));
                    facts.AddChild(UiTheme.Cell("Cut", m is { } c1 ? $"{c1.MaxCut:0.0} m" : "None"));
                    facts.AddChild(UiTheme.Cell("Fill", m is { } c2 ? $"{c2.MaxFill:0.0} m" : "None"));
                };
                _refresh.Add(_live);
            }
        }
        else
        {
            _settings.AddChild(UiTheme.Section("Brush", "[ ]"));
            _settings.AddChild(Slider("Size", TerrainToolController.MinRadius, TerrainToolController.MaxRadius, 1, () => t.BrushRadius, v => t.BrushRadius = v, v => $"{v:0} m"));
            _settings.AddChild(Slider("Strength", 0.05f, 1, 0.05f, () => t.BrushStrength, v => t.BrushStrength = v, v => $"{v * 100:0} %"));
        }

        if (kind == TerrainToolKind.Shift)
            _settings.AddChild(Slider("Max slope", 10, 80, 1, () => t.MaxSlopeDegrees, v => t.MaxSlopeDegrees = v, v => $"{v:0}°"));
        if (kind == TerrainToolKind.Level)
            _settings.AddChild(PointRow("Height", () => t.LevelTarget is { } h ? $"{h:0.0} m" : null, "Auto", () => t.LevelTarget = null));
        if (kind == TerrainToolKind.Slope)
            _settings.AddChild(PointRow("Start", () => t.SlopeAnchor is { } a ? $"{a.Y:0.0} m" : null, "Not set", () => t.SlopeAnchor = null));
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_live is null || !IsVisibleInTree() || (_liveWait -= delta) > 0) return;
        _liveWait = 0.15;
        _live();
    }

    private void Refresh()
    {
        if (_tools is null) return;
        _contours.SetPressedNoSignal(_tools.ShowContours);
        foreach (var r in _refresh) r();
    }

    /// <summary>A labelled slider with its value on the right.</summary>
    private Control Slider(string label, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string> format)
    {
        var tile = UiTheme.Chip(UiTheme.Tile, 5, 9, 5);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 2);
        tile.AddChild(col);
        var head = new HBoxContainer();
        col.AddChild(head);
        var l = UiTheme.Label(label.ToUpperInvariant(), 10, dim: true);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(l);
        var value = UiTheme.Label("", 13);
        head.AddChild(value);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, FocusMode = FocusModeEnum.None };
        s.ValueChanged += v => set((float)v);
        col.AddChild(s);
        _refresh.Add(() =>
        {
            s.SetValueNoSignal(get());
            value.Text = format(get());
        });
        return tile;
    }

    private Control Toggle(string label, string tip, Func<bool> get, Action<bool> set)
    {
        var row = new HBoxContainer { TooltipText = tip };
        var l = UiTheme.Label(label, 13);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(l);
        var c = new CheckButton { FocusMode = FocusModeEnum.None, TooltipText = tip };
        c.Toggled += on => set(on);
        row.AddChild(c);
        _refresh.Add(() => c.SetPressedNoSignal(get()));
        return row;
    }

    /// <summary>A picked point's value (dim <paramref name="none"/> when unset) with a clear button.</summary>
    private Control PointRow(string label, Func<string?> get, string none, Action clear)
    {
        var tile = UiTheme.Chip(UiTheme.Tile, 5, 9, 5);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        tile.AddChild(row);
        var l = UiTheme.Label(label.ToUpperInvariant(), 10, dim: true);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(l);
        var value = UiTheme.Label("", 13);
        value.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(value);
        var x = new Button { Icon = UiTheme.Icon("x"), Flat = true, ExpandIcon = true, TooltipText = "Clear", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(22, 22) };
        x.AddThemeConstantOverride("icon_max_width", 14);
        x.AddThemeColorOverride("icon_normal_color", UiTheme.TextDim);
        x.Pressed += clear;
        row.AddChild(x);
        _refresh.Add(() =>
        {
            string? v = get();
            value.Text = v ?? none;
            value.AddThemeColorOverride("font_color", v is null ? UiTheme.TextDim : UiTheme.Text);
            x.Visible = v is not null;
        });
        return tile;
    }

    private static Button IconToggle(string label, string icon, ButtonGroup group)
    {
        var b = new Button
        {
            Text = label,
            Icon = UiTheme.Icon(icon),
            ToggleMode = true,
            ButtonGroup = group,
            FocusMode = FocusModeEnum.None,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Top,
            CustomMinimumSize = new Vector2(48, 56),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        b.AddThemeConstantOverride("icon_max_width", 22);
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }
}
