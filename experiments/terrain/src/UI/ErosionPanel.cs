using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Erosion;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side): runs rain erosion (C++, <see cref="ErosionSim"/>) on the current map as one undo
/// step, and sets which depressions count as lakes. Lakes fill every depression up to where it would spill over, so
/// they follow the ground and move when it's sculpted or eroded.
/// </summary>
public partial class ErosionPanel : PanelContainer
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private ErosionSettings _s = ErosionPresets.Default.ApplyTo(new ErosionSettings());
    private bool _syncing;
    private readonly List<Action> _sync = new();
    private Button _run = null!;
    private Label _status = null!, _lakeInfo = null!;
    private CancellationTokenSource? _job;
    private float _progress;
    private bool _running;

    private Terrain? Terrain => Tools?.Terrain;

    public ErosionPanel()
    {
        Name = "ErosionPanel";
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
        var title = new Label { Text = "Erosion & Lakes", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28), TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(grid);
        BuildRows(grid);

        _run = new Button { Text = "Run Erosion", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 30) };
        _run.Pressed += () => { if (_running) _job?.Cancel(); else Run(); };
        col.AddChild(_run);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
        _status.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_status);
    }

    public override void _Process(double delta)
    {
        if (_running) _status.Text = $"Eroding… {Volatile.Read(ref _progress) * 100f:0}%";
    }

    public void Open()
    {
        Visible = true;
        SyncControls();
        UpdateLakeInfo();
        if (Terrain is { } t) t.LakesChanged += UpdateLakeInfo;
        if (!_running) _status.Text = "Rain runs down the slopes, carving gullies and dropping sediment below. Ctrl/Cmd+Z undoes a run.";
    }

    /// <summary>Hides the panel. A run in progress keeps going and still lands on the terrain.</summary>
    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (Terrain is { } t) t.LakesChanged -= UpdateLakeInfo;
        Closed?.Invoke();
    }

    // --- Controls ---

    private void BuildRows(GridContainer grid)
    {
        Section(grid, "Rain");
        var names = Array.ConvertAll(ErosionPresets.All, p => p.Name);
        var preset = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Fills the settings below (keeps the seed)" };
        foreach (var n in names) preset.AddItem(n);
        preset.ItemSelected += i =>
        {
            if (_syncing) return;
            _s = ErosionPresets.All[i].ApplyTo(_s);
            SyncControls();
            _status.Text = ErosionPresets.All[i].Description;
        };
        _sync.Add(() => preset.Selected = Array.FindIndex(ErosionPresets.All, p => p.Name == _s.Preset));
        Row(grid, "Preset", preset);

        var seed = new SpinBox { MinValue = 0, MaxValue = int.MaxValue, Rounded = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        seed.ValueChanged += v => Edit(() => _s = _s with { Seed = (int)v });
        _sync.Add(() => seed.Value = _s.Seed);
        var seedRow = new HBoxContainer();
        seedRow.AddChild(seed);
        var dice = new Button { Text = "Random", FocusMode = FocusModeEnum.None };
        dice.Pressed += () => seed.Value = Random.Shared.Next(0, int.MaxValue);
        seedRow.AddChild(dice);
        Row(grid, "Seed", seedRow);

        Slider(grid, "Rain", 0.1, 5, 0.1, "{0:0.0}", () => _s.Droplets, v => _s = _s with { Droplets = v },
            "Droplets per cell. More rain carves deeper and takes longer");
        Slider(grid, "Reach", 20, 200, 5, "{0:0} steps", () => _s.Lifetime, v => _s = _s with { Lifetime = (int)v },
            "How far each droplet runs (one cell per step): longer gullies and valleys");
        Slider(grid, "Strength", 0.05, 1, 0.01, "{0:0.00}", () => _s.ErodeSpeed, v => _s = _s with { ErodeSpeed = v },
            "How quickly water picks up ground");
        Slider(grid, "Carry", 1, 12, 0.5, "{0:0.0}", () => _s.Capacity, v => _s = _s with { Capacity = v },
            "How much sediment fast water can carry before it drops it");
        Slider(grid, "Width", 3, 40, 1, "{0:0} m", () => _s.Radius, v => _s = _s with { Radius = v },
            "Width over which a droplet erodes: narrow gullies or broad valleys");
        Slider(grid, "Drain Hollows", 0, 30, 0.5, "{0:0.#} m", () => _s.DrainDepth, v => _s = _s with { DrainDepth = v },
            "Hollows that a channel this deep can drain get one, like a river cutting through; deeper basins stay lakes (0 = off)");
        Slider(grid, "Slump Angle", 25, 60, 1, "{0:0}°", () => _s.TalusDegrees, v => _s = _s with { TalusDegrees = v },
            "After the rain, ground steeper than this slides down (cleans up sharp gully walls)");

        Section(grid, "Lakes");
        var show = new CheckButton { Text = "Show water", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        show.Toggled += on => { if (Terrain is { } t) t.ShowLakes = on; };
        _sync.Add(() => show.SetPressedNoSignal(Terrain?.ShowLakes ?? true));
        Row(grid, "", show);
        Slider(grid, "Min Depth", 0.2, 10, 0.1, "{0:0.0} m", () => Terrain?.LakeSettings.MinDepth ?? 1f,
            v => { if (Terrain is { } t) t.LakeSettings = t.LakeSettings with { MinDepth = v }; },
            "Hollows shallower than this stay dry");
        // Hectares on screen, square metres in the settings.
        Slider(grid, "Min Area", 0.1, 20, 0.1, "{0:0.0} ha", () => (Terrain?.LakeSettings.MinArea ?? 5000f) / 10000f,
            v => { if (Terrain is { } t) t.LakeSettings = t.LakeSettings with { MinArea = v * 10000f }; },
            "Hollows smaller than this stay dry (1 ha = 100 × 100 m)");
        _lakeInfo = new Label { Modulate = UiTheme.TextDim, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _lakeInfo.AddThemeFontSizeOverride("font_size", 12);
        Row(grid, "", _lakeInfo);
    }

    private static void Section(GridContainer grid, string name)
    {
        var label = new Label { Text = name };
        label.AddThemeColorOverride("font_color", UiTheme.Accent);
        grid.AddChild(label);
        grid.AddChild(new Control());
    }

    private static void Row(GridContainer grid, string name, Control value, string tooltip = "")
    {
        grid.AddChild(new Label { Text = name, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass });
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(value);
    }

    private void Slider(GridContainer grid, string name, double min, double max, double step, string format,
        Func<float> get, Action<float> set, string tooltip)
    {
        var row = new HBoxContainer();
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None, TooltipText = tooltip,
        };
        var value = new Label { CustomMinimumSize = new Vector2(64, 0), HorizontalAlignment = HorizontalAlignment.Right };
        row.AddChild(slider);
        row.AddChild(value);
        slider.ValueChanged += v =>
        {
            value.Text = string.Format(format, v);
            Edit(() => set((float)v));
        };
        _sync.Add(() =>
        {
            slider.Value = get();
            value.Text = string.Format(format, slider.Value);
        });
        Row(grid, name, row, tooltip);
    }

    private void Edit(Action change)
    {
        if (_syncing) return;
        change();
    }

    private void SyncControls()
    {
        _syncing = true;
        foreach (var sync in _sync) sync();
        _syncing = false;
    }

    private void UpdateLakeInfo()
    {
        if (Terrain?.Lakes is not { } lakes) { _lakeInfo.Text = "Finding lakes…"; return; }
        float km2 = lakes.WetVertices() * lakes.CellSize * lakes.CellSize / 1e6f;
        _lakeInfo.Text = $"{lakes.Count} lakes, {km2:0.##} km²";
    }

    // --- Run ---

    /// <summary>Erodes a copy of the map on a worker, then puts it on the terrain as one undo step.</summary>
    private void Run()
    {
        if (Terrain is not { Map: { } map } terrain || Tools is null) return;
        var job = _job = new CancellationTokenSource();
        int version = terrain.HeightVersion;
        var settings = _s;
        float? sea = terrain.SeaLevel;
        var sw = Stopwatch.StartNew();
        _progress = 0f;
        _running = true;
        _run.Text = "Cancel";
        Task.Run(() =>
        {
            var copy = new HeightMap(map.Width, map.Depth, map.CellSize);
            map.Data.CopyTo(copy.Data);
            bool done = ErosionSim.Run(copy, settings, sea, job.Token, f => Volatile.Write(ref _progress, f));
            return done ? copy : null;
        }).ContinueWith(t =>
        {
            var error = t.Exception?.GetBaseException();
            var result = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this)) return;
                _running = false;
                _run.Text = "Run Erosion";
                if (error is not null)
                {
                    GD.PushError($"Erosion failed: {error}");
                    _status.Text = error is DllNotFoundException ? error.Message : "Erosion failed (see the log).";
                }
                else if (result is null) _status.Text = "Cancelled. The terrain wasn't changed.";
                else if (Terrain is not { } now || now.Map != map || now.HeightVersion != version)
                    _status.Text = "The terrain changed while eroding, so the result was dropped. Run again.";
                else
                {
                    Tools!.ApplyEroded(result);
                    _status.Text = $"Eroded in {sw.Elapsed.TotalSeconds:0.0} s. Ctrl/Cmd+Z undoes it.";
                }
            }).CallDeferred();
        });
    }

    public override void _ExitTree() => _job?.Cancel();
}
