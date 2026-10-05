using System;
using System.Collections.Generic;
using CitySim.Roads;
using CitySim.Splines.Godot;
using Godot;

namespace CitySim.UI;

/// <summary>How the road tool draws (keys 1–4). The splines Freehand mode is left out on purpose.</summary>
public enum RoadDrawMode { Straight, Curve, Grid, Replace }

/// <summary>Grid split: equal blocks, or whole lots per block (the splines <c>GridFit</c>).</summary>
public enum RoadGridFit { Even, LotSteps }

/// <summary>Snap groups shown as toggles. Each maps onto several splines snap providers.</summary>
[Flags]
public enum RoadSnaps
{
    None = 0,
    Roads = 1,     // node, edge (and crossings)
    Guides = 2,    // extension, node align, parallel, perpendicular
    Angles = 4,    // the soft square/diagonal/straight-on angles
    Lengths = 8,   // lot-length steps, equal length
    All = Roads | Guides | Angles | Lengths,
}

/// <summary>What the options panel holds. The road tool reads it (wired in the road addon step).</summary>
public sealed class RoadToolOptions
{
    public RoadDrawMode Mode { get; set; } = RoadDrawMode.Straight;
    public int GridCols { get; set; } = 3;
    public int GridRows { get; set; } = 2;
    public RoadGridFit GridFit { get; set; } = RoadGridFit.Even;
    public bool Snapping { get; set; } = true;
    public RoadSnaps Snaps { get; set; } = RoadSnaps.All;
    public bool Anarchy { get; set; }
}

/// <summary>
/// The always-open panel next to the Roads tray, in the hover card's look: the picked road's name, tab chip and a row of
/// fact tiles; tool mode or Move (the splines Edit tool, with its controls), grid blocks (Grid mode only), snapping and
/// Anarchy. A picked road tool shows its usage instead. Only options the splines tool already supports.
/// </summary>
public partial class RoadOptionsPanel : PanelContainer
{
    public const float PanelWidth = 300f;

    private readonly Label _name = UiTheme.Label("", 17);
    private readonly HBoxContainer _head = new();
    private PanelContainer? _chip;
    private readonly GridContainer _facts = new() { Columns = 3 };
    private readonly Dictionary<RoadDrawMode, Button> _modes = new();
    private readonly Button _move = new();
    private readonly VBoxContainer _moveUsage = new();
    private readonly VBoxContainer _gridBox = new();
    private readonly SpinBox _cols = Spin(1, 40);
    private readonly SpinBox _rows = Spin(1, 40);
    private readonly Dictionary<RoadGridFit, Button> _fits = new();
    private readonly CheckButton _snapping = new() { FocusMode = FocusModeEnum.None };
    private readonly Dictionary<RoadSnaps, Button> _snapButtons = new();
    private readonly CheckButton _anarchy = new() { FocusMode = FocusModeEnum.None };
    private readonly VBoxContainer _usage = new();
    private readonly VBoxContainer _drawBox = new(); // everything for drawing roads, hidden while a tool is picked

    public RoadToolOptions Options { get; } = new();

    /// <summary>Whether Move (the Edit tool) is shown as on. The tool host owns the state; see <see cref="SetMoving"/>.</summary>
    public bool Moving { get; private set; }

    public event Action<RoadToolOptions>? OptionsChanged;
    /// <summary>Move was pressed (true), or a draw mode was picked (false).</summary>
    public event Action<bool>? MoveRequested;

    private const string MoveUsage = "LMB road  select\nShift+click road  add\ndrag road · node  move\ndrag ground  box\nRMB point  menu\nDel selection  delete";

