using System;
using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// Draws the <see cref="WaterSim"/>'s water. Each <see cref="WaterSim.PageSize"/>² page of water cells has a material
/// and, once it first holds water, two data textures (surface, depth, velocity per cell; see <c>cs_water_read</c>): the
/// latest sim tick and the one before, which the shader blends between so the water moves smoothly although the sim
/// ticks only 2–64 times a second; plus a pollutant texture. Each of its tiles (<see cref="WaterSim.TileSize"/>² cells)
/// is a grid mesh lifted to the water surface in the vertex shader, drawn only while the tile holds water. Only pages
/// the sim changed are uploaded, once per published tick.
/// Near tiles have two vertices per cell (the shader samples the water bilinearly, so the surface is smoother than the
/// sim grid); far tiles use coarser grids (every 2nd, 4th or 8th cell).
/// A page's material and tile nodes are made the first time it holds water (a 28.7 km map has 256 pages and 4096 tiles,
/// most of them dry).
/// </summary>
public partial class WaterSurface : Node3D
{
    /// <summary>Grid spacing per LOD, in water cells.</summary>
    private static readonly float[] LodSteps = [0.5f, 1f, 2f, 4f, 8f];
    /// <summary>Distance (in tile widths) beyond which each coarser grid is used.</summary>
    private static readonly float[] LodDistances = [1.5f, 3f, 7f, 14f];

    private sealed class Page
    {
        public required ShaderMaterial Material;
        public required MeshInstance3D?[] Tiles;
        public required int[] Lods;
        /// <summary>The two data textures (null until the page first holds water); Current is the latest tick.</summary>
        public ImageTexture?[] Data = new ImageTexture?[2];
        public int Current;
        /// <summary>True while the previous-tick slot shows the same data as the current one.</summary>
        public bool Settled = true;
        public ImageTexture? Pollution;
    }

    private WaterSim? _sim;
    private Page?[] _pages = [];
    private ShaderMaterial? _baseMaterial;
    private Aabb _tileAabb;
    private Vector2 _mapSize, _mapOrigin;
    private ArrayMesh[] _meshes = [];
    private float[] _scratch = [], _pollutionScratch = [];
    private byte[] _bytes = [], _pollutionBytes = [];
    private Image? _image, _pollutionImage;
    private bool[] _tileWater = [];
    private int _tilesPerPage;
    private long _published = -1;
    private double _sinceTick;

    private float _edgeFade = 250f;
    /// <summary>
    /// Metres over which the water fades out toward the border (into the edge fog). The horizon ring's sea carries on past
    /// the border, so there it's only a couple of metres.
    /// </summary>
    public float EdgeFade
    {
        get => _edgeFade;
        set
        {
            _edgeFade = value;
            foreach (var page in _pages) page?.Material.SetShaderParameter("edge_fade", value);
        }
    }

    public void Init(WaterSim sim, Material? material, HeightMap ground)
    {
        _sim = sim;
        int n = WaterSim.PageSize + 1;
        _tilesPerPage = WaterSim.PageSize / sim.TileSize;
        _scratch = new float[n * n * 4];
        _bytes = new byte[_scratch.Length * sizeof(float)];
        _pollutionScratch = new float[n * n];
        _pollutionBytes = new byte[_pollutionScratch.Length * sizeof(float)];
        _image = Image.CreateEmpty(n, n, false, Image.Format.Rgbaf);
        _pollutionImage = Image.CreateEmpty(n, n, false, Image.Format.Rf);
        _tileWater = new bool[_tilesPerPage * _tilesPerPage];
        _baseMaterial = material as ShaderMaterial ?? new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _meshes = new ArrayMesh[LodSteps.Length];
        for (int i = 0; i < LodSteps.Length; i++) _meshes[i] = GridMesh((int)(sim.TileSize / LodSteps[i]), LodSteps[i] * sim.CellSize);

        // Heights come from the texture, so the mesh's own bounds (flat at 0) would cull it wrongly.
        var (min, max) = ground.GetRange();
        float tileMetres = sim.TileSize * sim.CellSize;
        _tileAabb = new Aabb(new Vector3(0, min - 50f, 0), new Vector3(tileMetres, max - min + 250f, tileMetres));
        _mapSize = new Vector2(ground.SizeX, ground.SizeZ);
        // A child of the Terrain node, which sits at the map's (0, 0) corner.
        _mapOrigin = new Vector2(GlobalPosition.X, GlobalPosition.Z);
        _pages = new Page?[sim.PagesX * sim.PagesZ];
        Upload(force: true);
    }

