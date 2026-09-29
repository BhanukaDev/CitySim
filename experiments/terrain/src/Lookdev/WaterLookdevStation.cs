using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Lookdev;

/// <summary>
/// One test site for tweaking the water look without the sim (scenes/WaterLookdev.tscn): a mesa with a river running off
/// a 22 m cliff into a plunge pool with a beach, water from hand-made data in the same layout the sim uploads, and a
/// curtain and mist built the way WaterFalls builds them. Everything it builds is left out of the saved scene.
/// The curtain and mist use the materials as they are, so edits show at once. The water material gets per-map data
/// textures, so it runs on a copy that is re-synced from <see cref="WaterMaterial"/> a few times a second: edit the
/// template .tres, not the copy.
/// </summary>
[Tool]
public partial class WaterLookdevStation : Node3D
{
    // Layout (metres, station-local). The river runs +Z, off the cliff at Lip, into the pool.
    public const float Width = 180f, Length = 150f;
    private const float Cs = 3.5f; // the game's water cell size, so the surface looks as coarse as in game
    private const float Cx = 90f, Lip = 60f, Top = 25f, Bed = 22f, RiverLevel = 24f, Lowland = 3f;
    private const float ChannelHalf = 14f, FallHalf = 11f;

    private string _title = "Template";
    private ShaderMaterial? _water, _curtain, _mist, _rings;
    private ShaderMaterial? _waterCopy;
    private double _syncTimer;

    [Export] public string Title { get => _title; set { _title = value; Rebuild(); } }
    [Export] public ShaderMaterial? WaterMaterial { get => _water; set { _water = value; Rebuild(); } }
    [Export] public ShaderMaterial? CurtainMaterial { get => _curtain; set { _curtain = value; Rebuild(); } }
    [Export] public ShaderMaterial? MistMaterial { get => _mist; set { _mist = value; Rebuild(); } }
    /// <summary>Optional splash rings on the pool (shaders/waterfall_rings.gdshader). Look-dev only so far: the game's
    /// WaterFalls doesn't place them yet.</summary>
    [Export] public ShaderMaterial? RingsMaterial { get => _rings; set { _rings = value; Rebuild(); } }

    [ExportToolButton("Rebuild")]
    public Callable RebuildButton => Callable.From(Build);

    /// <summary>Where cameras should look: the foot of the fall.</summary>
    public Vector3 Focus => GlobalPosition + new Vector3(Cx, 10f, Lip + 4f);

    public override void _Ready() => Build();

    private void Rebuild()
    {
        if (IsInsideTree()) Build();
    }

    public override void _Process(double delta)
    {
        // Pull template edits into the water copy (ShaderMaterial doesn't signal parameter changes).
        _syncTimer -= delta;
        if (_syncTimer > 0) return;
        _syncTimer = 0.25;
        SyncWater();
    }

    private void Build()
    {
        foreach (var child in GetChildren())
            if (child.HasMeta("lookdev_built")) { RemoveChild(child); child.QueueFree(); }

        Add(new MeshInstance3D { Name = "Ground", Mesh = GroundMesh(), MaterialOverride = GroundMaterial() });
        BuildWater();
        BuildFall();
        Add(new Label3D
        {
            Name = "Title",
            Text = _title,
            Position = new Vector3(Cx, 48f, 10f),
            PixelSize = 0.12f,
            FontSize = 64,
            OutlineSize = 16,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
        });
    }

    private void Add(Node node)
    {
        node.SetMeta("lookdev_built", true);
        AddChild(node); // no Owner: built nodes aren't saved into the scene
    }

    // --- Ground ---