    public RoadOptionsPanel()
    {
        Name = "RoadOptionsPanel";
        CustomMinimumSize = new Vector2(PanelWidth, 0);
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
        _facts.AddThemeConstantOverride("h_separation", 6);
        col.AddChild(_facts);
        col.AddChild(new HSeparator());
        _usage.AddThemeConstantOverride("separation", 4);
        _usage.Visible = false;
        col.AddChild(_usage);
        _drawBox.AddThemeConstantOverride("separation", 8);
        col.AddChild(_drawBox);
        col = _drawBox;

        col.AddChild(UiTheme.Section("Mode", "1–4 · M"));
        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(modeRow);
        var modeGroup = new ButtonGroup();
        (RoadDrawMode Mode, string Label, string Icon, string Tip)[] modes =
        [
            (RoadDrawMode.Straight, "Straight", "line", "Straight (1): click points; corners round to the road's radius"),
            (RoadDrawMode.Curve, "Curve", "vector-spline", "Curve (2): start, bend, end"),
            (RoadDrawMode.Grid, "Grid", "grid-4x4", "Grid (3): corner, width, depth"),
            (RoadDrawMode.Replace, "Replace", "replace", "Replace (4): click a road to make it this one; where the mouse is across it moves it sideways"),
        ];
        foreach (var (mode, label, icon, tip) in modes)
        {
            var b = new Button
            {
                Text = label,
                Icon = UiTheme.Icon(icon),
                ToggleMode = true,
                ButtonGroup = modeGroup,
                TooltipText = tip,
                FocusMode = FocusModeEnum.None,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                VerticalIconAlignment = VerticalAlignment.Top,
                CustomMinimumSize = new Vector2(48, 56),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            b.AddThemeConstantOverride("icon_max_width", 22);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Pressed += () => SetMode(mode);
            modeRow.AddChild(b);
            _modes[mode] = b;
        }
        modeRow.AddChild(new VSeparator());
        _move.Text = "Move";
        _move.Icon = UiTheme.Icon("arrows-move");
        _move.ToggleMode = true;
        _move.ButtonGroup = modeGroup;
        _move.TooltipText = "Move (M): select, move and reshape built roads";
        _move.FocusMode = FocusModeEnum.None;
        _move.ExpandIcon = true;
        _move.IconAlignment = HorizontalAlignment.Center;
        _move.VerticalIconAlignment = VerticalAlignment.Top;
        _move.CustomMinimumSize = new Vector2(48, 56);
        _move.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _move.AddThemeConstantOverride("icon_max_width", 22);
        _move.AddThemeFontSizeOverride("font_size", 12);
        _move.Pressed += () => MoveRequested?.Invoke(true);
        modeRow.AddChild(_move);
        _moveUsage.AddThemeConstantOverride("separation", 4);
        _moveUsage.Visible = false;
        col.AddChild(_moveUsage);
        ShowUsage(_moveUsage, MoveUsage);

        _gridBox.AddThemeConstantOverride("separation", 6);
        col.AddChild(_gridBox);
        var blocksTile = UiTheme.Chip(UiTheme.Tile, 5, 9, 4);
        _gridBox.AddChild(blocksTile);
        var blocks = new HBoxContainer();
        blocks.AddThemeConstantOverride("separation", 6);
        blocksTile.AddChild(blocks);
        var bl = UiTheme.Label("BLOCKS", 10, dim: true);
        bl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        blocks.AddChild(bl);
        _cols.Value = Options.GridCols;
        _rows.Value = Options.GridRows;
        _cols.TooltipText = "Blocks along the first edge ([ ])";
        _rows.TooltipText = "Blocks across (Shift+[ ])";
        blocks.AddChild(_cols);
        var times = UiTheme.Label("×", 13, dim: true);
        times.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        blocks.AddChild(times);
        blocks.AddChild(_rows);
        _cols.ValueChanged += v => { Options.GridCols = (int)v; Changed(); };
        _rows.ValueChanged += v => { Options.GridRows = (int)v; Changed(); };
        var fitRow = new HBoxContainer();
        fitRow.AddThemeConstantOverride("separation", 4);
        _gridBox.AddChild(fitRow);
        var fitGroup = new ButtonGroup();
        (RoadGridFit Fit, string Label, string Tip)[] fits =
        [
            (RoadGridFit.Even, "Even", "Equal blocks"),
            (RoadGridFit.LotSteps, "Lot steps", "Every block holds whole lots"),
        ];
        foreach (var (fit, label, tip) in fits)
        {
            var b = new Button { Text = label, ToggleMode = true, ButtonGroup = fitGroup, TooltipText = tip, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.SetPressedNoSignal(fit == Options.GridFit);
            b.Pressed += () => { Options.GridFit = fit; Changed(); };
            fitRow.AddChild(b);
            _fits[fit] = b;
        }

        col.AddChild(new HSeparator());
        var snapHead = new HBoxContainer();
        col.AddChild(snapHead);
        var sl = UiTheme.Section("Snapping");
        sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        sl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        snapHead.AddChild(sl);
        _snapping.ButtonPressed = Options.Snapping;
        _snapping.TooltipText = "All snapping on or off";
        _snapping.Toggled += on => { Options.Snapping = on; RefreshSnaps(); Changed(); };
        snapHead.AddChild(_snapping);
        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 6);
        chips.AddThemeConstantOverride("v_separation", 6);
        col.AddChild(chips);
        (RoadSnaps Flag, string Label, string Tip)[] snaps =
        [
            (RoadSnaps.Roads, "Roads", "Road ends, junctions and along existing roads"),
            (RoadSnaps.Guides, "Guides", "Extension, alignment, parallel and perpendicular guides"),
            (RoadSnaps.Angles, "Angles", "Square, diagonal and straight-on angles"),
            (RoadSnaps.Lengths, "Lengths", "Lot-length steps and matching lengths"),
        ];
        foreach (var (flag, label, tip) in snaps)
        {
            var b = new Button { Text = label, ToggleMode = true, ButtonPressed = true, TooltipText = tip, FocusMode = FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Toggled += on => { Options.Snaps = on ? Options.Snaps | flag : Options.Snaps & ~flag; Changed(); };
            chips.AddChild(b);
            _snapButtons[flag] = b;
        }
        var steps = new HBoxContainer();
        steps.AddThemeConstantOverride("separation", 4);
        col.AddChild(steps);
        steps.AddChild(UiTheme.Keys("Ctrl"));
        steps.AddChild(UiTheme.Label("15°", 11, dim: true));
        steps.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
        steps.AddChild(UiTheme.Keys("Ctrl+Shift"));
        steps.AddChild(UiTheme.Label("5°", 11, dim: true));

        col.AddChild(new HSeparator());
        var anarchyRow = new HBoxContainer();
        anarchyRow.AddThemeConstantOverride("separation", 6);
        anarchyRow.TooltipText = "Ignore overlap and curve limits";
        col.AddChild(anarchyRow);
        var al = UiTheme.Label("Anarchy", 13);
        al.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        al.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        anarchyRow.AddChild(al);
        anarchyRow.AddChild(UiTheme.Keys("Ctrl+A"));
        _anarchy.TooltipText = "Ignore overlap and curve limits";
        _anarchy.Toggled += on => { Options.Anarchy = on; Changed(); };
        anarchyRow.AddChild(_anarchy);

        SetRoad(null);
        SetMode(Options.Mode);
    }

    /// <summary>The tool's usage lines (<c>"LMB junction  select"</c>) as tiles: the input's icons, the target, and the
    /// action dim on the right. A line whose first word isn't an input stays plain text.</summary>
    internal static void ShowUsage(VBoxContainer box, string usage)
    {
        foreach (var child in box.GetChildren()) child.QueueFree();
        foreach (var line in usage.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tile = UiTheme.Chip(UiTheme.Tile, 5, 9, 5);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            tile.AddChild(row);
            box.AddChild(tile);

            int space = line.IndexOf(' ');
            string first = space < 0 ? line : line[..space];
            string rest = line;
            if (KeyGlyphs.Known(first))
            {
                row.AddChild(UiTheme.Keys(first));
                rest = line[first.Length..].Trim();
            }
            int split = rest.IndexOf("  ", StringComparison.Ordinal);
            string target = split < 0 ? rest : rest[..split].Trim();
            var t = UiTheme.Label(target.Length > 0 ? char.ToUpperInvariant(target[0]) + target[1..] : "", 13);
            t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(t);
            if (split >= 0) row.AddChild(UiTheme.Label(rest[split..].Trim(), 12, dim: true));
        }
    }

    /// <summary>Shows the picked card: a road (with the draw options) or a road tool (with its usage).</summary>
    public void SetItem(Content.BuildItem? item, string? tabLabel = null)
    {
        if (item is RoadTool tool)
        {
            SetHead(tool.Label, tabLabel);
            _facts.Visible = false;
            ShowUsage(_usage, tool.Usage);
        }
        else SetRoad(item as RoadType, tabLabel);
        _usage.Visible = item is RoadTool;
        _drawBox.Visible = item is not RoadTool;
    }

    /// <summary>Shows the picked road in the header (null = nothing picked).</summary>
    public void SetRoad(RoadType? road, string? tabLabel = null)
    {
        SetHead(road?.Label ?? "No road", road is null ? null : tabLabel);
        _name.AddThemeColorOverride("font_color", road is null ? UiTheme.TextDim : UiTheme.Text);
        foreach (var child in _facts.GetChildren()) child.QueueFree();
        _facts.Visible = road is not null;
        if (road is null) return;
        _facts.AddChild(UiTheme.Cell("Lanes", road.Badge));
        _facts.AddChild(UiTheme.Cell("Width", road.Summary));
        _facts.AddChild(UiTheme.Cell("Sidewalks", road.Sidewalks == SidewalkLayout.Both ? "Both" : "None"));
    }

    private void SetHead(string name, string? tabLabel)
    {
        _name.Text = name;
        _name.AddThemeColorOverride("font_color", UiTheme.Text);
        _chip?.QueueFree();
        _chip = tabLabel is null ? null : UiTheme.TagChip(tabLabel);
        if (_chip is not null) _head.AddChild(_chip);
    }

    /// <summary>Picks a draw mode (keys 1–4), which also leaves Move.</summary>
    public void SetMode(RoadDrawMode mode)
    {
        Options.Mode = mode;
        Changed();
        MoveRequested?.Invoke(false);
        Refresh();
    }

    /// <summary>Shows Move as on or off (the tool host calls this whenever its tool changes).</summary>
    public void SetMoving(bool on)
    {
        Moving = on;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (m, b) in _modes) b.SetPressedNoSignal(!Moving && m == Options.Mode);
        _move.SetPressedNoSignal(Moving);
        _moveUsage.Visible = Moving;
        _gridBox.Visible = !Moving && Options.Mode == RoadDrawMode.Grid;
    }

    public void ToggleAnarchy() => _anarchy.ButtonPressed = !_anarchy.ButtonPressed;

    /// <summary>Grid mode's blocks from the tool's <c>[ ]</c> keys.</summary>
    public void SetGridBlocks(int cols, int rows)
    {
        Options.GridCols = cols;
        Options.GridRows = rows;
        _cols.SetValueNoSignal(cols);
        _rows.SetValueNoSignal(rows);
        Changed();
    }

    private void RefreshSnaps()
    {
        foreach (var b in _snapButtons.Values) b.Disabled = !Options.Snapping;
    }

    private void Changed() => OptionsChanged?.Invoke(Options);

    private static SpinBox Spin(int min, int max) => new()
    {
        MinValue = min,
        MaxValue = max,
        Step = 1,
        CustomMinimumSize = new Vector2(64, 0),
    };
}