    private Page CreatePage(int px, int pz)
    {
        var sim = _sim!;
        float pageMetres = WaterSim.PageSize * sim.CellSize, tileMetres = sim.TileSize * sim.CellSize;
        var mat = (ShaderMaterial)_baseMaterial!.Duplicate();
        mat.SetShaderParameter("blend", 1f);
        mat.SetShaderParameter("cell_size", sim.CellSize);
        mat.SetShaderParameter("map_size", _mapSize);
        mat.SetShaderParameter("map_origin", _mapOrigin);
        mat.SetShaderParameter("edge_fade", _edgeFade);
        var tiles = new MeshInstance3D?[_tilesPerPage * _tilesPerPage];
        for (int tz = 0; tz < _tilesPerPage; tz++)
            for (int tx = 0; tx < _tilesPerPage; tx++)
            {
                int cx = px * WaterSim.PageSize + tx * sim.TileSize, cz = pz * WaterSim.PageSize + tz * sim.TileSize;
                if (cx >= sim.Width - 1 || cz >= sim.Depth - 1) continue;
                var tile = new MeshInstance3D
                {
                    Name = $"Tile{cx / sim.TileSize}_{cz / sim.TileSize}",
                    Position = new Vector3(px * pageMetres + tx * tileMetres, 0, pz * pageMetres + tz * tileMetres),
                    MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    CustomAabb = _tileAabb,
                    Visible = false,
                };
                AddChild(tile);
                tile.SetInstanceShaderParameter("tile_offset", new Vector2(tx * tileMetres, tz * tileMetres));
                tiles[tz * _tilesPerPage + tx] = tile;
            }
        var lods = new int[tiles.Length];
        System.Array.Fill(lods, -1);
        return _pages[pz * sim.PagesX + px] = new Page { Material = mat, Tiles = tiles, Lods = lods };
    }

    public override void _Process(double delta)
    {
        if (_sim is null) return;
        long published = _sim.Publishes;
        if (published != _published)
        {
            _published = published;
            _sinceTick = 0;
            Upload(force: false);
        }
        else _sinceTick += delta;
        // Blend over one tick's worth of real time (the latest snapshot is one tick behind the sim, at most).
        var settings = _sim.Settings;
        double tickReal = WaterSim.TickSeconds / Math.Max(settings.Speed, 0.001);
        float blend = settings.Paused ? 1f : (float)Math.Clamp(_sinceTick / tickReal, 0, 1);
        foreach (var page in _pages)
            if (page is { Settled: false })
            {
                page.Material.SetShaderParameter("blend", blend);
                if (blend >= 1f) Settle(page);
            }
        UpdateLods();
    }

