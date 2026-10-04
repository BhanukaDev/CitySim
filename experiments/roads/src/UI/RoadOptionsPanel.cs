using System;
using System.Collections.Generic;
using CitySim.Roads;
using Godot;

namespace CitySim.UI;

/// <summary>How the road tool draws: the four modes of the splines Draw tool (1–4).</summary>
public enum RoadDrawMode { Straight, Curve, Freehand, Grid }

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
/// The always-open panel next to the Roads tray: the picked road and what it comes with, tool mode, grid blocks (Grid
/// mode only), snapping and Anarchy. Only options the splines tool already supports.
/// </summary>
public partial class RoadOptionsPanel : PanelContainer
{
    public const float PanelWidth = 300f;

    private readonly Label _name = UiTheme.Label("", 16);
    private readonly Label _comes = UiTheme.Label("", 12, dim: true);
    private readonly Dictionary<RoadDrawMode, Button> _modes = new();
    private readonly VBoxContainer _gridBox = new();
    private readonly SpinBox _cols = Spin(1, 40);
    private readonly SpinBox _rows = Spin(1, 40);
    private readonly OptionButton _fit = new() { FocusMode = FocusModeEnum.None };
    private readonly CheckButton _snapping = new() { Text = "Snapping", FocusMode = FocusModeEnum.None };
    private readonly Dictionary<RoadSnaps, Button> _snapButtons = new();
    private readonly CheckButton _anarchy = new() { Text = "Anarchy", FocusMode = FocusModeEnum.None };

    public RoadToolOptions Options { get; } = new();

    public event Action<RoadToolOptions>? OptionsChanged;

    public RoadOptionsPanel()
    {
        Name = "RoadOptionsPanel";
        CustomMinimumSize = new Vector2(PanelWidth, 0);
        SizeFlagsVertical = SizeFlags.ShrinkEnd;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        AddChild(col);

        col.AddChild(_name);
        _comes.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _comes.CustomMinimumSize = new Vector2(PanelWidth - 24, 0);
        col.AddChild(_comes);
        col.AddChild(new HSeparator());

        col.AddChild(UiTheme.Section("Tool mode"));
        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 4);
        col.AddChild(modeRow);
        var modeGroup = new ButtonGroup();
        (RoadDrawMode Mode, string Label, string Icon, string Tip)[] modes =
        [
            (RoadDrawMode.Straight, "Straight", "line", "Straight (1): click points; corners round to the road's radius"),
            (RoadDrawMode.Curve, "Curve", "vector-spline", "Curve (2): start, bend, end"),
            (RoadDrawMode.Freehand, "Freehand", "scribble", "Freehand (3): hold and drag"),
            (RoadDrawMode.Grid, "Grid", "grid-4x4", "Grid (4): corner, width, depth"),
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
                CustomMinimumSize = new Vector2(66, 56),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            b.AddThemeConstantOverride("icon_max_width", 22);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Pressed += () => SetMode(mode);
            modeRow.AddChild(b);
            _modes[mode] = b;
        }

        _gridBox.AddThemeConstantOverride("separation", 4);
        col.AddChild(_gridBox);
        var blocks = new HBoxContainer();
        blocks.AddThemeConstantOverride("separation", 6);
        _gridBox.AddChild(blocks);
        var bl = UiTheme.Label("Blocks", 13);
        bl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        blocks.AddChild(bl);
        _cols.Value = Options.GridCols;
        _rows.Value = Options.GridRows;
        _cols.TooltipText = "Blocks along the first edge ([ ])";
        _rows.TooltipText = "Blocks across (Shift+[ ])";
        blocks.AddChild(_cols);
        blocks.AddChild(UiTheme.Label("×", 13, dim: true));
        blocks.AddChild(_rows);
        _cols.ValueChanged += v => { Options.GridCols = (int)v; Changed(); };
        _rows.ValueChanged += v => { Options.GridRows = (int)v; Changed(); };
        _fit.AddItem("Even split", (int)RoadGridFit.Even);
        _fit.AddItem("Lot steps", (int)RoadGridFit.LotSteps);
        _fit.TooltipText = "Even split: equal blocks.\nLot steps: every block holds whole lots.";
        _fit.ItemSelected += i => { Options.GridFit = (RoadGridFit)_fit.GetItemId((int)i); Changed(); };
        _gridBox.AddChild(_fit);

        col.AddChild(new HSeparator());
        var snapHead = new HBoxContainer();
        col.AddChild(snapHead);
        var sl = UiTheme.Section("Snapping");
        sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        snapHead.AddChild(sl);
        _snapping.ButtonPressed = Options.Snapping;
        _snapping.Text = "";
        _snapping.TooltipText = "All snapping on or off";
        _snapping.Toggled += on => { Options.Snapping = on; RefreshSnaps(); Changed(); };
        snapHead.AddChild(_snapping);
        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 4);
        chips.AddThemeConstantOverride("v_separation", 4);
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
            b.Toggled += on => { Options.Snaps = on ? Options.Snaps | flag : Options.Snaps & ~flag; Changed(); };
            chips.AddChild(b);
            _snapButtons[flag] = b;
        }
        col.AddChild(UiTheme.Label("Hold Ctrl for 15° steps, Ctrl+Shift for 5°.", 11, dim: true));

        col.AddChild(new HSeparator());
        var anarchyRow = new HBoxContainer();
        col.AddChild(anarchyRow);
        var al = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        al.AddThemeConstantOverride("separation", 0);
        al.AddChild(UiTheme.Label("Anarchy", 13));
        al.AddChild(UiTheme.Label("Ignore overlap and curve limits · Ctrl+A", 11, dim: true));
        anarchyRow.AddChild(al);
        _anarchy.Text = "";
        _anarchy.Toggled += on => { Options.Anarchy = on; Changed(); };
        anarchyRow.AddChild(_anarchy);

        SetRoad(null);
        SetMode(Options.Mode);
    }

    /// <summary>Shows the picked road in the header (null = nothing picked).</summary>
    public void SetRoad(RoadType? road)
    {
        _name.Text = road?.Label ?? "No road picked";
        _name.AddThemeColorOverride("font_color", road is null ? UiTheme.TextDim : UiTheme.Text);
        _comes.Text = road is null ? "Pick a road from the tray." : $"Comes with {road.ComesWith}.";
    }

    public void SetMode(RoadDrawMode mode)
    {
        Options.Mode = mode;
        foreach (var (m, b) in _modes) b.SetPressedNoSignal(m == mode);
        _gridBox.Visible = mode == RoadDrawMode.Grid;
        Changed();
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
        CustomMinimumSize = new Vector2(70, 0),
    };
}
