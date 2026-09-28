using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// The render copy of the map in the Terrain3D addon (GPU clipmap). The only place that talks to Terrain3D: its classes
/// have no C# bindings, so everything goes through <c>Call</c>/<c>Get</c>/<c>Set</c> strings.
///
/// <see cref="HeightMap"/> and <see cref="SplatMap"/> stay the source of truth; this copies them in once and then pushes
/// only edited rectangles. Terrain3D's colour map carries our ground masks (<see cref="Erosion.LakeMap.Ground"/>), not colours. Terrain3D regions are square powers of two, so the copy covers the first <c>cells</c>²
/// vertices: the last row and column of the heightmap are not drawn. Pixels past the map (when the size isn't a
/// multiple of the region size) are holes. The regions are centred on the world origin (<see cref="Origin"/>), which halves
/// the float error at the far edges of big maps; the <c>Terrain</c> node sits at that origin.
/// </summary>
public sealed class Terrain3DBridge
{
    public const string NodeName = "Terrain3D";

    // Terrain3DRegion.MapType
    private const int TypeHeight = 0, TypeControl = 1, TypeColor = 2;
    private const uint HoleBit = 1u << 2;
    private const int MaxRegionsPerSide = 16, MinRegionSize = 256;

    private readonly Node3D _node;
    private readonly GodotObject _data;
    private readonly GodotObject _material;
    private readonly int _region;
    private readonly int _regionsX, _regionsZ;
    // Region location of the map's first region: negative, so the map is centred on the world origin.
    private readonly Vector2I _location;
    // Per region (row-major): the Terrain3DRegion and its height/control/colour Images, edited in place.
    private readonly GodotObject[] _regions;
    private readonly Image[] _heightImages, _controlImages, _colorImages;
    private readonly List<int> _edited = new();
    private Camera3D? _camera;

    /// <summary>Vertices drawn along X and Z (the heightmap's cells).</summary>
    public int RenderedX { get; }
    public int RenderedZ { get; }

    /// <summary>Regions touched by the last push.</summary>
    public int LastPushRegions { get; private set; }

    private Terrain3DBridge(Node3D node, int region, int renderedX, int renderedZ)
    {
        _node = node;
        _region = region;
        RenderedX = renderedX;
        RenderedZ = renderedZ;
        _regionsX = (renderedX + region - 1) / region;
        _regionsZ = (renderedZ + region - 1) / region;
        _location = new Vector2I(-(_regionsX / 2), -(_regionsZ / 2));
        _regions = new GodotObject[_regionsX * _regionsZ];
        _heightImages = new Image[_regions.Length];
        _controlImages = new Image[_regions.Length];
        _colorImages = new Image[_regions.Length];
        _data = node.Get("data").AsGodotObject();
        _material = node.Get("material").AsGodotObject();
    }

    /// <summary>
    /// Adds a Terrain3D node under <paramref name="parent"/> (not owned, so it's never saved with the scene) and copies
    /// the map into it. Frees any Terrain3D left from before, e.g. by an editor script reload.
    /// </summary>
    public static Terrain3DBridge Create(Node3D parent, HeightMap map, SplatMap splat, Shader shader)
    {
        foreach (var child in parent.GetChildren())
            if (child.Name == NodeName) child.Free();

        int cellsX = map.Width - 1, cellsZ = map.Depth - 1;
        int region = RegionSize(cellsX, cellsZ);

        var node = (Node3D)ClassDB.Instantiate("Terrain3D").AsGodotObject();
        node.Name = NodeName;
        // Terrain3D places regions in world space by their location; keep the node itself out of the parent's transform.
        node.TopLevel = true;
        node.Set("collision_mode", 0); // DISABLED: queries and raycasts run on HeightMap
        node.Set("vertex_spacing", map.CellSize);
        parent.AddChild(node);
        // Ignored before the node is in the tree.
        node.Call("change_region_size", region);

        var bridge = new Terrain3DBridge(node, region, cellsX, cellsZ);
        // Terrain3D shows a checkered debug view when it has no textures of its own (ours come from our uniforms). Its
        // debug views append code to the override that overwrites ALBEDO, so they must stay off.
        bridge._material.Set("show_checkered", false);
        bridge._material.Set("world_background", 0); // NONE: nothing drawn outside the regions
        bridge._material.Call("set_shader_override", shader);
        bridge._material.Call("enable_shader_override", true);
        bridge.AddRegions(map, splat);
        return bridge;
    }