    private void Upload(bool force)
    {
        if (_sim is null || _image is null || _pollutionImage is null) return;
        int n = WaterSim.PageSize + 1;
        for (int pz = 0; pz < _sim.PagesZ; pz++)
            for (int px = 0; px < _sim.PagesX; px++)
            {
                var page = _pages[pz * _sim.PagesX + px];
                if (!_sim.CopyPage(px, pz, _scratch, _pollutionScratch, _tileWater, out bool wet, force))
                {
                    // Unchanged this tick: it has nothing left to blend toward.
                    if (page is { Settled: false }) Settle(page);
                    continue;
                }
                if (page is null)
                {
                    if (!wet) continue;
                    page = CreatePage(px, pz);
                }
                for (int i = 0; i < page.Tiles.Length; i++)
                    if (page.Tiles[i] is { } tile) tile.Visible = _tileWater[i];
                if (!wet && page.Data[0] is null) continue;
                System.Buffer.BlockCopy(_scratch, 0, _bytes, 0, _bytes.Length);
                _image.SetData(n, n, false, Image.Format.Rgbaf, _bytes);
                System.Buffer.BlockCopy(_pollutionScratch, 0, _pollutionBytes, 0, _pollutionBytes.Length);
                _pollutionImage.SetData(n, n, false, Image.Format.Rf, _pollutionBytes);
                if (page.Data[0] is null)
                {
                    // First water on this page: both slots start with it.
                    page.Data[0] = ImageTexture.CreateFromImage(_image);
                    page.Data[1] = ImageTexture.CreateFromImage(_image);
                    page.Pollution = ImageTexture.CreateFromImage(_pollutionImage);
                    page.Material.SetShaderParameter("pollution", page.Pollution);
                    page.Current = 0;
                    Settle(page);
                    continue;
                }
                page.Pollution!.Update(_pollutionImage);
                // The new tick goes into the slot that isn't showing as current; the old current becomes previous.
                int next = 1 - page.Current;
                page.Data[next]!.Update(_image);
                page.Material.SetShaderParameter("water_prev", page.Data[page.Current]);
                page.Material.SetShaderParameter("water_data", page.Data[next]);
                page.Material.SetShaderParameter("blend", 0f);
                page.Current = next;
                page.Settled = false;
            }
    }

    /// <summary>Points both slots at the latest data (the blend is done).</summary>
    private static void Settle(Page page)
    {
        var current = page.Data[page.Current];
        page.Material.SetShaderParameter("water_data", current);
        page.Material.SetShaderParameter("water_prev", current);
        page.Material.SetShaderParameter("blend", 1f);
        page.Settled = true;
    }

    private void UpdateLods()
    {
        if (_sim is null || GetViewport().GetCamera3D() is not { } cam) return;
        var eye = cam.GlobalPosition;
        float tileMetres = _sim.TileSize * _sim.CellSize;
        foreach (var page in _pages)
            for (int i = 0; page is not null && i < page.Tiles.Length; i++)
            {
                if (page.Tiles[i] is not { Visible: true } tile) continue;
                var centre = tile.GlobalPosition + new Vector3(tileMetres * 0.5f, 0, tileMetres * 0.5f);
                float d = new Vector3(eye.X - centre.X, (eye.Y - centre.Y) * 0.5f, eye.Z - centre.Z).Length() / tileMetres;
                int lod = 0;
                while (lod < LodDistances.Length && d > LodDistances[lod]) lod++;
                if (lod == page.Lods[i]) continue;
                page.Lods[i] = lod;
                tile.Mesh = _meshes[lod];
                tile.SetInstanceShaderParameter("lod_step", LodSteps[lod]);
            }
    }

    /// <summary>A flat grid of <paramref name="quads"/>² quads, <paramref name="spacing"/> metres apart, from the origin.</summary>
    private static ArrayMesh GridMesh(int quads, float spacing)
    {
        int n = quads + 1;
        var verts = new Vector3[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                verts[z * n + x] = new Vector3(x * spacing, 0, z * spacing);
        var idx = new int[quads * quads * 6];
        int k = 0;
        for (int z = 0; z < quads; z++)
            for (int x = 0; x < quads; x++)
            {
                int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                // Clockwise seen from above (Godot's front face).
                idx[k++] = a; idx[k++] = b; idx[k++] = c;
                idx[k++] = c; idx[k++] = b; idx[k++] = d;
            }
        var normals = new Vector3[verts.Length];
        System.Array.Fill(normals, Vector3.Up);
        var data = new Godot.Collections.Array();
        data.Resize((int)Mesh.ArrayType.Max);
        data[(int)Mesh.ArrayType.Vertex] = verts;
        data[(int)Mesh.ArrayType.Normal] = normals;
        data[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, data);
        return mesh;
    }
}
