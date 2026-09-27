using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem.Erosion;
using CitySim.TerrainSystem.Generation;
using CitySim.TerrainSystem.Themes;

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
    /// <summary>Lake surfaces (<c>lake_water.gdshader</c>).</summary>
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
    private LakeWater? _water;
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
    /// Standing water found on the current heights, or null until the first search finishes. Also carries the ground
    /// masks (shores, gullies, wear, deposits) the shader textures with.
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

    private bool _showLakes = true;
    public bool ShowLakes
    {
        get => _showLakes;
        set { _showLakes = value; if (_water is not null) _water.Visible = value; }
    }

    /// <summary>Sea level for lake finding: ground below it connected to the edge is sea. Null when the map has no sea shape.</summary>
    public float? SeaLevel => Settings.Shape.Kind != ShapeKind.None ? Settings.SeaLevel : null;

    public override void _Ready()
    {
        // Run after tools so edits made this frame are rebuilt this frame.
        ProcessPriority = 100;
        Native.Directory ??= ProjectSettings.GlobalizePath("res://native/erosion/bin");
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
                SetMap(loaded.Map, loaded.Splat);
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
        _render.FollowCamera(GetViewport().GetCamera3D());
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
        if (_lakeTimer >= 0 && (_lakeTimer -= delta) < 0) StartLakeSearch();
    }

    public override void _ExitTree() => _lakeJob?.Cancel();

    /// <summary>Finds the lakes again after <paramref name="delay"/> seconds (restarted by every height edit).</summary>
    public void RefreshLakes(double delay = LakeDelay) => _lakeTimer = delay;

    /// <summary>
    /// Finds lakes and ground masks and builds the lake mesh arrays on a worker; the result is dropped if the heights
    /// changed meanwhile.
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
            var arrays = LakeWater.Build(lakes);
            double ms = sw.Elapsed.TotalMilliseconds;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || job.IsCancellationRequested || map != Map || version != HeightVersion) return;
                if (lakes.Ground is { } ground) _render?.PushGround(ground, Lakes?.Ground, map.Width);
                GD.Print($"Terrain: {lakes.Count} lakes and ground masks in {ms:0} ms");
                Lakes = lakes;
                LastLakeMs = ms;
                EnsureWater().Apply(arrays);
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

    private LakeWater EnsureWater()
    {
        if (_water is null || !IsInstanceValid(_water))
        {
            _water = new LakeWater { Name = "Lakes", Material = WaterMaterial, Visible = _showLakes };
            AddChild(_water);
        }
        return _water;
    }

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
    public void SetMap(HeightMap map, SplatMap? splat = null)
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
            if (child is TerrainSkirt or LakeWater)
                child.Free();
        _water = null;
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

        GD.Print($"Terrain: {Map.Width}x{Map.Depth} verts, copied to Terrain3D in {sw.ElapsedMilliseconds} ms");
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

    /// <summary>Depth of lake water at a world position, in metres (0 on dry ground or before lakes are found).</summary>
    public float GetWaterDepth(float worldX, float worldZ)
    {
        if (Map is null || Lakes is null) return 0f;
        var o = GlobalPosition;
        return Lakes.WaterDepth(Map, worldX - o.X, worldZ - o.Z);
    }

    public bool IsUnderwater(float worldX, float worldZ) => GetWaterDepth(worldX, worldZ) > 0f;

    public float GetSlopeDegrees(float worldX, float worldZ)
    {
        if (Map is null) return 0f;
        var o = GlobalPosition;
        return Map.SampleSlopeDegrees(worldX - o.X, worldZ - o.Z);
    }

    /// <summary>
    /// Intersects a world-space ray with the terrain by ray-marching the heightmap, then refining
    /// with a binary search. No physics bodies involved.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, out Vector3 hit, float maxDistance = 8000f)
    {
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
                hit = new Vector3(p.X, GetHeight(p.X, p.Z), p.Z);
                return true;
            }
            tPrev = tc;
            if (tc >= tExit) break;
        }
        return false;

        float Above(float t)
        {
            var p = origin + dir * t;
            return p.Y - GetHeight(p.X, p.Z);
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
    public void SetAnchor(Vector3? worldPos)
    {
        if (_render is not { } r) return;
        r.SetParam("anchor_visible", worldPos.HasValue);
        if (worldPos.HasValue) r.SetParam("anchor_pos", worldPos.Value);
    }

    /// <summary>Marks heights in the given inclusive vertex range as edited.</summary>
    public void MarkDirty(int minX, int minZ, int maxX, int maxZ)
    {
        // The skirt follows the border heights, so edits touching the border move it too.
        if (_render is not null && (minX <= 0 || minZ <= 0 || maxX >= _render.RenderedX - 1 || maxZ >= _render.RenderedZ - 1))
            _skirtDirty = true;
        _heightDirty = _heightDirty.Union(new VertexRect(minX, minZ, maxX, maxZ));
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
