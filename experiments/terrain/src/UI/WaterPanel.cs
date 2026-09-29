using System;
using System.Linq;
using Godot;
using CitySim.TerrainSystem;
using CitySim.Tools;
using CitySim.WaterSystem;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side) for the water simulation: play/pause, speed, evaporation, open or walled map edges,
/// wet-ground paint, flow arrows, Add Lake Sources (a Lake source in every hollow, filled at once), Clear, and live
/// stats. Sources are placed with the Water tab's tools.
/// </summary>
public partial class WaterPanel : PanelContainer
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private Button _play = null!;
    private OptionButton _speed = null!;
    private HSlider _evaporation = null!, _paint = null!, _fade = null!, _rainIntensity = null!;
    private Label _evaporationValue = null!, _paintValue = null!, _fadeValue = null!, _rainValue = null!, _stats = null!;
    private CheckButton _openEdges = null!, _show = null!, _arrows = null!, _rain = null!;
    private bool _syncing;
    private double _refresh, _noteTime;
    private string _note = "";

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

        _paint = Slider(grid, "Wet Paint", 0, 120, 1, "Simulated minutes of water on the ground until the theme's Wet ground\nmaterial fully covers it (0 = off)",
            out _paintValue, v => Edit(s => s with { PaintMinutes = (float)v }));
        _fade = Slider(grid, "Paint Fades", 0, 168, 1, "Simulated hours until dry ground loses its wet paint (0 = never)",
            out _fadeValue, v => Edit(s => s with { PaintFadeHours = (float)v }));

        _rain = new CheckButton { Text = "Raining", FocusMode = FocusModeEnum.None,
            TooltipText = "The ground wets over ~10 sim-minutes while it rains and dries over ~2 sim-hours after.\nLook only for now: no water is added to the simulation." };
        _rain.Toggled += on => { if (!_syncing && Terrain is { } t) t.Weather.Raining = on; };
        Row(grid, "Rain", _rain);
        _rainIntensity = Slider(grid, "Intensity", 0, 1, 0.05, "How wet the ground gets while it rains (puddles on flat ground from ~0.55)",
            out _rainValue, v =>
            {
                if (Terrain is { } t) t.Weather.Intensity = (float)v;
                _rainValue.Text = $"{v:0.00}";
            });

        _openEdges = new CheckButton { Text = "Open", FocusMode = FocusModeEnum.None, TooltipText = "Water runs off the map at its edges (off: the edges are walls)" };
        _openEdges.Toggled += on => Edit(s => s with { OpenEdges = on });
        Row(grid, "Map Edges", _openEdges);

        _show = new CheckButton { Text = "Show water", FocusMode = FocusModeEnum.None };
        _show.Toggled += on => { if (Terrain is { } t) t.ShowWater = on; };
        Row(grid, "", _show);

        _arrows = new CheckButton { Text = "Flow arrows", FocusMode = FocusModeEnum.None,
            TooltipText = "Arrows along the water's flow (longer = faster). Always on while a Water tool is out." };
        _arrows.Toggled += on => { if (Terrain is { } t) t.FlowArrows = on; };
        Row(grid, "", _arrows);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        var fill = new Button { Text = "Add Lake Sources", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Put a Lake source in every hollow that has none (Erosion panel: Min Depth/Area), sized for it,\nand fill those lakes and the sea right away. Delete a source (right-click, Water tab) to remove its lake.\nOne undo step." };
        fill.Pressed += () => Tools?.AddLakeSources(n =>
        {
            _note = n == 0 ? "Every hollow already has a source." : $"Added {n} lake source{(n == 1 ? "" : "s")}.";
            _noteTime = 5;
        });
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
        _noteTime -= delta;
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
        _stats.Text = (_noteTime > 0 ? _note + "\n" : "") +
            $"Sources: {(sim.Sources.Count == 0 ? "none (use the Water tab)" : string.Join(", ", kinds))}\n" +
            $"Water: {st.Volume / 1e6:0.###} million m³ over {st.WetCells * cellArea / 1e6:0.###} km²\n" +
            $"Deepest {st.MaxDepth:0.0} m · fastest {st.MaxSpeed:0.0} m/s\n" +
            $"Running at × {sim.SimRatio:0.#} (sim time {TimeSpan.FromSeconds(sim.SimTime):hh\\:mm\\:ss})\n" +
            (st.Pollution > 0.001 ? $"Pollutant: {st.Pollution:0.#} kg\n" : "") +
            (Terrain is { } t && (t.Weather.Raining || t.Weather.Wetness > 0.005f)
                ? $"Ground wetness {t.Weather.Wetness:0.00}{(t.Weather.Raining ? " (raining)" : " (drying)")}\n" : "") +
            $"{sim.Width}² cells of {sim.CellSize:0.#} m · {st.ActiveTiles} active, {st.SleepingTiles} sleeping tiles · " +
            $"{st.AllocatedTiles} in memory ({st.AllocatedMb:0} MB) · " +
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
        _paint.Value = s.PaintMinutes;
        _paintValue.Text = s.PaintMinutes > 0 ? $"{s.PaintMinutes:0} min" : "off";
        _fade.Value = s.PaintFadeHours;
        _fadeValue.Text = s.PaintFadeHours > 0 ? $"{s.PaintFadeHours:0} h" : "never";
        _arrows.SetPressedNoSignal(Terrain?.FlowArrows ?? false);
        _openEdges.SetPressedNoSignal(s.OpenEdges);
        _show.SetPressedNoSignal(Terrain?.ShowWater ?? true);
        if (Terrain is { } terrain)
        {
            _rain.SetPressedNoSignal(terrain.Weather.Raining);
            _rainIntensity.Value = terrain.Weather.Intensity;
            _rainValue.Text = $"{terrain.Weather.Intensity:0.00}";
        }
        _syncing = false;
    }

    private void Edit(Func<WaterSettings, WaterSettings> change)
    {
        if (_syncing || Sim is not { } sim) return;
        sim.Settings = change(sim.Settings);
        Sync();
    }

    private HSlider Slider(GridContainer grid, string name, double min, double max, double step, string tip, out Label value,
        Action<double> changed)
    {
        var box = new HBoxContainer();
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None, TooltipText = tip,
        };
        value = new Label { CustomMinimumSize = new Vector2(90, 0), HorizontalAlignment = HorizontalAlignment.Right };
        slider.ValueChanged += v => { if (!_syncing) changed(v); };
        box.AddChild(slider);
        box.AddChild(value);
        Row(grid, name, box);
        return slider;
    }

    private static void Row(GridContainer grid, string name, Control value)
    {
        grid.AddChild(new Label { Text = name });
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(value);
    }
}
