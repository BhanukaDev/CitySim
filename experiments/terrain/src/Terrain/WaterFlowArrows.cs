using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// Arrows along the water's flow, like Cities: Skylines' water tool: on a grid around where the camera looks, spaced by
/// the camera distance, each pointing downstream, longer and warmer-coloured the faster the water. Shown while a water
/// tool is out or when the Water panel turns them on. Refreshed a few times a second from the sim's snapshot.
/// </summary>
public partial class WaterFlowArrows : Node3D
{
    /// <summary>Most arrows drawn (the grid is at most this many points).</summary>
    public const int MaxArrows = 3600;
    /// <summary>Water speed (m/s) drawn at full length and red.</summary>
    public const float FullSpeed = 3f;

    private Terrain? _terrain;
    private WaterSim? _sim;
    private MultiMeshInstance3D _node = null!;
    private MultiMesh _multi = null!;
    private double _timer;

    public void Init(Terrain terrain, WaterSim sim)
    {
        _terrain = terrain;
        _sim = sim;
        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = ArrowMesh(),
            InstanceCount = MaxArrows,
            VisibleInstanceCount = 0,
        };
        _node = new MultiMeshInstance3D
        {
            Multimesh = _multi,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Drawn in the transparent pass after the water (priority), but hidden behind hills (depth test on).
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 20,
            },
        };
        AddChild(_node);
        VisibilityChanged += () => _timer = 0;
    }

    public override void _Process(double delta)
    {
        if (!Visible || (_timer -= delta) > 0) return;
        _timer = 0.2;
        Refresh();
    }

    private void Refresh()
    {
        if (_terrain is not { } terrain || _sim is not { } sim || GetViewport().GetCamera3D() is not { } cam) return;
        var origin = terrain.GlobalPosition;
        var forward = -cam.GlobalBasis.Z;
        var focus = terrain.Raycast(cam.GlobalPosition, forward, out var hit) ? hit : cam.GlobalPosition + forward * 500f;
        float distance = cam.GlobalPosition.DistanceTo(focus);
        float spacing = Mathf.Clamp(distance / 20f, sim.CellSize * 2f, 200f);
        int side = (int)Mathf.Sqrt(MaxArrows);
        float reach = Mathf.Min(distance * 1.5f, spacing * side * 0.5f);
        int n = Mathf.Min(side, (int)(reach * 2f / spacing) + 1);
        // Grid points on multiples of the spacing, so arrows stay put while the camera moves.
        float x0 = Mathf.Floor((focus.X - origin.X - reach) / spacing) * spacing;
        float z0 = Mathf.Floor((focus.Z - origin.Z - reach) / spacing) * spacing;
        float width = spacing * 0.28f;
        int count = 0;
        for (int j = 0; j < n && count < MaxArrows; j++)
            for (int i = 0; i < n && count < MaxArrows; i++)
            {
                float x = x0 + i * spacing, z = z0 + j * spacing;
                if (sim.DepthAt(x, z) < 0.05f) continue;
                var (vx, vz) = sim.VelocityAt(x, z);
                float speed = Mathf.Sqrt(vx * vx + vz * vz);
                if (speed < 0.05f) continue;
                float y = (sim.SurfaceAt(x, z) ?? terrain.GetHeight(x + origin.X, z + origin.Z) - origin.Y) + 0.3f;
                float length = spacing * Mathf.Clamp(speed / FullSpeed, 0.15f, 1f) * 0.9f;
                var dir = new Vector3(vx, 0f, vz) / speed;
                var basis = new Basis(Vector3.Up, Mathf.Atan2(dir.X, dir.Z)) * Basis.FromScale(new Vector3(width, 1f, length));
                var at = new Vector3(x, y, z) - dir * length * 0.5f;
                _multi.SetInstanceTransform(count, new Transform3D(basis, at));
                _multi.SetInstanceColor(count, SpeedColor(speed));
                count++;
            }
        _multi.VisibleInstanceCount = count;
        _node.Position = origin;
    }

    /// <summary>Blue (slow) through yellow to red (<see cref="FullSpeed"/> and faster).</summary>
    public static Color SpeedColor(float speed)
    {
        float t = Mathf.Clamp(speed / FullSpeed, 0f, 1f);
        var slow = new Color(0.35f, 0.7f, 1f, 0.9f);
        var mid = new Color(1f, 0.9f, 0.25f, 0.9f);
        var fast = new Color(1f, 0.25f, 0.15f, 0.95f);
        return t < 0.5f ? slow.Lerp(mid, t * 2f) : mid.Lerp(fast, t * 2f - 1f);
    }

    /// <summary>A flat arrow one unit long along +Z (0 → 1) and one unit wide: a shaft and a head.</summary>
    private static ArrayMesh ArrowMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, Vector3 b, Vector3 c) { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); }
        const float shaft = 0.18f, head = 0.5f, neck = 0.6f;
        var (s0, s1, s2, s3) = (new Vector3(-shaft, 0, 0), new Vector3(shaft, 0, 0), new Vector3(-shaft, 0, neck), new Vector3(shaft, 0, neck));
        Tri(s0, s1, s2);
        Tri(s2, s1, s3);
        Tri(new Vector3(-head, 0, neck), new Vector3(head, 0, neck), new Vector3(0, 0, 1));
        return st.Commit();
    }
}
