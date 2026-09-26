using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem.Generation;

namespace CitySim.TerrainSystem;

/// <summary>
/// Owns the heightmap and the chunk meshes built from it, and answers terrain queries
/// (height, normal, slope, bounds) for other systems such as the camera, roads and buildings.
/// </summary>
[Tool]
public partial class Terrain : Node3D
{
    /// <summary>Cell size for maps made from the menu (new, flat, imported).</summary>
    public const float DefaultCellSize = 2f;

    [ExportGroup("Size")]
    [Export(PropertyHint.Range, "16,4096,16")] public int CellsX { get; set; } = 1024;
    [Export(PropertyHint.Range, "16,4096,16")] public int CellsZ { get; set; } = 1024;
    [Export(PropertyHint.Range, "0.5,16,0.5,suffix:m")] public float CellSize { get; set; } = DefaultCellSize;
    [Export(PropertyHint.Range, "8,256,8")] public int ChunkCells { get; set; } = 64;

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
    [Export] public Material? Material { get; set; }

    [ExportToolButton("Regenerate")]
    public Callable RegenerateButton => Callable.From(Generate);

    private readonly Dictionary<Vector2I, TerrainChunk> _chunks = new();
    private readonly HashSet<Vector2I> _dirty = new();
    private TerrainSkirt? _skirt;
    private bool _skirtDirty;

    // Painted layer weights, uploaded to the shader as two RGBA8 textures (layers 0-3 and 4-7).
    private byte[] _splatBytes0 = [], _splatBytes1 = [];
    private Image? _splatImage0, _splatImage1;
    private ImageTexture? _splatTex0, _splatTex1;
    private VertexRect _splatDirty = VertexRect.Empty;

    public HeightMap? Map { get; private set; }

    /// <summary>Painted ground layers, one weight set per heightmap vertex.</summary>
    public SplatMap? Splat { get; private set; }

    /// <summary>World-space XZ rectangle covered by the terrain.</summary>
    public Rect2 Bounds => Map is null
        ? new Rect2()
        : new Rect2(GlobalPosition.X, GlobalPosition.Z, Map.SizeX, Map.SizeZ);

    /// <summary>Time spent rebuilding dirty chunks in the last frame that had edits.</summary>
    public double LastRebuildMs { get; private set; }
    public int LastRebuildChunks { get; private set; }

    public override void _Ready()
    {
        // Run after tools so edits made this frame are rebuilt this frame.
        ProcessPriority = 100;
        if (Engine.IsEditorHint()) Generate();
        else Open(MapSession.TakePending());
        CheckTextures();
    }