    /// <summary>
    /// Region side in vertices. An edit re-uploads each touched region's whole map, so regions are as small as Terrain3D
    /// allows: 16 per side fit its region locations (-16..15) once centred with room to spare. 256² up to 4k maps, 512²
    /// for the 28.7 km build area.
    /// </summary>
    private static int RegionSize(int cellsX, int cellsZ)
    {
        int side = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(cellsX, cellsZ));
        return Math.Clamp(side / MaxRegionsPerSide, MinRegionSize, 2048);
    }

    /// <summary>
    /// World XZ of the map's (0, 0) corner: the map is centred on the world origin, on whole regions (Terrain3D draws a
    /// region at location × region size). The <c>Terrain</c> node goes here, so map and world differ by this offset only.
    /// </summary>
    public static Vector2 Origin(HeightMap map)
    {
        int cellsX = map.Width - 1, cellsZ = map.Depth - 1, region = RegionSize(cellsX, cellsZ);
        int regionsX = (cellsX + region - 1) / region, regionsZ = (cellsZ + region - 1) / region;
        return new Vector2(-(regionsX / 2), -(regionsZ / 2)) * (region * map.CellSize);
    }

    /// <summary>Removes the Terrain3D node.</summary>
    public void Free()
    {
        if (GodotObject.IsInstanceValid(_node)) _node.Free();
    }

    private void AddRegions(HeightMap map, SplatMap splat)
    {
        int r = _region;
        var heights = new float[r * r];
        var control = new uint[r * r];
        // No ground masks until the first lake search (all zero = no shore, gullies, wear or deposits).
        var blankColor = new byte[r * r * 4];
        for (int rz = 0; rz < _regionsZ; rz++)
        {
            for (int rx = 0; rx < _regionsX; rx++)
            {
                int x0 = rx * r, z0 = rz * r;
                int w = Math.Min(r, RenderedX - x0), d = Math.Min(r, RenderedZ - z0);
                bool padded = w < r || d < r, painted = padded;
                for (int z = 0; z < r; z++)
                {
                    // Padding repeats the last drawn row/column; it's a hole anyway.
                    var src = map.Row(z0 + Math.Min(z, d - 1)).Slice(x0, w);
                    var dst = heights.AsSpan(z * r, r);
                    src.CopyTo(dst);
                    dst[w..].Fill(src[w - 1]);
                }
                for (int tz = z0 / SplatMap.TileSize; !painted && tz <= (z0 + d - 1) / SplatMap.TileSize; tz++)
                    for (int tx = x0 / SplatMap.TileSize; tx <= (x0 + w - 1) / SplatMap.TileSize; tx++)
                        painted |= splat.GetTile(tx, tz) is not null;

                var region = ClassDB.Instantiate("Terrain3DRegion").AsGodotObject();
                region.Call("set_region_size", r);
                region.Call("set_vertex_spacing", map.CellSize);
                region.Call("set_location", _location + new Vector2I(rx, rz));
                region.Call("set_height_map", FloatImage(r, r, MemoryMarshal.AsBytes(heights.AsSpan())));
                if (painted)
                {
                    for (int z = 0; z < r; z++)
                        for (int x = 0; x < r; x++)
                            control[z * r + x] = x < w && z < d ? splat.Get(x0 + x, z0 + z) : HoleBit;
                    region.Call("set_control_map", FloatImage(r, r, MemoryMarshal.AsBytes(control.AsSpan())));
                }
                region.Call("set_color_map", Image.CreateFromData(r, r, false, Image.Format.Rgba8, blankColor));
                region.Call("sanitize_maps"); // fills in a blank control map
                region.Call("calc_height_range");
                _data.Call("add_region", region, false);

                int i = rz * _regionsX + rx;
                _regions[i] = region;
                _heightImages[i] = (Image)region.Call("get_height_map").AsGodotObject();
                _controlImages[i] = (Image)region.Call("get_control_map").AsGodotObject();
                _colorImages[i] = (Image)region.Call("get_color_map").AsGodotObject();
            }
        }
        _data.Call("update_maps", 3, true, false); // TYPE_MAX: every map of every region
        _data.Call("calc_height_range", false);
    }

    private static Image FloatImage(int w, int d, ReadOnlySpan<byte> bytes) =>
        Image.CreateFromData(w, d, false, Image.Format.Rf, bytes.ToArray());

    /// <summary>Copies heights inside <paramref name="rect"/> to the render copy.</summary>
    public void PushHeights(HeightMap map, VertexRect rect)
    {
        Push(rect, TypeHeight, (x0, z0, w, d, bytes) =>
        {
            float min = float.MaxValue, max = float.MinValue;
            var dst = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            for (int z = 0; z < d; z++)
            {
                var row = map.Row(z0 + z).Slice(x0, w);
                row.CopyTo(dst.Slice(z * w, w));
                foreach (float h in row) { min = Math.Min(min, h); max = Math.Max(max, h); }
            }
            return new Vector2(min, max);
        });
    }

    /// <summary>Copies painted control values inside <paramref name="rect"/> to the render copy.</summary>
    public void PushControl(SplatMap splat, VertexRect rect)
    {
        Push(rect, TypeControl, (x0, z0, w, d, bytes) =>
        {
            var dst = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                    dst[z * w + x] = splat.Get(x0 + x, z0 + z);
            return null;
        });
    }

    /// <summary>
    /// Copies ground masks (one packed RGBA8 value per heightmap vertex, see <see cref="Erosion.LakeMap.Ground"/>) to the
    /// render copy. With <paramref name="previous"/> (the masks now on the GPU), only regions that differ are uploaded.
    /// </summary>
    public void PushGround(uint[] ground, uint[]? previous, int mapWidth)
    {
        int r = _region;
        LastPushRegions = 0;
        for (int rz = 0; rz < _regionsZ; rz++)
            for (int rx = 0; rx < _regionsX; rx++)
            {
                int x0 = rx * r, z0 = rz * r;
                int w = Math.Min(r, RenderedX - x0), d = Math.Min(r, RenderedZ - z0);
                if (previous is not null && !Differs(x0, z0, w, d)) continue;
                PushRegion(rx, rz, x0, z0, w, d, TypeColor, bytes =>
                {
                    var dst = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
                    for (int z = 0; z < d; z++)
                        ground.AsSpan((z0 + z) * mapWidth + x0, w).CopyTo(dst.Slice(z * w, w));
                    return null;
                });
            }
        FinishPush(TypeColor);

        bool Differs(int x0, int z0, int w, int d)
        {
            for (int z = 0; z < d; z++)
            {
                int o = (z0 + z) * mapWidth + x0;
                if (!ground.AsSpan(o, w).SequenceEqual(previous.AsSpan(o, w))) return true;
            }
            return false;
        }
    }

    /// <summary>Writes the part of a vertex rect in each region: fill builds its bytes and may return a height range.</summary>
    private void Push(VertexRect rect, int type, Func<int, int, int, int, byte[], Vector2?> fill)
    {
        rect = new VertexRect(Math.Max(rect.MinX, 0), Math.Max(rect.MinZ, 0),
            Math.Min(rect.MaxX, RenderedX - 1), Math.Min(rect.MaxZ, RenderedZ - 1));
        LastPushRegions = 0;
        if (rect.IsEmpty) return;
        int r = _region;
        for (int rz = rect.MinZ / r; rz <= rect.MaxZ / r; rz++)
        {
            for (int rx = rect.MinX / r; rx <= rect.MaxX / r; rx++)
            {
                int x0 = Math.Max(rect.MinX, rx * r), z0 = Math.Max(rect.MinZ, rz * r);
                int w = Math.Min(rect.MaxX, rx * r + r - 1) - x0 + 1;
                int d = Math.Min(rect.MaxZ, rz * r + r - 1) - z0 + 1;
                PushRegion(rx, rz, x0, z0, w, d, type, bytes => fill(x0, z0, w, d, bytes));
            }
        }
        FinishPush(type);
    }

    /// <summary>Blits a w×d patch at vertex (x0, z0) into region (rx, rz)'s map of the given type. 4 bytes per pixel.</summary>
    private void PushRegion(int rx, int rz, int x0, int z0, int w, int d, int type, Func<byte[], Vector2?> fill)
    {
        var bytes = new byte[w * d * 4];
        var range = fill(bytes);
        var patch = Image.CreateFromData(w, d, false, type == TypeColor ? Image.Format.Rgba8 : Image.Format.Rf, bytes);
        int i = rz * _regionsX + rx;
        var target = type switch { TypeHeight => _heightImages[i], TypeControl => _controlImages[i], _ => _colorImages[i] };
        target.BlitRect(patch, new Rect2I(0, 0, w, d), new Vector2I(x0 - rx * _region, z0 - rz * _region));
        patch.Dispose();
        if (range is { } h) _regions[i].Call("update_heights", h);
        _regions[i].Call("set_edited", true);
        _edited.Add(i);
    }

    private void FinishPush(int type)
    {
        if (_edited.Count == 0) return;
        LastPushRegions = _edited.Count;
        _data.Call("update_maps", type, false, false);
        foreach (int i in _edited) _regions[i].Call("set_edited", false);
        _edited.Clear();
        if (type == TypeHeight) _data.Call("calc_height_range", false);
    }

    /// <summary>Recomputes every region's height range exactly (pushes only ever widen it).</summary>
    public void RecalcHeightRange() => _data.Call("calc_height_range", true);

    /// <summary>Swaps the shader override (Terrain3D copies its code when set, so edits to the Shader need this).</summary>
    public void SetShader(Shader shader) => _material.Call("set_shader_override", shader);

    public void SetParam(string name, Variant value) => _material.Call("set_shader_param", name, value);

    /// <summary>Copies every public uniform set on <paramref name="source"/> (the material tuned in the inspector).</summary>
    public void CopyParams(ShaderMaterial source)
    {
        if (source.Shader is null) return;
        foreach (var u in source.Shader.GetShaderUniformList())
        {
            string name = u.AsGodotDictionary()["name"].AsString();
            if (name.StartsWith('_')) continue;
            var value = source.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil) SetParam(name, value);
        }
    }

    /// <summary>
    /// Points the clipmap at the viewport's camera (call every frame; only calls through when it changes), and gives it
    /// enough LOD levels to reach the farthest ground the camera can see: whichever is nearer, its far plane or the
    /// farthest map corner (<paramref name="bounds"/>: the map in world XZ). Each level doubles the clipmap's reach and adds
    /// a ring of triangles, so the default 7 levels (~28 km at 3.5 m cells) are only raised for the whole-map views of big maps.
    /// </summary>
    public void FollowCamera(Camera3D? camera, Rect2 bounds)
    {
        if (camera is null) return;
        if (camera != _camera)
        {
            _camera = camera;
            _node.Call("set_camera", camera);
            _meshSize = _node.Get("mesh_size").AsInt32();
            _cellSize = (float)_node.Get("vertex_spacing").AsDouble();
        }
        var c = camera.GlobalPosition;
        float dx = Math.Max(Math.Abs(c.X - bounds.Position.X), Math.Abs(c.X - bounds.End.X));
        float dz = Math.Max(Math.Abs(c.Z - bounds.Position.Y), Math.Abs(c.Z - bounds.End.Y));
        float reach = Math.Min(camera.Far, MathF.Sqrt(dx * dx + dz * dz));
        // Measured reach of the clipmap: about 1.3 × mesh_size × 2^lods vertices from the camera.
        float verts = reach / _cellSize / (1.3f * Math.Max(_meshSize, 8));
        int lods = Math.Clamp((int)MathF.Ceiling(MathF.Log2(Math.Max(verts, 1f))), DefaultLods, MaxLods);
        if (lods == _lods) return;
        _lods = lods;
        _node.Set("mesh_lods", lods);
    }

    private const int DefaultLods = 7, MaxLods = 10;
    private int _lods = DefaultLods, _meshSize = 48;
    private float _cellSize = 1f;
}
