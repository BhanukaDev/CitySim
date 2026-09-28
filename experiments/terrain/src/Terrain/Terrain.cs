using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem.Erosion;
using CitySim.TerrainSystem.Generation;
using CitySim.TerrainSystem.Themes;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// Owns the heightmap and its render copy in Terrain3D (<see cref="Terrain3DBridge"/>), and answers terrain queries
/// (height, normal, slope, bounds) for other systems such as the camera, roads and buildings.
/// </summary>
[Tool]
public partial class Terrain : Node3D
{
    /// <summary>Cell size for maps made from the menu (new, flat, imported).</summary>
    public const float DefaultCellSize = GenSettings.DefaultCellSize;

    [ExportGroup("Size")]
    [Export(PropertyHint.Range, "16,4096,16")] public int CellsX { get; set; } = 1024;
    [Export(PropertyHint.Range, "16,4096,16")] public int CellsZ { get; set; } = 1024;
    [Export(PropertyHint.Range, "0.5,16,0.5,suffix:m")] public float CellSize { get; set; } = DefaultCellSize;

    [ExportGroup("Generation")]
    [Export] public int Seed { get; set; } = 1337;
    [Export(PropertyHint.Range, "0.0001,0.02,0.0001")] public float Frequency { get; set; } = 0.0015f;
    [Export(PropertyHint.Range, "1,10")] public int Octaves { get; set; } = 6;
    [Export(PropertyHint.Range, "0,1000,1,suffix:m")] public float HeightScale { get; set; } = 250f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Flatness { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,500,1,suffix:m")] public float WarpAmplitude { get; set; } = 60f;
    [Export(PropertyHint.Range, "0.2,0.6,0.01")] public float Gain { get; set; } = 0.42f;
    [Export(PropertyHint.Range, "0,8")] public int SmoothPasses { get; set; } = 2;

    [ExportGroup("Rendering")]
    /// <summary>
    /// The look for new maps (a loaded map uses the theme it was saved with). Empty: the default theme
    /// (<see cref="ThemeLibrary.DefaultId"/>). Terrain3D draws with the theme's shader; see <see cref="SetTheme"/>.
    /// In the Godot editor, setting it switches the viewport's terrain to that theme, and edits to the theme (uniforms,
    /// tints, tiling, slot settings) show within half a second: a live preview while making a theme.
    /// </summary>
    [Export] public TerrainTheme? DefaultTheme
    {
        get => _defaultTheme;
        set
        {
            _defaultTheme = value;
            if (Engine.IsEditorHint() && value is not null && _render is not null) SetTheme(value);
        }
    }
    private TerrainTheme? _defaultTheme;
    /// <summary>Fog-only material for the ring around the map; gets the theme's edge fog settings.</summary>
    [Export] public Material? SkirtMaterial { get; set; }
    /// <summary>Water surfaces (<c>water.gdshader</c>); each drawn page gets a copy with its own data texture.</summary>
    [Export] public Material? WaterMaterial { get; set; }

    /// <summary>The current map's theme: its shader, materials and erosion slots.</summary>
    public TerrainTheme? Theme { get; private set; }
    /// <summary>Raised when <see cref="Theme"/> changes (or is reloaded), so the Paint tool can list its materials.</summary>
    public event System.Action? ThemeChanged;

    /// <summary>Debug view: <see cref="MaterialDebugView"/> the strongest material, <see cref="CostDebugView"/> textures blended per pixel; 0 off.</summary>
    public int DebugView
    {
        get => _debugView;
        set { _debugView = value; _render?.SetParam("terrain_debug", value); }
    }
    public const int MaterialDebugView = 1, CostDebugView = 2;

    /// <summary>Shows one erosion slot's coverage (an <see cref="ErosionSlotKind"/>), -1 off.</summary>
    public int SlotDebug
    {
        get => _slotDebug;
        set { _slotDebug = value; _render?.SetParam("slot_debug", value); }
    }

    /// <summary>The shader code Terrain3D draws with (the theme's shader with its own includes inlined).</summary>
    public string? DrawnShaderCode => _liveShader?.Code;

    [ExportToolButton("Regenerate")]
    public Callable RegenerateButton => Callable.From(Generate);

    private Terrain3DBridge? _render;
    private TerrainSkirt? _skirt;
    private bool _skirtDirty;
    private VertexRect _heightDirty = VertexRect.Empty, _splatDirty = VertexRect.Empty;
    private double _paramCopyTimer;
    // Runtime copy of the theme's shader with its own (relative) includes inlined; what Terrain3D draws. See ThemeShaderCode.
    private Shader? _liveShader;
    private int _debugView, _slotDebug = -1;
    private string? _newMapTheme;
    // The last theme warning, so the editor's twice-a-second refresh doesn't repeat it.
    private string? _lastThemeWarning;
    private WaterSurface? _waterSurface;
    private WaterSourceMarkers? _markers;
    private WaterFlowArrows? _arrows;
    private WaterPreview? _preview;
    // Add lake sources when the hollow search finishes; the callback (if any) gets what was added, for undo.
    private bool _lakeSourcesPending;
    private System.Action<LakeSourcesAdded>? _lakeSourcesDone;
    private CancellationTokenSource? _planJob;
    // Ground marks from the water (distance to water, wet paint) for the terrain shader.
    private Image? _groundImage;
    private ImageTexture? _groundTexture;
    private byte[]? _groundBytes;
    private long _groundVersion = -1;
    private double _lakeTimer = -1;
    private CancellationTokenSource? _lakeJob;
    private bool _lakesFailed;

    /// <summary>Wait after the last height edit before finding lakes again (a stroke edits every tick).</summary>
    private const double LakeDelay = 0.5;

    public HeightMap? Map { get; private set; }

    /// <summary>Painted ground layers, one weight set per heightmap vertex.</summary>
    public SplatMap? Splat { get; private set; }

    /// <summary>World-space XZ rectangle covered by the terrain.</summary>
    public Rect2 Bounds => Map is null
        ? new Rect2()
        : new Rect2(GlobalPosition.X, GlobalPosition.Z, Map.SizeX, Map.SizeZ);

    /// <summary>Time spent pushing edits to Terrain3D in the last frame that had edits.</summary>
    public double LastPushMs { get; private set; }
    public int LastPushRegions { get; private set; }

    /// <summary>Goes up with every height change, so work started on older heights can tell it's stale.</summary>
    public int HeightVersion { get; private set; }

    /// <summary>
    /// Hollows filled to their spill height (Priority-Flood) on the current heights, or null until the first search
    /// finishes. Not the water itself (that's <see cref="Water"/>): it carries the ground masks (shores, gullies, wear,
    /// deposits) the shader textures with, and the hollows <see cref="AddLakeSources"/> puts Lake sources in.
    /// </summary>
    public LakeMap? Lakes { get; private set; }
    /// <summary>Time the last lake search took (find + ground masks + mesh arrays).</summary>
    public double LastLakeMs { get; private set; }
    /// <summary>Raised on the main thread when <see cref="Lakes"/> is replaced.</summary>
    public event System.Action? LakesChanged;

    private LakeSettings _lakeSettings = new();
    /// <summary>Which depressions count as lakes. Changing it finds the lakes again.</summary>
    public LakeSettings LakeSettings
    {
        get => _lakeSettings;
        set { _lakeSettings = value; RefreshLakes(0.25); }
    }

    /// <summary>The water simulation for the current map (null in the Godot editor or if the library is missing).</summary>
    public WaterSim? Water { get; private set; }
    /// <summary>Raised when <see cref="Water"/> is replaced (a new map).</summary>
    public event System.Action? WaterChanged;

    private bool _edgeFog = true;
    /// <summary>
    /// The fog bank at the map's edge (and the fog skirt around it). On in game mode; the Map Editor starts with it off so
    /// the creator sees the ground up to the border (the shader draws a thin line there), and can switch it on to preview.
    /// Display only, not saved with the map.
    /// </summary>
    public bool EdgeFog
    {
        get => _edgeFog;
        set { _edgeFog = value; ApplyEdgeFog(); }
    }

    private void ApplyEdgeFog()
    {
        _render?.SetParam("edge_fog_enabled", _edgeFog);
        if (_skirt is not null) _skirt.Visible = _edgeFog;
    }

    private bool _showWater = true;
    public bool ShowWater
    {
        get => _showWater;
        set { _showWater = value; if (_waterSurface is not null) _waterSurface.Visible = value; }
    }

    /// <summary>Sea level for lake finding: ground below it connected to the edge is sea. Null when the map has no sea shape.</summary>
    public float? SeaLevel => Settings.Shape.Kind != ShapeKind.None ? Settings.SeaLevel : null;

    public override void _Ready()
    {
        // Run after tools so edits made this frame are rebuilt this frame.
        ProcessPriority = 100;
        // The Godot editor previews themes with their fog; in the app it follows the mode.
        _edgeFog = Engine.IsEditorHint() || MapSession.Mode == AppMode.Game;
        Native.Directory ??= ProjectSettings.GlobalizePath("res://native/erosion/bin");
        WaterNative.Directory ??= ProjectSettings.GlobalizePath("res://native/water/bin");
        if (Engine.IsEditorHint()) Generate();
        else Open(MapSession.TakePending());
    }

    /// <summary>Starts with the requested map; with no request (e.g. run straight from a CLI flag), generates from the exports.</summary>
    public void Open(MapRequest? request)
    {
        switch (request)
        {
            case GeneratedMapRequest { Map: { } map } gen:
                _newMapTheme = MapSession.NewMapTheme;
                Settings = gen.Settings;
                SetMap(map);
                ShowGeneratorOnStart = gen.ShowGenerator;
                break;
            case GeneratedMapRequest gen:
                _newMapTheme = MapSession.NewMapTheme;
                Generate(gen.Settings);
                ShowGeneratorOnStart = gen.ShowGenerator;
                break;
            case LoadedMapRequest loaded:
                SetMap(loaded.Map, loaded.Splat, loaded.Water);
                break;
            default:
                Generate();
                break;
        }
    }

    /// <summary>The generator settings the map was last made with (the Generation exports until something else is used).</summary>
    public GenSettings Settings { get; private set; } = new();

    /// <summary>Set when the map was requested with the generator panel open (New Map → Generator).</summary>
    public bool ShowGeneratorOnStart { get; private set; }

    /// <summary>The Size and Generation exports as generator settings.</summary>
    public GenSettings ExportSettings() => new()
    {
        Cells = CellsX,
        CellSize = CellSize,
        SmoothPasses = SmoothPasses,
        Noise = new NoiseSettings
        {
            Seed = Seed, Frequency = Frequency, Octaves = Octaves, HeightScale = HeightScale,
            Flatness = Flatness, WarpAmplitude = WarpAmplitude, Gain = Gain,
        },
    };

    public override void _Process(double delta)
    {
        if (_render is null) return;
        _render.FollowCamera(GetViewport().GetCamera3D(), new Vector2(Bounds.Size.X, Bounds.Size.Y));
        // In the editor, pick up uniforms tuned in the inspector.
        if (Engine.IsEditorHint() && (_paramCopyTimer += delta) > 0.5)
        {
            _paramCopyTimer = 0;
            ApplyTheme();
        }
        if (_skirtDirty)
        {
            _skirtDirty = false;
            _skirt?.Rebuild();
        }
        PushDirty();
        PushWaterGround();
        if (_lakeTimer >= 0 && (_lakeTimer -= delta) < 0) StartLakeSearch();
    }

    /// <summary>Uploads the water's ground marks (distance to water, wet paint) when the sim published new ones.</summary>
    private void PushWaterGround()
    {
        if (_render is null || Water is not { } sim || sim.GroundVersion == _groundVersion) return;
        int w = sim.MarksWidth, d = sim.MarksDepth, n = w * d * 2;
        if (_groundBytes?.Length != n) _groundBytes = new byte[n];
        _groundVersion = sim.CopyGroundMarks(_groundBytes);
        if (_groundImage is null || _groundImage.GetWidth() != w || _groundImage.GetHeight() != d)
        {
            _groundImage = Image.CreateFromData(w, d, false, Image.Format.Rg8, _groundBytes);
            _groundTexture = ImageTexture.CreateFromImage(_groundImage);
        }
        else
        {
            _groundImage.SetData(w, d, false, Image.Format.Rg8, _groundBytes);
            _groundTexture!.Update(_groundImage);
        }
        _render.SetParam("water_ground", _groundTexture);
        _render.SetParam("water_ground_cell", sim.MarksCellSize);
        _render.SetParam("has_water_ground", true);
    }

    public override void _ExitTree()
    {
        _lakeJob?.Cancel();
        DisposeWater();
    }

    public override void _Notification(int what)
    {
        // The Esc menu pauses the tree: the water pauses with it.
        if (what == NotificationPaused && Water is not null) Water.Suspended = true;
        else if (what == NotificationUnpaused && Water is not null) Water.Suspended = false;
    }

    /// <summary>Finds the lakes again after <paramref name="delay"/> seconds (restarted by every height edit).</summary>
    public void RefreshLakes(double delay = LakeDelay) => _lakeTimer = delay;

    /// <summary>
    /// Finds hollows and ground masks on a worker; the result is dropped if the heights changed meanwhile.
    /// </summary>
    private void StartLakeSearch()
    {
        _lakeTimer = -1;
        if (Map is not { } map || _lakesFailed) return;
        _lakeJob?.Cancel();
        var job = _lakeJob = new CancellationTokenSource();
        int version = HeightVersion;
        var settings = _lakeSettings;
        float? sea = SeaLevel;
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var lakes = TerrainSystem.Erosion.Lakes.Find(map, settings, sea, job.Token, ground: true);
            if (lakes is null) return;
            double ms = sw.Elapsed.TotalMilliseconds;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || job.IsCancellationRequested || map != Map || version != HeightVersion) return;
                if (lakes.Ground is { } ground) _render?.PushGround(ground, Lakes?.Ground, map.Width);
                GD.Print($"Terrain: {lakes.Count} lakes and ground masks in {ms:0} ms");
                Lakes = lakes;
                LastLakeMs = ms;
                if (_lakeSourcesPending && Water is not null)
                {
                    _lakeSourcesPending = false;
                    var done = _lakeSourcesDone;
                    _lakeSourcesDone = null;
                    PlanLakeSources(done);
                }
                LakesChanged?.Invoke();
            }).CallDeferred();
        }, job.Token).ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            var error = t.Exception!.GetBaseException();
            Callable.From(() =>
            {
                // A missing library won't appear by itself: stop retrying until the next map.
                if (error is System.DllNotFoundException) _lakesFailed = true;
                GD.PushError($"Terrain: finding lakes failed: {error.Message}");
            }).CallDeferred();
        });
    }

    /// <summary>
    /// Puts a Lake source in every hollow that has no Lake or River source yet (<see cref="LakeSources"/>), sized for it,
    /// and fills those hollows to their spill height at once; floods the sea up to sea level too. The water belongs to
    /// the sources, so a lake the user doesn't want is deleted with its source. Waits for the hollow search if it's still
    /// running. <paramref name="done"/> gets what was added (for undo), on the main thread.
    /// </summary>
    public void AddLakeSources(System.Action<LakeSourcesAdded>? done = null)
    {
        if (Water is null) return;
        if (Lakes is { } lakes && lakes.Width == Map?.Width && !_lakeSourcesPending && _lakeTimer < 0) PlanLakeSources(done);
        else
        {
            _lakeSourcesPending = true;
            _lakeSourcesDone = done;
            RefreshLakes(0);
        }
    }

    private void PlanLakeSources(System.Action<LakeSourcesAdded>? done)
    {
        if (Water is not { } sim || Lakes is not { } lakes || Map is not { } map) return;
        _planJob?.Cancel();
        var job = _planJob = new CancellationTokenSource();
        int version = HeightVersion;
        var existing = sim.Sources.ToArray();
        int firstId = sim.NextSourceId();
        float minRadius = sim.CellSize * 1.5f, evap = sim.Settings.EvaporationMmPerMin;
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var plan = LakeSources.Plan(lakes, map, existing, firstId, minRadius, evap, job.Token);
            if (plan is null) return;
            double ms = sw.Elapsed.TotalMilliseconds;
            var levels = plan.Lakes.Count > 0 ? plan.Levels() : null;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || job.IsCancellationRequested || Water != sim || version != HeightVersion) return;
                var before = sim.Sources.ToArray();
                var added = plan.Lakes.Select(l => l.Source with { Id = 0 }).ToList();
                // Ids again from the current list, in case sources changed while planning.
                int id = sim.NextSourceId();
                var sources = added.Select(s => s with { Id = id++ }).ToArray();
                var after = before.Concat(sources).ToArray();
                if (sources.Length > 0) sim.SetSources(after);
                if (levels is not null) sim.RaiseTo(levels);
                sim.FillHollows(null);
                GD.Print($"Terrain: {sources.Length} lake sources added in {ms:0} ms");
                done?.Invoke(new LakeSourcesAdded(before, after, sources, levels));
            }).CallDeferred();
        }, job.Token).ContinueWith(t =>
        {
            if (t.IsFaulted) Callable.From(() => GD.PushError($"Terrain: planning lake sources failed: {t.Exception!.GetBaseException().Message}")).CallDeferred();
        });
    }

    /// <summary>The water to save with the map, or null when there's no simulation.</summary>
    public WaterData? SaveWater() => Water is { } w
        ? new WaterData(w.Settings, w.Sources, w.Width, w.Depth, w.ReadDepth(), w.ReadPollution(), w.ReadPaint())
        : null;

    private void DisposeWater()
    {
        _planJob?.Cancel();
        _lakeSourcesPending = false;
        _lakeSourcesDone = null;
        Water?.Dispose();
        Water = null;
        _groundVersion = -1;
        _render?.SetParam("has_water_ground", false);
    }

    /// <summary>
    /// Starts the water for a new map: saved water as it was, else none. Water is never placed automatically, not even a
    /// sea for a generated coast: the map maker places every source (the Sea tool defaults to the generator's sea level;
    /// the Water panel's Add Lake Sources fills the hollows on request).
    /// </summary>
    private void StartWater(HeightMap map, WaterData? saved)
    {
        DisposeWater();
        if (Engine.IsEditorHint()) return;
        try
        {
            Water = new WaterSim(map);
        }
        catch (System.Exception e) when (e is System.DllNotFoundException or System.ArgumentException)
        {
            GD.PushError($"Terrain: no water simulation: {e.Message}");
            return;
        }
        if (saved is not null)
        {
            Water.Settings = saved.Settings;
            Water.SetSources(saved.Sources);
            if (saved.DepthGrid is { } grid && saved.Width > 1 && saved.Depth > 1)
            {
                var pollution = saved.PollutionGrid;
                var paint = saved.PaintGrid;
                if (saved.Width != Water.Width || saved.Depth != Water.Depth)
                {
                    // Saved on another water grid (before M6 phase 3f big maps had 14 m cells): nearest cell, the
                    // pollutant's kg scaled by the cell areas so the total stays about the same.
                    int w = Water.Width, d = Water.Depth;
                    float area = (w - 1f) * (d - 1f) / ((saved.Width - 1f) * (saved.Depth - 1f));
                    grid = grid.Resample(w, d);
                    pollution = pollution?.Resample(w, d, 1f / area);
                    paint = paint?.Resample(w, d);
                    GD.Print($"Terrain: water saved on {saved.Width}² cells, resampled to {w}²");
                }
                Water.LoadWater(grid, pollution, paint);
            }
        }
        _waterSurface = new WaterSurface { Name = "Water", Visible = _showWater };
        AddChild(_waterSurface);
        _waterSurface.Init(Water, WaterMaterial, map);
        _markers = new WaterSourceMarkers { Name = "WaterSources", Visible = false };
        AddChild(_markers);
        _markers.Init(this, Water);
        _arrows = new WaterFlowArrows { Name = "FlowArrows", Visible = false };
        AddChild(_arrows);
        _arrows.Init(this, Water);
        _preview = new WaterPreview { Name = "WaterPreview", Visible = false };
        AddChild(_preview);
        _preview.Init(this, Water);
        WaterChanged?.Invoke();
    }

    /// <summary>Shows the source markers (while a water tool is out), highlighting the hovered and selected ones.</summary>
    public void ShowWaterSources(bool visible, int? hovered = null, int? selected = null)
    {
        if (_markers is null) return;
        _markers.Visible = visible;
        if (visible) _markers.SetHighlight(hovered, selected);
    }

    /// <summary>The info label over the hovered source (null hides it).</summary>
    public void ShowWaterSourceInfo(int? id) => _markers?.ShowInfo(id);

    private bool _flowArrowsForced, _flowArrowsTool;
    /// <summary>Flow arrows on at all times (Water panel checkbox), not just while a water tool is out.</summary>
    public bool FlowArrows
    {
        get => _flowArrowsForced;
        set { _flowArrowsForced = value; UpdateArrows(); }
    }

    /// <summary>A water tool is out: show the flow arrows.</summary>
    public void ShowFlowArrowsForTool(bool on)
    {
        if (on == _flowArrowsTool) return;
        _flowArrowsTool = on;
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        if (_arrows is not null) _arrows.Visible = _flowArrowsForced || _flowArrowsTool;
    }

    /// <summary>
    /// Previews the water a Lake, River or Sea source placed here would hold (local metres; null hides it). Worked out
    /// on a worker; the label shows the area and volume.
    /// </summary>
    public void PreviewSource(WaterSource? source) => _preview?.Show(source);

    /// <summary>Copies this frame's edits to Terrain3D.</summary>
    public void PushDirty()
    {
        if (_render is null || Map is null || Splat is null || (_heightDirty.IsEmpty && _splatDirty.IsEmpty)) return;
        var sw = Stopwatch.StartNew();
        int regions = 0;
        if (!_heightDirty.IsEmpty)
        {
            _render.PushHeights(Map, _heightDirty);
            regions = _render.LastPushRegions;
        }
        if (!_splatDirty.IsEmpty)
        {
            _render.PushControl(Splat, _splatDirty);
            regions = System.Math.Max(regions, _render.LastPushRegions);
        }
        _heightDirty = _splatDirty = VertexRect.Empty;
        LastPushRegions = regions;
        LastPushMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Generates a new heightmap from the Size and Generation exports.</summary>
    public void Generate() => Generate(ExportSettings());

    /// <summary>Generates a new map from <paramref name="settings"/> (painted layers start empty).</summary>
    public void Generate(GenSettings settings)
    {
        var sw = Stopwatch.StartNew();
        var map = TerrainGen.Create(settings);
        GD.Print($"Terrain: heightmap generated in {sw.ElapsedMilliseconds} ms");
        Settings = settings;
        SetMap(map);
    }

    /// <summary>
    /// Overwrites every height with <paramref name="heights"/> and rebuilds, keeping painted layers. A different size
    /// replaces the map instead (painted layers start empty).
    /// Undo is the caller's job (see <c>TerrainToolController.ApplyGenerated</c>).
    /// </summary>
    public void ReplaceHeights(HeightMap heights, GenSettings settings)
    {
        if (Map is null || heights.Width != Map.Width || heights.Depth != Map.Depth)
        {
            // New size: a new map (tools drop their undo history).
            Settings = settings;
            SetMap(heights);
            return;
        }
        heights.Data.CopyTo(Map.Data);
        Map.Invalidate();
        Settings = settings;
        MarkDirty(0, 0, Map.Width - 1, Map.Depth - 1);
        PushDirty();
        _render?.RecalcHeightRange();
        UpdateMaterialRange();
        RefreshLakes(0);
    }

    /// <summary>
    /// Replaces the whole map (heights and, optionally, painted layers) and copies it into a new Terrain3D.
    /// Tools notice the new <see cref="Map"/> and drop their undo history.
    /// </summary>
    public void SetMap(HeightMap map, SplatMap? splat = null, WaterData? water = null)
    {
        if (splat is not null && (splat.Width != map.Width || splat.Depth != map.Depth))
            throw new System.ArgumentException("Splat map size doesn't match the heightmap.", nameof(splat));
        var sw = Stopwatch.StartNew();

        // A loaded map keeps its theme; a new one gets New Map's choice, else the current theme, else the default.
        var theme = ThemeLibrary.Get(splat?.ThemeId is { Length: > 0 } id ? id : _newMapTheme ?? Theme?.Id ?? DefaultTheme?.Id);
        _newMapTheme = null;
        if (theme?.Material?.Shader is null)
        {
            GD.PushError($"Terrain: no usable terrain theme in {ThemeLibrary.Root} (each needs theme.tres with a shader).");
            return;
        }
        if (!GlobalPosition.IsZeroApprox())
            GD.PushWarning("Terrain: Terrain3D draws at the world origin; move the Terrain node to (0, 0, 0).");

        // Free the old render copy and skirt, including ones left over from an editor script reload.
        _render?.Free();
        _render = null;
        foreach (var child in GetChildren())
            if (child is TerrainSkirt or WaterSurface or WaterSourceMarkers or WaterFlowArrows or WaterPreview)
                child.Free();
        _waterSurface = null;
        _markers = null;
        _arrows = null;
        _preview = null;
        _heightDirty = _splatDirty = VertexRect.Empty;
        _lakeJob?.Cancel();
        Lakes = null;
        _lakesFailed = false;
        HeightVersion++;

        Map = map;
        CellsX = map.Width - 1;
        CellsZ = map.Depth - 1;
        CellSize = map.CellSize;
        Splat = splat ?? new SplatMap(map.Width, map.Depth, map.CellSize);
        Theme = theme;
        Splat.Remap(theme.Id, theme.MaterialIds());
        _liveShader = new Shader { Code = ThemeShaderCode(theme) };
        _render = Terrain3DBridge.Create(this, Map, Splat, _liveShader);
        ApplyTheme();

        _skirt = new TerrainSkirt();
        _skirt.Init(Map, _render.RenderedX - 1, _render.RenderedZ - 1);
        _skirt.MaterialOverride = SkirtMaterial;
        AddChild(_skirt);
        _skirt.Rebuild();
        _skirtDirty = false;
        ApplyEdgeFog();

        GD.Print($"Terrain: {Map.Width}x{Map.Depth} verts, copied to Terrain3D in {sw.ElapsedMilliseconds} ms");
        StartWater(map, water);
        RefreshLakes(0);
    }

    // --- Queries (world space) ---

    public float GetHeight(float worldX, float worldZ)
    {
        if (Map is null) return GlobalPosition.Y;
        var o = GlobalPosition;
        return Map.SampleHeight(worldX - o.X, worldZ - o.Z) + o.Y;
    }

    public Vector3 GetNormal(float worldX, float worldZ)
    {
        if (Map is null) return Vector3.Up;
        var o = GlobalPosition;
        var n = Map.SampleNormal(worldX - o.X, worldZ - o.Z);
        return new Vector3(n.X, n.Y, n.Z);
    }

    /// <summary>Depth of water at a world position, in metres (0 on dry ground). From the latest water snapshot.</summary>
    public float GetWaterDepth(float worldX, float worldZ)
    {
        if (Water is null) return 0f;
        var o = GlobalPosition;
        return Water.DepthAt(worldX - o.X, worldZ - o.Z);
    }

    /// <summary>Under more than 1 cm of water.</summary>
    public bool IsUnderwater(float worldX, float worldZ) => GetWaterDepth(worldX, worldZ) > 0.01f;

    /// <summary>World height of the water surface, or null on dry ground.</summary>
    public float? GetWaterSurface(float worldX, float worldZ)
    {
        if (Water is null) return null;
        var o = GlobalPosition;
        return Water.SurfaceAt(worldX - o.X, worldZ - o.Z) + o.Y;
    }

    /// <summary>Pollutant concentration (kg/m³) in the water at a world position; zero on dry ground.</summary>
    public float GetWaterPollution(float worldX, float worldZ)
    {
        if (Water is null) return 0f;
        var o = GlobalPosition;
        return Water.PollutionAt(worldX - o.X, worldZ - o.Z);
    }

    /// <summary>Water velocity (m/s) at a world position: direction and speed of the flow. Zero on dry ground.</summary>
    public Vector3 GetWaterVelocity(float worldX, float worldZ)
    {
        if (Water is null) return Vector3.Zero;
        var o = GlobalPosition;
        var (vx, vz) = Water.VelocityAt(worldX - o.X, worldZ - o.Z);
        return new Vector3(vx, 0f, vz);
    }

    public float GetSlopeDegrees(float worldX, float worldZ)
    {
        if (Map is null) return 0f;
        var o = GlobalPosition;
        return Map.SampleSlopeDegrees(worldX - o.X, worldZ - o.Z);
    }

    /// <summary>
    /// Intersects a world-space ray with the terrain by ray-marching the heightmap, then refining
    /// with a binary search. No physics bodies involved. <paramref name="heightAt"/> (world x, z → world height) replaces
    /// the current ground, e.g. with the ground from before a stroke.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, out Vector3 hit, float maxDistance = 100000f,
        System.Func<float, float, float>? heightAt = null)
    {
        heightAt ??= GetHeight;
        hit = default;
        if (Map is null || direction.IsZeroApprox()) return false;
        var dir = direction.Normalized();

        // Clip the ray to the terrain's XZ bounds (slab test).
        var b = Bounds;
        float tEnter = 0f, tExit = maxDistance;
        if (!ClipSlab(origin.X, dir.X, b.Position.X, b.End.X, ref tEnter, ref tExit) ||
            !ClipSlab(origin.Z, dir.Z, b.Position.Y, b.End.Y, ref tEnter, ref tExit))
            return false;

        float step = Map.CellSize;
        float tPrev = tEnter;
        if (Above(tPrev) <= 0f)
        {
            hit = origin + dir * tPrev;
            return true;
        }
        for (float t = tEnter + step; t <= tExit + step; t += step)
        {
            float tc = Mathf.Min(t, tExit);
            if (Above(tc) <= 0f)
            {
                float lo = tPrev, hi = tc;
                for (int i = 0; i < 16; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (Above(mid) > 0f) lo = mid; else hi = mid;
                }
                var p = origin + dir * hi;
                hit = new Vector3(p.X, heightAt(p.X, p.Z), p.Z);
                return true;
            }
            tPrev = tc;
            if (tc >= tExit) break;
        }
        return false;

        float Above(float t)
        {
            var p = origin + dir * t;
            return p.Y - heightAt(p.X, p.Z);
        }
    }

    private static bool ClipSlab(float o, float d, float min, float max, ref float tEnter, ref float tExit)
    {
        if (Mathf.Abs(d) < 1e-8f)
            return o >= min && o <= max;
        float t0 = (min - o) / d, t1 = (max - o) / d;
        if (t0 > t1) (t0, t1) = (t1, t0);
        tEnter = Mathf.Max(tEnter, t0);
        tExit = Mathf.Min(tExit, t1);
        return tEnter <= tExit;
    }

    // --- Editing hooks ---

    /// <summary>Marks heights in a vertex rectangle as edited. They're pushed to Terrain3D at the end of the frame.</summary>
    public void MarkDirty(VertexRect r)
    {
        if (!r.IsEmpty) MarkDirty(r.MinX, r.MinZ, r.MaxX, r.MaxZ);
    }

    /// <summary>Recomputes the height colouring range. Call after a stroke, not during it.</summary>
    public void RefreshHeightRange() => UpdateMaterialRange();

    /// <summary>
    /// Shows the brush ring in the terrain shader. With a <paramref name="mask"/> the fill shows the brush
    /// shape turned by <paramref name="angle"/> (radians); <paramref name="showAngle"/> adds a tick on the ring.
    /// </summary>
    public void SetBrush(Vector3 worldPos, float radius, bool visible, Texture2D? mask = null, float angle = 0f, bool showAngle = false)
    {
        if (_render is not { } r) return;
        r.SetParam("brush_visible", visible);
        r.SetParam("brush_pos", worldPos);
        r.SetParam("brush_radius", radius);
        r.SetParam("brush_use_mask", mask is not null);
        if (mask is not null) r.SetParam("brush_mask", mask);
        r.SetParam("brush_angle", angle);
        r.SetParam("brush_show_angle", showAngle);
    }

    /// <summary>Marks painted layers in a vertex rectangle for upload at the end of the frame.</summary>
    public void MarkSplatDirty(VertexRect r) => _splatDirty = _splatDirty.Union(r);

    /// <summary>Toggles the placement grid overlay.</summary>
    public void SetGrid(bool visible)
    {
        _render?.SetParam("show_grid", visible);
    }

    /// <summary>Toggles height contour lines, <paramref name="interval"/> metres apart.</summary>
    public void SetContours(bool visible, float interval)
    {
        _render?.SetParam("show_contours", visible);
        _render?.SetParam("contour_interval", interval);
    }

    /// <summary>Shows a marker at the slope tool's start point, with a guide line to the brush.</summary>
    /// <summary>
    /// Shows the start point marker and a guide line from it to the brush. A <paramref name="bandHalfWidth"/> above 0
    /// draws the guide as a band that wide on each side (the Channel tool's corridor).
    /// </summary>
    public void SetAnchor(Vector3? worldPos, float bandHalfWidth = 0f)
    {
        if (_render is not { } r) return;
        r.SetParam("anchor_visible", worldPos.HasValue);
        if (worldPos.HasValue) r.SetParam("anchor_pos", worldPos.Value);
        r.SetParam("anchor_band", bandHalfWidth);
    }

    /// <summary>Marks heights in the given inclusive vertex range as edited.</summary>
    public void MarkDirty(int minX, int minZ, int maxX, int maxZ)
    {
        // The skirt follows the border heights, so edits touching the border move it too.
        if (_render is not null && (minX <= 0 || minZ <= 0 || maxX >= _render.RenderedX - 1 || maxZ >= _render.RenderedZ - 1))
            _skirtDirty = true;
        var rect = new VertexRect(minX, minZ, maxX, maxZ);
        _heightDirty = _heightDirty.Union(rect);
        Water?.GroundChanged(rect);
        HeightVersion++;
        RefreshLakes();
    }

    /// <summary>
    /// Switches the map to another theme. Painted ground keeps its material ids: materials the new theme doesn't have
    /// stay in the map (drawn as automatic ground) and come back when a theme that has them is chosen again.
    /// </summary>
    public void SetTheme(TerrainTheme theme)
    {
        if (Splat is null || _render is null) { Theme = theme; return; }
        var old = Splat.Palette;
        Splat.Remap(theme.Id, theme.MaterialIds());
        Theme = theme;
        // Painted indices moved: push every painted tile again.
        if (!System.Linq.Enumerable.SequenceEqual(old, Splat.Palette)) MarkSplatDirty(Splat.All);
        ApplyTheme(reset: true);
    }

    /// <summary>Reloads the current theme from disk (after editing and baking it in Godot).</summary>
    public void ReloadTheme()
    {
        if (Theme is null) return;
        if (ThemeLibrary.Reload(Theme.Id) is { } fresh) SetTheme(fresh);
    }

    /// <summary>
    /// Sends <see cref="Theme"/> to Terrain3D: its shader, tuned uniforms, texture arrays, per-material and per-slot
    /// uniforms, and its edge fog to the skirt. With <paramref name="reset"/> (a different theme), uniforms the theme
    /// doesn't set go back to their defaults instead of keeping the previous theme's values.
    /// </summary>
    private void ApplyTheme(bool reset = false)
    {
        if (_render is null || Theme is not { Material: { Shader: { } shader } sm } theme) return;
        string code = ThemeShaderCode(theme);
        if (_liveShader is null || code != _liveShader.Code || reset)
        {
            _liveShader = new Shader { Code = code };
            _render.SetShader(_liveShader);
        }
        if (reset)
            foreach (var u in shader.GetShaderUniformList())
            {
                string name = u.AsGodotDictionary()["name"].AsString();
                if (!name.StartsWith('_') && !RuntimeParams.Contains(name) && !name.StartsWith("brush_") && !name.StartsWith("anchor_"))
                    _render.SetParam(name, RenderingServer.ShaderGetParameterDefault(shader.GetRid(), name));
            }
        _render.CopyParams(sm);

        var albedo = ResourceLoader.Exists(theme.AlbedoArrayPath) ? ResourceLoader.Load<TextureLayered>(theme.AlbedoArrayPath) : null;
        var normal = ResourceLoader.Exists(theme.NormalArrayPath) ? ResourceLoader.Load<TextureLayered>(theme.NormalArrayPath) : null;
        string? warning = null;
        if (albedo is null || normal is null)
            warning = $"Terrain: theme '{theme.Id}' isn't baked (no {theme.AlbedoArrayPath}). Run tools/fetch_textures.sh, " +
                      "or press Bake on the theme in the Godot inspector.";
        else if (albedo.GetLayers() != theme.Materials.Count)
            warning = $"Terrain: theme '{theme.Id}' has {theme.Materials.Count} materials but its baked textures have " +
                      $"{albedo.GetLayers()}; bake it again.";
        else if (!ThemeBaker.IsIncludeCurrent(theme))
            warning = $"Terrain: theme '{theme.Id}' changed since it was baked ({TerrainTheme.IncludeName} is out of date); bake it again.";
        if (warning is not null && warning != _lastThemeWarning) GD.PushWarning(warning);
        _lastThemeWarning = warning;
        _render.SetParam("albedo_height_array", albedo);
        _render.SetParam("normal_array", normal);

        var (tints, prms) = theme.PackMaterials();
        _render.SetParam("material_tint", tints);
        _render.SetParam("material_params", prms);
        var (edge, slope) = theme.PackSlots();
        _render.SetParam("slot_edge", edge);
        _render.SetParam("slot_slope", slope);
        _render.SetParam("terrain_debug", _debugView);
        _render.SetParam("slot_debug", _slotDebug);
        UpdateMaterialRange();
        ApplyEdgeFog();

        if (SkirtMaterial is ShaderMaterial skirt && skirt.Shader is not null)
            foreach (var u in skirt.Shader.GetShaderUniformList())
            {
                string name = u.AsGodotDictionary()["name"].AsString();
                var value = sm.GetShaderParameter(name);
                if (value.VariantType == Variant.Type.Nil) value = RenderingServer.ShaderGetParameterDefault(shader.GetRid(), name);
                if (value.VariantType != Variant.Type.Nil) skirt.SetShaderParameter(name, value);
            }
        ThemeChanged?.Invoke();
    }

    // Uniforms the game sets while running; a theme switch leaves them alone.
    private static readonly System.Collections.Generic.HashSet<string> RuntimeParams =
    [
        "height_min", "height_max", "terrain_origin", "terrain_size", "albedo_height_array", "normal_array", "material_tint",
        "material_params", "slot_edge", "slot_slope", "terrain_debug", "slot_debug", "ground_debug", "show_grid", "show_contours",
        "water_ground", "water_ground_cell", "has_water_ground", "edge_fog_enabled",
    ];

    /// <summary>
    /// The theme's shader code with its relative <c>#include</c>s (its own files, such as materials.gdshaderinc) replaced by
    /// their text, read fresh from disk. Terrain3D copies the code into a shader with no path, where relative includes
    /// wouldn't resolve, and a re-baked include must not come from Godot's resource cache. Absolute includes (the SDK)
    /// stay as they are.
    /// </summary>
    private static string ThemeShaderCode(TerrainTheme theme)
    {
        var shader = theme.Material!.Shader!;
        string dir = shader.ResourcePath.GetBaseDir();
        return System.Text.RegularExpressions.Regex.Replace(shader.Code, @"#include\s+""(?!res://)([^""]+)""", m =>
        {
            string path = $"{dir}/{m.Groups[1].Value}";
            return Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : $"#include \"{path}\"";
        });
    }

    private void UpdateMaterialRange()
    {
        if (Map is null || _render is null) return;
        var (min, max) = Map.GetRange();
        _render.SetParam("height_min", min);
        _render.SetParam("height_max", max);
        // Edge fog follows the drawn area (the last heightmap row and column aren't drawn).
        _render.SetParam("terrain_origin", new Vector2(GlobalPosition.X, GlobalPosition.Z));
        _render.SetParam("terrain_size", new Vector2((_render.RenderedX - 1) * Map.CellSize, (_render.RenderedZ - 1) * Map.CellSize));
    }
}
