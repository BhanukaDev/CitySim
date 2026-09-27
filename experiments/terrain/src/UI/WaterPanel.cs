using System;
using System.Linq;
using Godot;
using CitySim.TerrainSystem;
using CitySim.Tools;
using CitySim.WaterSystem;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side) for the water simulation: play/pause, speed, evaporation, open or walled map edges,
/// Fill Hollows (every hollow up to its spill height, and the sea up to sea level), Clear, and live
/// stats. Sources are placed with the Water tab's tools.
/// </summary>
public partial class WaterPanel : PanelContainer
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private Button _play = null!;
    private OptionButton _speed = null!;
    private HSlider _evaporation = null!;
    private Label _evaporationValue = null!, _stats = null!;
    private CheckButton _openEdges = null!, _show = null!;
    private bool _syncing;
    private double _refresh;

    private Terrain? Terrain => Tools?.Terrain;
    private WaterSim? Sim => Tools?.Terrain?.Water;

    public WaterPanel()
    {
        Name = "WaterPanel";
        Visible = false;
        AnchorLeft = AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 1f;
        OffsetLeft = -356f;
        OffsetRight = -16f;
        OffsetTop = 16f;
        OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        var title = new Label { Text = "Water", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28), TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        col.AddChild(grid);

        _play = new Button { FocusMode = FocusModeEnum.None, TooltipText = "Pause or run the water simulation" };
        _play.Pressed += () => Edit(s => s with { Paused = !s.Paused });
        Row(grid, "Simulation", _play);

        _speed = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Simulated seconds per real second (as far as the CPU keeps up)" };
        foreach (var v in WaterSettings.Speeds) _speed.AddItem($"× {v:0}");
        _speed.ItemSelected += i => Edit(s => s with { Speed = WaterSettings.Speeds[i] });
        Row(grid, "Speed", _speed);

        var evap = new HBoxContainer();
        _evaporation = new HSlider
        {
            MinValue = 0, MaxValue = 10, Step = 0.1, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None,
            TooltipText = "Water lost per simulated minute everywhere: water nothing feeds dries up",
        };
        _evaporationValue = new Label { CustomMinimumSize = new Vector2(90, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _evaporation.ValueChanged += v =>
        {
            _evaporationValue.Text = $"{v:0.0} mm/min";
            Edit(s => s with { EvaporationMmPerMin = (float)v });
        };
        evap.AddChild(_evaporation);
        evap.AddChild(_evaporationValue);
        Row(grid, "Evaporation", evap);

        _openEdges = new CheckButton { Text = "Open", FocusMode = FocusModeEnum.None, TooltipText = "Water runs off the map at its edges (off: the edges are walls)" };
        _openEdges.Toggled += on => Edit(s => s with { OpenEdges = on });
        Row(grid, "Map Edges", _openEdges);

        _show = new CheckButton { Text = "Show water", FocusMode = FocusModeEnum.None };
        _show.Toggled += on => { if (Terrain is { } t) t.ShowWater = on; };
        Row(grid, "", _show);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        var fill = new Button { Text = "Fill Hollows", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Fill every hollow up to where it would spill over (Erosion panel: Min Depth/Area), and the sea up to\nsea level, right away. Rivers and lakes fill by flowing. The simulation carries on from there." };
        fill.Pressed += () => Terrain?.FillHollows();
        var clear = new Button { Text = "Clear Water", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Remove all water (sources keep running). Can't be undone." };
        clear.Pressed += () => Sim?.Clear();
        buttons.AddChild(fill);
        buttons.AddChild(clear);
        col.AddChild(buttons);

        _stats = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
        _stats.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_stats);
    }

    public void Open()
    {
        Visible = true;
        Sync();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        Closed?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (!Visible || (_refresh -= delta) > 0) return;
        _refresh = 0.25;
        if (Sim is not { } sim)
        {
            _stats.Text = "No water simulation (see the log).";
            return;
        }
        var st = sim.LastStats;
        float cellArea = sim.CellSize * sim.CellSize;
        var kinds = sim.Sources.GroupBy(s => s.Kind).Select(g => $"{g.Count()} {WaterSource.Label(g.Key).ToLowerInvariant()}");
        _stats.Text =
            $"Sources: {(sim.Sources.Count == 0 ? "none (use the Water tab)" : string.Join(", ", kinds))}\n" +
            $"Water: {st.Volume / 1e6:0.###} million m³ over {st.WetCells * cellArea / 1e6:0.###} km²\n" +
            $"Deepest {st.MaxDepth:0.0} m · fastest {st.MaxSpeed:0.0} m/s\n" +
            $"Running at × {sim.SimRatio:0.#} (sim time {TimeSpan.FromSeconds(sim.SimTime):hh\\:mm\\:ss})\n" +
            (st.Pollution > 0.001 ? $"Pollutant: {st.Pollution:0.#} kg\n" : "") +
            $"{sim.Width}² cells of {sim.CellSize:0.#} m · {st.ActiveTiles} active, {st.SleepingTiles} sleeping tiles · " +
            $"{sim.StepMs:0.00} ms per substep, {st.Substeps} per tick";
        _play.Text = sim.Settings.Paused ? "▶ Run" : "❚❚ Pause";
    }

    private void Sync()
    {
        if (Sim is not { } sim) return;
        _syncing = true;
        var s = sim.Settings;
        _play.Text = s.Paused ? "▶ Run" : "❚❚ Pause";
        int speed = Array.IndexOf(WaterSettings.Speeds, s.Speed);
        _speed.Selected = speed >= 0 ? speed : 3;
        _evaporation.Value = s.EvaporationMmPerMin;
        _evaporationValue.Text = $"{s.EvaporationMmPerMin:0.0} mm/min";
        _openEdges.SetPressedNoSignal(s.OpenEdges);
        _show.SetPressedNoSignal(Terrain?.ShowWater ?? true);
        _syncing = false;
    }

    private void Edit(Func<WaterSettings, WaterSettings> change)
    {
        if (_syncing || Sim is not { } sim) return;
        sim.Settings = change(sim.Settings);
        Sync();
    }

    private static void Row(GridContainer grid, string name, Control value)
    {
        grid.AddChild(new Label { Text = name });
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(value);
    }
}
