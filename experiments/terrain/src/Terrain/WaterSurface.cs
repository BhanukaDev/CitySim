using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// Draws the <see cref="WaterSim"/>'s water. Each <see cref="WaterSim.PageSize"/>² page of water cells has a data texture
/// (surface, depth, velocity per cell; see <c>cs_water_read</c>) and a material; each of its tiles
/// (<see cref="WaterSim.TileSize"/>² cells) is a grid mesh lifted to the water surface in the vertex shader, drawn only
/// while the tile holds water. Only pages the sim changed are uploaded, at most <see cref="UploadHz"/> times a second.
/// Far tiles use coarser grids (every 2nd, 4th or 8th cell).
/// </summary>
public partial class WaterSurface : Node3D
{
    private const double UploadHz = 20;
    private static readonly int[] LodSteps = [1, 2, 4, 8];
    /// <summary>Distance (in tile widths) beyond which each coarser grid is used.</summary>
    private static readonly float[] LodDistances = [3f, 7f, 14f];

    private sealed class Page
    {
        public required Image Image;
        public required ImageTexture Texture;
        public required MeshInstance3D?[] Tiles;
        public required int[] Lods;
    }

    private WaterSim? _sim;
    private Page[] _pages = [];
    private ArrayMesh[] _meshes = [];
    private float[] _scratch = [];
    private byte[] _bytes = [];
    private bool[] _tileWater = [];
    private int _tilesPerPage;
    private double _uploadTimer;

    public void Init(WaterSim sim, Material? material, HeightMap ground)
    {
        _sim = sim;
        int n = WaterSim.PageSize + 1;
        _tilesPerPage = WaterSim.PageSize / sim.TileSize;
        _scratch = new float[n * n * 4];
        _bytes = new byte[_scratch.Length * sizeof(float)];
        _tileWater = new bool[_tilesPerPage * _tilesPerPage];
        var baseMaterial = material as ShaderMaterial ?? new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        float pageMetres = WaterSim.PageSize * sim.CellSize, tileMetres = sim.TileSize * sim.CellSize;
        _meshes = new ArrayMesh[LodSteps.Length];
        for (int i = 0; i < LodSteps.Length; i++) _meshes[i] = GridMesh(sim.TileSize / LodSteps[i], LodSteps[i] * sim.CellSize);

        // Heights come from the texture, so the mesh's own bounds (flat at 0) would cull it wrongly.
        var (min, max) = ground.GetRange();
        var aabb = new Aabb(new Vector3(0, min - 50f, 0), new Vector3(tileMetres, max - min + 250f, tileMetres));
        _pages = new Page[sim.PagesX * sim.PagesZ];
        for (int pz = 0; pz < sim.PagesZ; pz++)
            for (int px = 0; px < sim.PagesX; px++)
            {
                var image = Image.CreateEmpty(n, n, false, Image.Format.Rgbaf);
                var texture = ImageTexture.CreateFromImage(image);
                var mat = (ShaderMaterial)baseMaterial.Duplicate();
                mat.SetShaderParameter("water_data", texture);
                mat.SetShaderParameter("cell_size", sim.CellSize);
                mat.SetShaderParameter("map_size", new Vector2(ground.SizeX, ground.SizeZ));
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
                            CustomAabb = aabb,
                            Visible = false,
                        };
                        AddChild(tile);
                        tile.SetInstanceShaderParameter("tile_offset", new Vector2(tx * tileMetres, tz * tileMetres));
                        tiles[tz * _tilesPerPage + tx] = tile;
                    }
                var lods = new int[tiles.Length];
                System.Array.Fill(lods, -1);
                _pages[pz * sim.PagesX + px] = new Page { Image = image, Texture = texture, Tiles = tiles, Lods = lods };
            }
        Upload(force: true);
    }

    public override void _Process(double delta)
    {
        if (_sim is null) return;
        if ((_uploadTimer += delta) >= 1.0 / UploadHz)
        {
            _uploadTimer = 0;
            Upload(force: false);
        }
        UpdateLods();
    }

    private void Upload(bool force)
    {
        if (_sim is null) return;
        int n = WaterSim.PageSize + 1;
        for (int pz = 0; pz < _sim.PagesZ; pz++)
            for (int px = 0; px < _sim.PagesX; px++)
            {
                var page = _pages[pz * _sim.PagesX + px];
                if (!_sim.CopyPage(px, pz, _scratch, _tileWater, out bool wet, force)) continue;
                for (int i = 0; i < page.Tiles.Length; i++)
                    if (page.Tiles[i] is { } tile) tile.Visible = _tileWater[i];
                if (!wet) continue;
                System.Buffer.BlockCopy(_scratch, 0, _bytes, 0, _bytes.Length);
                page.Image.SetData(n, n, false, Image.Format.Rgbaf, _bytes);
                page.Texture.Update(page.Image);
            }
    }

    private void UpdateLods()
    {
        if (_sim is null || GetViewport().GetCamera3D() is not { } cam) return;
        var eye = cam.GlobalPosition;
        float tileMetres = _sim.TileSize * _sim.CellSize;
        foreach (var page in _pages)
            for (int i = 0; i < page.Tiles.Length; i++)
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