    /// <summary>Starts with the requested map; with no request (e.g. run straight from a CLI flag), generates from the exports.</summary>
    public void Open(MapRequest? request)
    {
        switch (request)
        {
            case GeneratedMapRequest gen:
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

    /// <summary>Warns when the material has no ground textures (the shader then shows flat, over-bright tints).</summary>
    private void CheckTextures()
    {
        if (Material is not ShaderMaterial sm) return;
        foreach (var param in new[] { "albedo_height_array", "normal_array" })
            if (sm.GetShaderParameter(param).VariantType == Variant.Type.Nil)
                GD.PushWarning($"Terrain: material has no '{param}'. Run tools/fetch_textures.sh, and check that " +
                               "Main.tscn still assigns the texture arrays (an editor tab with an old copy of the scene can overwrite it).");
    }

    public override void _Process(double delta)
    {
        UploadSplat();
        if (_skirtDirty)
        {
            _skirtDirty = false;
            _skirt?.Rebuild();
        }
        if (_dirty.Count == 0) return;
        var sw = Stopwatch.StartNew();
        LastRebuildChunks = _dirty.Count;
        RebuildDirty();
        LastRebuildMs = sw.Elapsed.TotalMilliseconds;
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
        Settings = settings;
        MarkDirty(0, 0, Map.Width - 1, Map.Depth - 1);
        UpdateMaterialRange();
    }

    /// <summary>
    /// Replaces the whole map (heights and, optionally, painted layers) and rebuilds every chunk.
    /// Tools notice the new <see cref="Map"/> and drop their undo history.
    /// </summary>
    public void SetMap(HeightMap map, SplatMap? splat = null)
    {
        if (splat is not null && (splat.Width != map.Width || splat.Depth != map.Depth))
            throw new System.ArgumentException("Splat map size doesn't match the heightmap.", nameof(splat));
        var sw = Stopwatch.StartNew();

        // Free every existing chunk, including ones left over from an editor script reload.
        foreach (var child in GetChildren())
            if (child is TerrainChunk or TerrainSkirt)
                child.Free();
        _chunks.Clear();
        _dirty.Clear();

        Map = map;
        CellsX = map.Width - 1;
        CellsZ = map.Depth - 1;
        CellSize = map.CellSize;
        Splat = splat ?? new SplatMap(map.Width, map.Depth, map.CellSize);
        CreateSplatTextures();
        if (splat is not null) _splatDirty = Splat.All;
        UpdateMaterialRange();

        int chunksX = (CellsX + ChunkCells - 1) / ChunkCells;
        int chunksZ = (CellsZ + ChunkCells - 1) / ChunkCells;
        for (int cz = 0; cz < chunksZ; cz++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                var coord = new Vector2I(cx, cz);
                var chunk = new TerrainChunk();
                chunk.Init(Map, coord, ChunkCells);
                chunk.MaterialOverride = Material;
                AddChild(chunk);
                chunk.Rebuild();
                _chunks[coord] = chunk;
            }
        }

        _skirt = new TerrainSkirt();
        _skirt.Init(Map);
        _skirt.MaterialOverride = Material;
        AddChild(_skirt);
        _skirt.Rebuild();
        _skirtDirty = false;

        GD.Print($"Terrain: {Map.Width}x{Map.Depth} verts, {_chunks.Count} chunks, built in {sw.ElapsedMilliseconds} ms");
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

    /// <summary>Marks the chunks touching a vertex rectangle dirty. They're rebuilt at the end of the frame.</summary>
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
        if (Material is not ShaderMaterial sm) return;
        sm.SetShaderParameter("brush_visible", visible);
        sm.SetShaderParameter("brush_pos", worldPos);
        sm.SetShaderParameter("brush_radius", radius);
        sm.SetShaderParameter("brush_use_mask", mask is not null);
        if (mask is not null) sm.SetShaderParameter("brush_mask", mask);
        sm.SetShaderParameter("brush_angle", angle);
        sm.SetShaderParameter("brush_show_angle", showAngle);
    }

    /// <summary>Marks painted splat weights in a vertex rectangle for upload at the end of the frame.</summary>
    public void MarkSplatDirty(VertexRect r) => _splatDirty = _splatDirty.Union(r);

    /// <summary>Toggles the placement grid overlay.</summary>
    public void SetGrid(bool visible)
    {
        if (Material is ShaderMaterial sm) sm.SetShaderParameter("show_grid", visible);
    }

    /// <summary>Toggles height contour lines, <paramref name="interval"/> metres apart.</summary>
    public void SetContours(bool visible, float interval)
    {
        if (Material is not ShaderMaterial sm) return;
        sm.SetShaderParameter("show_contours", visible);
        sm.SetShaderParameter("contour_interval", interval);
    }

    /// <summary>Shows a marker at the slope tool's start point, with a guide line to the brush.</summary>
    public void SetAnchor(Vector3? worldPos)
    {
        if (Material is not ShaderMaterial sm) return;
        sm.SetShaderParameter("anchor_visible", worldPos.HasValue);
        if (worldPos.HasValue) sm.SetShaderParameter("anchor_pos", worldPos.Value);
    }

    /// <summary>Marks every chunk touching the given inclusive vertex range as needing a rebuild.</summary>
    public void MarkDirty(int minX, int minZ, int maxX, int maxZ)
    {
        // The skirt follows the border heights, so edits touching the border move it too.
        if (Map is not null && (minX <= 0 || minZ <= 0 || maxX >= Map.Width - 1 || maxZ >= Map.Depth - 1))
            _skirtDirty = true;
        // Normals use neighbouring heights, so widen by one vertex.
        int cx0 = Mathf.Max(0, (minX - 1) / ChunkCells);
        int cz0 = Mathf.Max(0, (minZ - 1) / ChunkCells);
        int cx1 = (maxX + 1) / ChunkCells;
        int cz1 = (maxZ + 1) / ChunkCells;
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var coord = new Vector2I(cx, cz);
                if (_chunks.ContainsKey(coord)) _dirty.Add(coord);
                // A vertex on a chunk edge is shared with the previous chunk.
                var prev = new Vector2I(cx - 1, cz);
                if (cx > 0 && _chunks.ContainsKey(prev)) _dirty.Add(prev);
                prev = new Vector2I(cx, cz - 1);
                if (cz > 0 && _chunks.ContainsKey(prev)) _dirty.Add(prev);
            }
    }

