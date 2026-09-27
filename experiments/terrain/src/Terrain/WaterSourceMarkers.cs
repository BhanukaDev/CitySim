using System.Collections.Generic;
using System.Linq;
using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// A ring and a post for each water source, coloured by kind, drawn through the ground so they're never lost behind a
/// hill. Shown while a water tool is out; the hovered source is brighter and the selected one white.
/// </summary>
public partial class WaterSourceMarkers : Node3D
{
    /// <summary>Radius of the sea marker (the sea itself covers the whole border).</summary>
    public const float SeaMarkerRadius = 80f;

    private Terrain? _terrain;
    private WaterSim? _sim;
    private readonly Dictionary<int, (MeshInstance3D Node, StandardMaterial3D Material, WaterSourceKind Kind)> _markers = new();
    private int? _hovered, _selected, _info;
    private Label3D? _label;

    public static Color KindColor(WaterSourceKind kind) => kind switch
    {
        WaterSourceKind.Stream => new Color(0.95f, 0.45f, 0.3f),
        WaterSourceKind.River => new Color(0.98f, 0.88f, 0.25f),
        WaterSourceKind.Lake => new Color(0.45f, 0.85f, 0.45f),
        _ => new Color(0.35f, 0.8f, 0.95f),
    };

    public void Init(Terrain terrain, WaterSim sim)
    {
        _terrain = terrain;
        _sim = sim;
        sim.SourcesChanged += () => { if (IsInstanceValid(this)) Rebuild(); };
        VisibilityChanged += () => { if (Visible) Rebuild(); };
        Rebuild();
    }

    public void SetHighlight(int? hovered, int? selected)
    {
        if (hovered == _hovered && selected == _selected) return;
        _hovered = hovered;
        _selected = selected;
        foreach (var (id, m) in _markers) Colour(id, m.Material, m.Kind);
    }

    /// <summary>Shows the info label (kind, level, depth, flow, pollution) over source <paramref name="id"/>; null hides it.</summary>
    public void ShowInfo(int? id)
    {
        _info = id;
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_label is null) AddChild(_label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0009f,
            FontSize = 30, OutlineSize = 10, RenderPriority = 30, OutlineRenderPriority = 29, Visible = false,
        });
        var s = _sim?.Sources.FirstOrDefault(x => x.Id == _info);
        if (s is null || _terrain is null)
        {
            _label.Visible = false;
            return;
        }
        float ground = _terrain.GetHeight(s.X, s.Z);
        float top = s.Kind == WaterSourceKind.Stream ? ground : Mathf.Max(ground, s.Level);
        var lines = new List<string> { WaterSource.Label(s.Kind) };
        switch (s.Kind)
        {
            case WaterSourceKind.Stream:
                lines.Add($"{s.FlowRate:0.#} m³/s · radius {s.Radius:0} m");
                if (s.Pollution > 0) lines.Add($"Pollution {s.Pollution:0.##} kg/s");
                break;
            case WaterSourceKind.River:
                lines.Add($"Level {s.Level:0.0} m ({s.Level - ground:0.0} m deep) · radius {s.Radius:0} m");
                break;
            case WaterSourceKind.Lake:
                lines.Add($"Level {s.Level:0.0} m ({s.Level - ground:0.0} m deep) · radius {s.Radius:0} m");
                lines.Add($"Max flow {s.MaxFlow:0.#} m³/s");
                break;
            default:
                lines.Add($"Sea level {s.Level:0.0} m");
                break;
        }
        float depth = _sim!.DepthAt(s.X, s.Z);
        if (depth > 0.01f) lines.Add($"Water here: {depth:0.0} m deep");
        _label.Text = string.Join("\n", lines);
        _label.Position = new Vector3(s.X, top + 16f, s.Z);
        _label.Visible = true;
    }

    /// <summary>Rebuilds every marker (sources changed, or the ground under them may have).</summary>
    public void Rebuild()
    {
        if (_sim is null || _terrain is null) return;
        UpdateLabel();
        foreach (var (_, m) in _markers) m.Node.QueueFree();
        _markers.Clear();
        foreach (var s in _sim.Sources)
        {
            float radius = s.Kind == WaterSourceKind.Sea ? SeaMarkerRadius : s.Radius;
            float ground = _terrain.GetHeight(s.X, s.Z);
            float top = s.Kind == WaterSourceKind.Stream ? ground : Mathf.Max(ground, s.Level);
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 10,
            };
            Colour(s.Id, material, s.Kind);
            var node = new MeshInstance3D
            {
                Mesh = MarkerMesh(radius, Mathf.Max(radius * 0.05f, 1.5f), Mathf.Max(top - ground, 0f) + 12f),
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(s.X, ground, s.Z),
            };
            AddChild(node);
            _markers[s.Id] = (node, material, s.Kind);
        }
    }

    private void Colour(int id, StandardMaterial3D m, WaterSourceKind kind)
    {
        var c = id == _selected ? Colors.White : KindColor(kind);
        c.A = id == _selected || id == _hovered ? 0.95f : 0.6f;
        m.AlbedoColor = c;
    }

    /// <summary>A flat ring at the post's top (where the source holds its level) and a thin post down to the ground.</summary>
    private static ArrayMesh MarkerMesh(float radius, float width, float height)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int segments = 64;
        float y = height - 12f + 0.5f;
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Tau * i / segments, a1 = Mathf.Tau * (i + 1) / segments;
            Vector3 P(float a, float r) => new(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            var (o0, o1, i0, i1) = (P(a0, radius), P(a1, radius), P(a0, radius - width), P(a1, radius - width));
            st.AddVertex(o0); st.AddVertex(o1); st.AddVertex(i0);
            st.AddVertex(i0); st.AddVertex(o1); st.AddVertex(i1);
        }
        // Post: two crossed quads from the ground to above the ring.
        float w = Mathf.Max(width * 0.4f, 0.6f);
        foreach (var dir in new[] { new Vector3(w, 0, 0), new Vector3(0, 0, w) })
        {
            Vector3 a = -dir, b = dir, up = new(0, height, 0);
            st.AddVertex(a); st.AddVertex(b); st.AddVertex(a + up);
            st.AddVertex(a + up); st.AddVertex(b); st.AddVertex(b + up);
        }
        return st.Commit();
    }
}