    private static float S(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Mesa(float x, float z) =>
        S(0f, 25f, x) * S(Width, Width - 25f, x) * S(-40f, -15f, z) * (1f - S(Lip, Lip + 3f, z));

    private static float Pool(float x, float z)
    {
        float r = MathF.Sqrt(Sq((x - Cx) / 75f) + Sq((z - 88f) / 40f));
        float g = Lowland + (-6f - Lowland) * (1f - S(0.35f, 1f, r));
        return g - 4f * MathF.Exp(-(Sq(x - Cx) + Sq(z - (Lip + 5f))) / 150f); // plunge hole under the fall
    }

    private static float Ground(float x, float z)
    {
        float carve = (Top - Bed) * S(ChannelHalf, ChannelHalf - 4f, MathF.Abs(x - Cx)) * S(2f, 12f, z);
        float m = Mesa(x, z);
        return Pool(x, z) * (1f - m) + (Top - carve) * m;
    }

    private static float Sq(float v) => v * v;

    private static ArrayMesh GroundMesh()
    {
        const float step = 1f, z0 = -45f;
        int nx = (int)(Width / step) + 1, nz = (int)((Length - z0) / step) + 1;
        var verts = new Vector3[nx * nz];
        var normals = new Vector3[nx * nz];
        var colors = new Color[nx * nz];
        var grass = new Color(0.33f, 0.47f, 0.2f);
        var rock = new Color(0.45f, 0.42f, 0.38f);
        var sand = new Color(0.76f, 0.69f, 0.5f);
        var gravel = new Color(0.47f, 0.42f, 0.33f);
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                float x = i * step, z = z0 + j * step, y = Ground(x, z);
                var n = new Vector3(Ground(x - 0.5f, z) - Ground(x + 0.5f, z), 1f, Ground(x, z - 0.5f) - Ground(x, z + 0.5f)).Normalized();
                var c = grass.Lerp(sand, S(1.2f, 0.4f, y) * (y < 5f ? 1f : 0f));
                if (y < -0.3f || (Mesa(x, z) > 0.5f && y < RiverLevel - 0.2f)) c = gravel;
                c = c.Lerp(rock, S(0.25f, 0.5f, 1f - n.Y));
                int k = j * nx + i;
                verts[k] = new Vector3(x, y, z);
                normals[k] = n;
                colors[k] = c;
            }
        var indices = new List<int>((nx - 1) * (nz - 1) * 6);
        for (int j = 0; j < nz - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                indices.AddRange([a, b, c, c, b, d]);
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D GroundMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Roughness = 1f,
    };

    // --- Water surface ---

    /// <summary>Fake sim output at a point: surface height and flow (m/s).</summary>
    private static (float Surface, Vector2 Flow) Water(float x, float z)
    {
        if (Mesa(x, z) > 0.5f && MathF.Abs(x - Cx) < ChannelHalf)
            return (RiverLevel, new Vector2(0f, 1.5f + 3f * S(20f, Lip, z)));
        float plume = 3.5f * MathF.Exp(-MathF.Max(z - Lip - 3f, 0f) / 14f) * MathF.Exp(-Sq((x - Cx) / 16f)) + 0.15f;
        return (0f, new Vector2((x - Cx) / 40f * plume * 0.5f, plume));
    }

    private void BuildWater()
    {
        int nx = (int)MathF.Ceiling(Width / Cs) + 2, nz = (int)MathF.Ceiling(Length / Cs) + 2;
        var surf = new float[nx * nz];
        var depth = new float[nx * nz];
        var flow = new Vector2[nx * nz];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                float x = i * Cs, z = j * Cs;
                var (s, v) = Water(x, z);
                int k = j * nx + i;
                surf[k] = s;
                depth[k] = s - Ground(x, z);
                flow[k] = v;
            }
        // Same layout as cs_water_read: (surface, depth, velocity x, velocity z). Dry cells next to water keep the
        // water level at depth 0 so the ground cuts the shoreline; the rest are hidden (depth -1).
        var data = new float[nx * nz * 4];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i, o = k * 4;
                if (depth[k] > 0.02f)
                {
                    data[o] = surf[k]; data[o + 1] = depth[k]; data[o + 2] = flow[k].X; data[o + 3] = flow[k].Y;
                    continue;
                }
                float near = float.MinValue;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        int a = i + di, b = j + dj;
                        if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                        int q = b * nx + a;
                        if (depth[q] > 0.02f) near = MathF.Max(near, surf[q]);
                    }
                data[o] = near > float.MinValue ? near : Ground(i * Cs, j * Cs);
                data[o + 1] = near > float.MinValue ? 0f : -1f;
            }
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(nx, nz, false, Image.Format.Rgbaf, bytes));

        _waterCopy = _water?.Duplicate() as ShaderMaterial;
        if (_waterCopy is not null)
        {
            _waterCopy.SetShaderParameter("water_data", tex);
            _waterCopy.SetShaderParameter("water_prev", tex);
            SyncWater();
        }
        var surface = new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh
            {
                Size = new Vector2(Width, Length),
                CenterOffset = new Vector3(Width * 0.5f, 0f, Length * 0.5f),
                // Two vertices per cell, like the nearest LOD in game.
                SubdivideWidth = (int)(Width / (Cs * 0.5f)) - 1,
                SubdivideDepth = (int)(Length / (Cs * 0.5f)) - 1,
            },
            MaterialOverride = _waterCopy,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(0f, -10f, 0f), new Vector3(Width, 40f, Length)),
        };
        Add(surface);
        surface.SetInstanceShaderParameter("tile_offset", Vector2.Zero);
        surface.SetInstanceShaderParameter("lod_step", 0.5f);
    }

    private static readonly HashSet<string> PerMap = ["water_data", "water_prev", "pollution", "blend", "cell_size", "map_size", "map_origin"];

    private void SyncWater()
    {
        if (_water?.Shader is not { } shader || _waterCopy is null) return;
        if (_waterCopy.Shader != shader) _waterCopy.Shader = shader;
        foreach (var item in shader.GetShaderUniformList())
        {
            var u = item.AsGodotDictionary();
            string name = (string)u["name"];
            if ((((int)u["usage"]) & (int)(PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup | PropertyUsageFlags.Category)) != 0) continue;
            if (PerMap.Contains(name)) continue;
            _waterCopy.SetShaderParameter(name, _water.GetShaderParameter(name));
        }
        _waterCopy.RenderPriority = _water.RenderPriority;
        _waterCopy.SetShaderParameter("cell_size", Cs);
        // No edge fade: the whole station counts as far inside the map.
        _waterCopy.SetShaderParameter("map_origin", new Vector2(GlobalPosition.X, GlobalPosition.Z) - new Vector2(1000f, 1000f));
        _waterCopy.SetShaderParameter("map_size", new Vector2(2000f + Width, 2000f + Length));
    }

    // --- Curtain and mist (as WaterFalls.AddCurtain and its mist bins) ---

    private static float Hash(int a, int b)
    {
        uint h = (uint)(a * 374761393 + b * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    private void BuildFall()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var uv2s = new List<Vector2>();
        var indices = new List<int>();
        var landings = new List<Vector3>();
        var dir = new Vector2(0f, 1f);
        var perp = new Vector2(-dir.Y, dir.X);
        const float top = RiverLevel, baseY = 0f, height = top - baseY, speed = 4.5f, gravity = 9.81f;
        const int rows = 10;
        for (float lx = Cx - FallHalf + Cs * 0.5f; lx < Cx + FallHalf; lx += Cs)
        {
            // Ground every half cell from the lip, until it stops falling.
            float stepLen = Cs * 0.5f;
            var face = new List<float> { Ground(lx, Lip) };
            for (int k = 1; k < 24; k++)
            {
                float h = Ground(lx, Lip + k * stepLen);
                face.Add(h);
                if (h <= baseY) break;
            }
            float FaceAt(float y)
            {
                for (int k = 1; k < face.Count; k++)
                    if (face[k] <= y) return (k - 1 + (face[k - 1] - y) / MathF.Max(face[k - 1] - face[k], 1e-4f)) * stepLen;
                return (face.Count - 1) * stepLen;
            }
            // Weaker at the fall's edges, as the sim's flux would be.
            float strength = 1f - 0.4f * S(FallHalf - 5f, FallHalf, MathF.Abs(lx - Cx));
            var color = new Color(1f, 1f, 1f, strength);
            var lip = new Vector2(lx, Lip);
            int first = verts.Count;
            Vector3 prev = default, at = default;
            for (int row = 0; row <= rows; row++)
            {
                float s = (float)row / rows, drop = s * height, y = top - drop;
                float standoff = MathF.Min(0.3f + drop * 0.08f, 4f);
                float fwd = MathF.Max(speed * MathF.Sqrt(2f * drop / gravity), FaceAt(y) + standoff);
                var p = lip + dir * fwd;
                at = new Vector3(p.X, y, p.Y);
                float half = Cs * 0.6f * (1f + 0.35f * s);
                var tangent = row == 0 ? new Vector3(dir.X, -1f, dir.Y) : at - prev;
                var normal = tangent.Cross(new Vector3(perp.X, 0f, perp.Y)).Normalized();
                prev = at;
                for (int side = 0; side < 2; side++)
                {
                    var e = perp * (side == 0 ? -half : half);
                    var v = at + new Vector3(e.X, 0f, e.Y);
                    verts.Add(v); normals.Add(normal); colors.Add(color);
                    uvs.Add(new Vector2(v.X * perp.X + v.Z * perp.Y, drop));
                    uv2s.Add(new Vector2(side, s));
                }
                if (row == 0) continue;
                int b = first + (row - 1) * 2;
                indices.AddRange([b, b + 1, b + 2, b + 2, b + 1, b + 3]);
            }
            landings.Add(at);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2s.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var curtain = new ArrayMesh();
        curtain.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Add(new MeshInstance3D
        {
            Name = "Curtain",
            Mesh = curtain,
            MaterialOverride = _curtain,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        if (_rings is not null && landings.Count > 0)
        {
            var centre = Vector3.Zero;
            foreach (var l in landings) centre += l;
            centre /= landings.Count;
            float ringSize = FallHalf * 2f * 2.6f;
            Add(new MeshInstance3D
            {
                Name = "Rings",
                Mesh = new PlaneMesh { Size = new Vector2(ringSize, ringSize) },
                Position = new Vector3(centre.X, baseY + 0.05f, centre.Z + ringSize * 0.12f),
                MaterialOverride = _rings,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // Mist: two bins across the fall, a few overlapping puffs each plus spray hanging over the foot.
        var puffs = new List<(Vector3 At, Color Custom)>();
        float size = Math.Clamp(5f + height * 0.3f, 5f, 20f);
        const float bin = 12f;
        for (int side = 0; side < 2; side++)
        {
            var group = landings.FindAll(l => (l.X < Cx) == (side == 0));
            if (group.Count == 0) continue;
            var sum = Vector3.Zero;
            foreach (var l in group) sum += l;
            var at = sum / group.Count;
            for (int k = 0; k < 5; k++)
            {
                float jx = Hash(side, k + 17) - 0.5f, jz = Hash(side, k + 31) - 0.5f, o = size * 0.35f * Hash(side, k + 47);
                puffs.Add((new Vector3(at.X + jx * bin, baseY, at.Z + jz * bin + o), new Color(Hash(side, k), size, 0.9f, 0f)));
            }
            puffs.Add((new Vector3(at.X, baseY + height * 0.3f, at.Z - 1f), new Color(Hash(side, 99), size * 0.8f, 0.55f, height * 0.3f)));
        }
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = puffs.Count,
        };
        for (int k = 0; k < puffs.Count; k++)
        {
            multimesh.SetInstanceTransform(k, new Transform3D(Basis.Identity, puffs[k].At));
            multimesh.SetInstanceCustomData(k, puffs[k].Custom);
        }
        Add(new MultiMeshInstance3D
        {
            Name = "Mist",
            Multimesh = multimesh,
            MaterialOverride = _mist,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The shader moves and grows puffs; bounds from the mesh alone would cull them.
            CustomAabb = new Aabb(new Vector3(Cx - 40f, -10f, Lip - 20f), new Vector3(80f, 50f, 60f)),
        });
    }
}
