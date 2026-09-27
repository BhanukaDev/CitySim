using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Erosion;
using CitySim.TerrainSystem.Generation;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side) for making the terrain: preset, source (noise, a placed heightmap image, or flat), a
/// land/sea shape, and a top-down preview that updates as you drag. With Live on, the 3D terrain regenerates shortly
/// after the last change; otherwise Apply does it. A run of changes is one undo step. Painted ground is kept.
/// </summary>
public partial class GeneratorPanel : PanelContainer
{
    private const int PreviewVerts = 257;
    private const double LiveDelay = 0.3;

    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private GenSettings _s = new();
    private bool _syncing;
    private readonly List<Action> _sync = new();
    private readonly List<Control> _noiseRows = new(), _imageRows = new(), _flatRows = new(), _shapeRows = new(), _coastRows = new();
    private Label _shapeSizeLabel = null!;
    private Label _fileName = null!;
    private Label _status = null!;
    private CheckButton _live = null!;
    private TextureRect _preview = null!;
    private Image? _previewImage;
    private ImageTexture? _previewTexture;
    private double _liveTimer = -1;
    private CancellationTokenSource? _job;
    private int _jobId;
    private float _progress;
    private bool _generating;

    public GeneratorPanel()
    {
        Name = "GeneratorPanel";
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
        var title = new Label { Text = "Terrain Generator", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28), TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 300),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TooltipText = "Top-down preview, north up. Blue: lakes, and below sea level.",
        };
        col.AddChild(_preview);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(grid);
        BuildRows(grid);

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        _live = new CheckButton { Text = "Live", ButtonPressed = true, FocusMode = FocusModeEnum.None, TooltipText = "Update the 3D terrain as you change settings" };
        footer.AddChild(_live);
        var apply = new Button { Text = "Apply", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
        apply.Pressed += Apply;
        footer.AddChild(apply);
        col.AddChild(footer);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
        _status.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_status);
    }

    public override void _Process(double delta)
    {
        if (_generating) _status.Text = $"Generating… {Volatile.Read(ref _progress) * 100f:0}%";
        if (_liveTimer < 0) return;
        _liveTimer -= delta;
        if (_liveTimer < 0) Apply();
    }

    /// <summary>Shows the panel with the current map's settings.</summary>
    public void Open()
    {
        if (Tools?.Terrain is { Map: { } map } terrain)
            _s = terrain.Settings with { Cells = map.Width - 1, CellSize = map.CellSize };
        Visible = true;
        SyncControls();
        UpdatePreview();
        _status.Text = "Changes replace the heights (painted ground is kept). Ctrl/Cmd+Z undoes.";
    }

    public void Close()
    {
        if (!Visible) return;
        // A pending live update would be lost otherwise.
        if (_liveTimer >= 0) Apply();
        _liveTimer = -1;
        Visible = false;
        Tools?.CommitGenerated();
        Closed?.Invoke();
    }

    // --- Controls ---

    private void BuildRows(GridContainer grid)
    {
        Choice(grid, "Preset", Array.ConvertAll(GenPresets.All, p => p.Name), () => Array.FindIndex(GenPresets.All, p => p.Name == _s.Preset), i =>
        {
            _s = GenPresets.All[i].ApplyTo(_s);
            _status.Text = GenPresets.All[i].Description;
        }, "Fills the hills and shape settings (keeps size, seed and heightmap)");
        Choice(grid, "Source", ["Noise", "Heightmap image", "Flat"], () => (int)_s.Source, i => _s = _s with { Source = (TerrainSource)i });
        Choice(grid, "Size", Array.ConvertAll(MapSize.All, z => z.Label), () => Array.FindIndex(MapSize.All, z => z.Cells == _s.Cells),
            i => _s = _s with { Cells = MapSize.All[i].Cells, CellSize = GenSettings.DefaultCellSize }, "A new size starts a new map: undo history and painted ground are cleared");

        Section(grid, "Hills", _noiseRows);
        var seed = new SpinBox { MinValue = 0, MaxValue = int.MaxValue, Rounded = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        seed.ValueChanged += v => Edit(() => _s = _s with { Noise = _s.Noise with { Seed = (int)v } });
        _sync.Add(() => seed.Value = _s.Noise.Seed);
        var seedRow = new HBoxContainer();
        seedRow.AddChild(seed);
        var dice = new Button { Text = "Random", FocusMode = FocusModeEnum.None };
        dice.Pressed += () => seed.Value = Random.Shared.Next(0, int.MaxValue);
        seedRow.AddChild(dice);
        Row(grid, "Seed", seedRow, _noiseRows);
        Slider(grid, "Height", 10, 800, 5, "{0:0} m", () => _s.Noise.HeightScale, v => _s = _s with { Noise = _s.Noise with { HeightScale = v } }, _noiseRows,
            "Height of the tallest hills above the ground level");
        Slider(grid, "Ground Level", -50, 200, 1, "{0:0} m", () => _s.Noise.BaseHeight, v => _s = _s with { Noise = _s.Noise with { BaseHeight = v } }, _noiseRows,
            "Height of the lowest land");
        Slider(grid, "Lowlands", 0, 1, 0.01, "{0:0%}", () => _s.Noise.Flatness, v => _s = _s with { Noise = _s.Noise with { Flatness = v } }, _noiseRows,
            "How much of the map is wide, flat lowland to build on");
        // Shown as the size of the largest features (1 / frequency).
        Slider(grid, "Feature Size", 200, 3000, 10, "{0:0} m", () => 1f / _s.Noise.Frequency, v => _s = _s with { Noise = _s.Noise with { Frequency = 1f / v } }, _noiseRows,
            "Width of the largest hills and valleys");
        Slider(grid, "Roughness", 0.3, 0.6, 0.01, "{0:0.00}", () => _s.Noise.Gain, v => _s = _s with { Noise = _s.Noise with { Gain = v } }, _noiseRows,
            "How much small detail the hills get");
        Slider(grid, "Warp", 0, 300, 5, "{0:0} m", () => _s.Noise.WarpAmplitude, v => _s = _s with { Noise = _s.Noise with { WarpAmplitude = v } }, _noiseRows,
            "Twists the hills into winding ridges and valleys");
        Slider(grid, "Max Slope", 15, 90, 1, "{0:0}°", () => _s.Noise.MaxSlope, v => _s = _s with { Noise = _s.Noise with { MaxSlope = v } }, _noiseRows,
            "Steeper ground is cut down to this angle (90° = no limit)");

        Section(grid, "Heightmap", _imageRows);
        var fileRow = new HBoxContainer();
        _fileName = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Modulate = UiTheme.TextDim };
        fileRow.AddChild(_fileName);
        var browse = new Button { Text = "Browse…", FocusMode = FocusModeEnum.None, TooltipText = "16-bit greyscale PNG, or square 16-bit RAW (.r16/.raw, little-endian)" };
        browse.Pressed += Browse;
        fileRow.AddChild(browse);
        _sync.Add(() => _fileName.Text = _s.Image is null ? "No file" : $"{_s.ImageName} ({_s.Image.Width}×{_s.Image.Height})");
        Row(grid, "File", fileRow, _imageRows);
        Number(grid, "Lowest", () => _s.Placement.Lowest, v => _s = _s with { Placement = _s.Placement with { Lowest = v } }, _imageRows, "Height of black (0)");
        Number(grid, "Highest", () => _s.Placement.Highest, v => _s = _s with { Placement = _s.Placement with { Highest = v } }, _imageRows, "Height of white (65535)");
        var rotation = Slider(grid, "Rotation", 0, 360, 1, "{0:0}°", () => _s.Placement.Rotation, v => _s = _s with { Placement = _s.Placement with { Rotation = v } }, _imageRows);
        var turn = new Button { Text = "+90°", FocusMode = FocusModeEnum.None };
        turn.Pressed += () => rotation.Value = (rotation.Value + 90) % 360;
        rotation.GetParent().AddChild(turn);
        Slider(grid, "Scale", 0.1, 4, 0.01, "{0:0%}", () => _s.Placement.Scale, v => _s = _s with { Placement = _s.Placement with { Scale = v } }, _imageRows,
            "100%: the image covers the map. Smaller shrinks it (use Tile to repeat it)");
        Slider(grid, "Offset X", -1, 1, 0.01, "{0:0%}", () => _s.Placement.OffsetX, v => _s = _s with { Placement = _s.Placement with { OffsetX = v } }, _imageRows,
            "Moves the image east (+) or west (-), in map widths");
        Slider(grid, "Offset Z", -1, 1, 0.01, "{0:0%}", () => _s.Placement.OffsetZ, v => _s = _s with { Placement = _s.Placement with { OffsetZ = v } }, _imageRows,
            "Moves the image south (+) or north (-), in map widths");
        Choice(grid, "Edges", ["Clamp", "Tile", "Mirror", "Fill (lowest)"], () => (int)_s.Placement.Edges,
            i => _s = _s with { Placement = _s.Placement with { Edges = (EdgeMode)i } }, "What's outside the image: its edge stretched, repeats, mirrored repeats, or flat at Lowest", _imageRows);

        Section(grid, "Flat", _flatRows);
        Slider(grid, "Height", 0, 500, 1, "{0:0} m", () => _s.FlatHeight, v => _s = _s with { FlatHeight = v }, _flatRows);

        Section(grid, "Shape", null);
        Choice(grid, "Shape", ["None", "Island", "Coast", "Archipelago"], () => (int)_s.Shape.Kind,
            i => _s = _s with { Shape = _s.Shape with { Kind = (ShapeKind)i } }, "Blends the land down to a sea floor around a shape");
        var size = Slider(grid, "Size", 0.05, 1.2, 0.01, "{0:0%}", () => _s.Shape.Size, v => _s = _s with { Shape = _s.Shape with { Size = v } }, _shapeRows);
        _shapeSizeLabel = (Label)grid.GetChild(size.GetParent().GetIndex() - 1);
        Slider(grid, "Sea Side", 0, 360, 5, "{0:0}°", () => _s.Shape.Direction, v => _s = _s with { Shape = _s.Shape with { Direction = v } }, _coastRows,
            "Which side the sea is on: 0° north, 90° east, 180° south, 270° west");
        Slider(grid, "Shore Width", 0.02, 0.5, 0.01, "{0:0.00}", () => _s.Shape.EdgeWidth, v => _s = _s with { Shape = _s.Shape with { EdgeWidth = v } }, _shapeRows,
            "Width of the gentlest beaches and sea shelves (in map half-widths); steep shores use a fraction of it");
        Slider(grid, "Coast Roughness", 0, 1.5, 0.01, "{0:0.00}", () => _s.Shape.Roughness, v => _s = _s with { Shape = _s.Shape with { Roughness = v } }, _shapeRows,
            "How much the coastline wanders: bays and headlands");
        Slider(grid, "Gentle Shores", 0, 1, 0.01, "{0:0%}", () => _s.GentleShores, v => _s = _s with { GentleShores = v }, _shapeRows,
            "Share of sea shores that are gentle beaches; the rest are steep banks");
        Slider(grid, "Sea Level", -50, 200, 1, "{0:0} m", () => _s.SeaLevel, v => _s = _s with { SeaLevel = v }, _shapeRows,
            "Height the shore blends down to (no water yet; sand shows below ~9 m)");
        Slider(grid, "Sea Depth", 0, 200, 1, "{0:0} m", () => _s.Shape.SeaDepth, v => _s = _s with { Shape = _s.Shape with { SeaDepth = v } }, _shapeRows,
            "How far below sea level the sea floor is");
    }

    private void Section(GridContainer grid, string name, List<Control>? group)
    {
        var label = new Label { Text = name };
        label.AddThemeColorOverride("font_color", UiTheme.Accent);
        var spacer = new Control();
        grid.AddChild(label);
        grid.AddChild(spacer);
        group?.AddRange([label, spacer]);
    }

    private static void Row(GridContainer grid, string name, Control value, List<Control>? group, string tooltip = "")
    {
        var label = new Label { Text = name, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass };
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(label);
        grid.AddChild(value);
        group?.AddRange([label, value]);
    }

    /// <summary>A slider with its value shown beside it. Returns the slider (its parent is the row).</summary>
    private HSlider Slider(GridContainer grid, string name, double min, double max, double step, string format,
        Func<float> get, Action<float> set, List<Control>? group, string tooltip = "")
    {
        var row = new HBoxContainer();
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None, TooltipText = tooltip,
        };
        var value = new Label { CustomMinimumSize = new Vector2(52, 0), HorizontalAlignment = HorizontalAlignment.Right };
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
        Row(grid, name, row, group, tooltip);
        return slider;
    }

    private void Number(GridContainer grid, string name, Func<float> get, Action<float> set, List<Control>? group, string tooltip)
    {
        var box = new SpinBox { MinValue = -1000, MaxValue = 9000, Step = 1, Suffix = "m", TooltipText = tooltip };
        box.ValueChanged += v => Edit(() => set((float)v));
        _sync.Add(() => box.Value = get());
        Row(grid, name, box, group, tooltip);
    }

    private void Choice(GridContainer grid, string name, string[] items, Func<int> get, Action<int> set, string tooltip = "", List<Control>? group = null)
    {
        var option = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = tooltip };
        foreach (var item in items) option.AddItem(item);
        option.ItemSelected += i => Edit(() => set((int)i), resync: true);
        _sync.Add(() => option.Selected = get());
        Row(grid, name, option, group, tooltip);
    }

    /// <summary>Applies a control change to the settings, then refreshes the preview and (with Live) schedules a 3D update.</summary>
    private void Edit(Action change, bool resync = false)
    {
        if (_syncing) return;
        change();
        if (resync) SyncControls();
        UpdateVisibility();
        UpdatePreview();
        if (_live.ButtonPressed) _liveTimer = LiveDelay;
    }

    private void SyncControls()
    {
        _syncing = true;
        foreach (var sync in _sync) sync();
        _syncing = false;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        foreach (var c in _noiseRows) c.Visible = _s.Source == TerrainSource.Noise;
        foreach (var c in _imageRows) c.Visible = _s.Source == TerrainSource.Heightmap;
        foreach (var c in _flatRows) c.Visible = _s.Source == TerrainSource.Flat;
        foreach (var c in _shapeRows) c.Visible = _s.Shape.Kind != ShapeKind.None;
        foreach (var c in _coastRows) c.Visible = _s.Shape.Kind == ShapeKind.Coast;
        _shapeSizeLabel.Text = _s.Shape.Kind switch
        {
            ShapeKind.Island => "Island Size",
            ShapeKind.Coast => "Land Share",
            _ => "Land Amount",
        };
    }

    private void Browse()
    {
        var dialog = MapFiles.HeightmapDialog(save: false, path =>
        {
            var image = MapFiles.ReadHeightmap(path, out string? error);
            if (image is null) { _status.Text = error ?? ""; return; }
            var placement = image.Range is var (min, max) ? _s.Placement with { Lowest = min, Highest = max } : _s.Placement;
            _s = _s with { Source = TerrainSource.Heightmap, Image = image, ImageName = System.IO.Path.GetFileName(path), Placement = placement };
            SyncControls();
            Edit(() => { });
        });
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // --- Preview and apply ---

    /// <summary>Redraws the top-down preview: hillshade over a height tint, with ground below sea level in blue when a shape is on.</summary>
    private void UpdatePreview()
    {
        var sw = Stopwatch.StartNew();
        var map = TerrainGen.Preview(_s, PreviewVerts);
        var (min, max) = map.GetRange();
        bool sea = _s.Shape.Kind != ShapeKind.None;
        var lakes = PreviewLakes(map, sea);
        float landMin = sea ? MathF.Max(min, _s.SeaLevel) : min;
        float span = MathF.Max(max - landMin, 1f);
        var light = System.Numerics.Vector3.Normalize(new(-1f, 1.4f, -1f));
        var bytes = new byte[PreviewVerts * PreviewVerts * 3];
        for (int z = 0; z < PreviewVerts; z++)
            for (int x = 0; x < PreviewVerts; x++)
            {
                float h = map[x, z];
                var n = map.GetNormal(x, z);
                float shade = 0.55f + 0.6f * MathF.Max(0f, System.Numerics.Vector3.Dot(n, light));
                Color c;
                float lake = lakes?.LevelAt(x, z) ?? float.NaN;
                if (!float.IsNaN(lake))
                {
                    float depth = Mathf.Clamp((lake - h) / 6f, 0f, 1f);
                    c = new Color(0.36f, 0.62f, 0.66f).Lerp(new Color(0.10f, 0.28f, 0.42f), depth);
                    shade = 0.9f + 0.1f * shade;
                }
                else if (sea && h < _s.SeaLevel)
                {
                    float depth = Mathf.Clamp((_s.SeaLevel - h) / MathF.Max(_s.Shape.SeaDepth, 1f), 0f, 1f);
                    c = new Color(0.38f, 0.66f, 0.78f).Lerp(new Color(0.12f, 0.28f, 0.48f), depth);
                    shade = 0.85f + 0.15f * shade;
                }
                else
                {
                    float t = Mathf.Clamp((h - landMin) / span, 0f, 1f);
                    c = t < 0.5f
                        ? new Color(0.45f, 0.62f, 0.32f).Lerp(new Color(0.62f, 0.58f, 0.38f), t * 2f)
                        : new Color(0.62f, 0.58f, 0.38f).Lerp(new Color(0.92f, 0.92f, 0.9f), (t - 0.5f) * 2f);
                    if (sea && h < _s.SeaLevel + 3f) c = new Color(0.86f, 0.80f, 0.60f); // beach
                }
                int i = (z * PreviewVerts + x) * 3;
                bytes[i] = (byte)Mathf.Clamp(c.R * shade * 255f, 0f, 255f);
                bytes[i + 1] = (byte)Mathf.Clamp(c.G * shade * 255f, 0f, 255f);
                bytes[i + 2] = (byte)Mathf.Clamp(c.B * shade * 255f, 0f, 255f);
            }

        if (_previewImage is null)
        {
            _previewImage = Image.CreateFromData(PreviewVerts, PreviewVerts, false, Image.Format.Rgb8, bytes);
            _previewTexture = ImageTexture.CreateFromImage(_previewImage);
            _preview.Texture = _previewTexture;
        }
        else
        {
            _previewImage.SetData(PreviewVerts, PreviewVerts, false, Image.Format.Rgb8, bytes);
            _previewTexture!.Update(_previewImage);
        }
        _preview.TooltipText = $"Top-down preview, north up. {min:0}–{max:0} m" + (sea ? ", blue below sea level" : "") +
                               (lakes is null ? "" : $", {lakes.Count} lakes") + $" ({sw.ElapsedMilliseconds} ms)";
    }

    private bool _previewLakesFailed;

    /// <summary>Lakes on the preview grid, with the terrain's lake settings (null if the erosion library is missing).</summary>
    private LakeMap? PreviewLakes(HeightMap map, bool sea)
    {
        if (_previewLakesFailed) return null;
        try
        {
            return Lakes.Find(map, Tools?.Terrain?.LakeSettings ?? new LakeSettings(), sea ? _s.SeaLevel : null);
        }
        catch (DllNotFoundException e)
        {
            _previewLakesFailed = true;
            GD.PushWarning($"Generator preview: no lakes ({e.Message})");
            return null;
        }
    }

    /// <summary>Generates the full map on a worker thread and puts it on the terrain; a newer Apply supersedes an older one.</summary>
    private void Apply()
    {
        _liveTimer = -1;
        if (Tools is null) return;
        if (_s.Source == TerrainSource.Heightmap && _s.Image is null) { _status.Text = "Pick a heightmap file first."; return; }
        _job?.Cancel();
        var job = _job = new CancellationTokenSource();
        int id = ++_jobId;
        var settings = _s;
        var sw = Stopwatch.StartNew();
        _status.Text = "Generating…";
        _progress = 0f;
        _generating = true;
        Task.Run(() => TerrainGen.Create(settings, job.Token, f => { if (id == _jobId) Volatile.Write(ref _progress, f); }), job.Token).ContinueWith(t =>
        {
            Callable.From(() => { if (IsInstanceValid(this) && id == _jobId) _generating = false; }).CallDeferred();
            if (t.IsFaulted)
            {
                GD.PushError($"Terrain generation failed: {t.Exception}");
                Callable.From(() => { if (IsInstanceValid(this)) _status.Text = "Generation failed (see the log)."; }).CallDeferred();
            }
            if (t.Status != TaskStatus.RanToCompletion) return;
            var map = t.Result;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || id != _jobId || Tools is null) return;
                Tools.ApplyGenerated(map, settings);
                _status.Text = $"Generated in {sw.ElapsedMilliseconds} ms. Ctrl/Cmd+Z undoes all changes since opening.";
            }).CallDeferred();
        });
    }

    public override void _ExitTree() => _job?.Cancel();
}