    public void RebuildDirty()
    {
        foreach (var coord in _dirty)
            _chunks[coord].Rebuild();
        _dirty.Clear();
    }

    private void CreateSplatTextures()
    {
        if (Splat is null) return;
        int n = Splat.Width * Splat.Depth * 4;
        _splatBytes0 = new byte[n];
        _splatBytes1 = new byte[n];
        _splatImage0 = Image.CreateFromData(Splat.Width, Splat.Depth, false, Image.Format.Rgba8, _splatBytes0);
        _splatImage1 = Image.CreateFromData(Splat.Width, Splat.Depth, false, Image.Format.Rgba8, _splatBytes1);
        _splatTex0 = ImageTexture.CreateFromImage(_splatImage0);
        _splatTex1 = ImageTexture.CreateFromImage(_splatImage1);
        _splatDirty = VertexRect.Empty;
        // In the editor, leave the material alone so the scene file doesn't embed the (empty) textures.
        if (Engine.IsEditorHint() || Material is not ShaderMaterial sm) return;
        sm.SetShaderParameter("splat0", _splatTex0);
        sm.SetShaderParameter("splat1", _splatTex1);
        sm.SetShaderParameter("splat_size", new Vector2(Splat.Width, Splat.Depth));
    }

    /// <summary>
    /// Re-uploads the splat textures if anything was painted this frame. Only the dirty rectangle is
    /// converted to bytes, but the whole texture is uploaded (about 4 MB each at 1025²).
    /// </summary>
    private void UploadSplat()
    {
        if (_splatDirty.IsEmpty || Splat is null || _splatImage0 is null || _splatImage1 is null) return;
        Splat.WriteRgba8(_splatDirty, _splatBytes0, _splatBytes1);
        _splatDirty = VertexRect.Empty;
        _splatImage0.SetData(Splat.Width, Splat.Depth, false, Image.Format.Rgba8, _splatBytes0);
        _splatImage1.SetData(Splat.Width, Splat.Depth, false, Image.Format.Rgba8, _splatBytes1);
        _splatTex0!.Update(_splatImage0);
        _splatTex1!.Update(_splatImage1);
    }

    private void UpdateMaterialRange()
    {
        if (Map is null || Material is not ShaderMaterial sm) return;
        var (min, max) = Map.GetRange();
        sm.SetShaderParameter("height_min", min);
        sm.SetShaderParameter("height_max", max);
        sm.SetShaderParameter("terrain_origin", new Vector2(GlobalPosition.X, GlobalPosition.Z));
        sm.SetShaderParameter("terrain_size", new Vector2(Map.SizeX, Map.SizeZ));
    }
}
