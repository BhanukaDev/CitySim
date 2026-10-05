using System.Collections.Generic;
using System.Linq;
using CitySim.UI;
using CitySim.Roads;
using CitySim.Splines.Godot;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Zoning;

/// <summary>
/// Keeps the zoning grid in step with the roads (rebuilt after every network change, undo and redo) and draws it as a
/// debug overlay on the terrain: a faint fill per cell and its outline. Each road type's <c>ZoneRows</c> sets how deep
/// its sides go. Shown only while a tray that uses it is open (Roads; Zones from Z3), as in CS.
/// </summary>
public partial class ZoneGridView : Node3D
{
    [Export] public Terrain? Terrain { get; set; }
    [Export] public SplineNetwork? Network { get; set; }
    [Export] public GameHud? Hud { get; set; }

    private const float Lift = 0.25f;
    private const float LineWidth = 0.3f;
    private static readonly Color Fill = new(1f, 1f, 1f, 0.12f);
    private static readonly Color Line = new(0.11f, 0.15f, 0.16f, 0.75f);

    /// <summary>
    /// Unshaded vertex colour with alpha, pulled toward the camera far away (as the road shaders' far_bias) so the
    /// terrain's coarse distance LODs don't bury it on uneven ground. Same pixel, nearer depth.
    /// </summary>
    private const string GridShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;

        const float far_bias_start = 40.0;
        const float far_bias = 0.01;

        void vertex() {
            vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
            float d = length(view.xyz);
            view.xyz *= 1.0 - min(far_bias * max(d - far_bias_start, 0.0), d * 0.5) / max(d, 1e-3);
            POSITION = PROJECTION_MATRIX * view;
        }

        void fragment() {
            ALBEDO = COLOR.rgb;
            ALPHA = COLOR.a;
        }
        """;

    private readonly Dictionary<string, int> _rows = new();
    private MeshInstance3D _mesh = null!;

    public GridSettings Settings { get; set; } = new();
    public ZoneGrid Grid { get; private set; } = ZoneGrid.Build([]);
    public event System.Action<ZoneGrid>? Rebuilt;

    /// <summary>The categories whose tray shows the grid.</summary>
    private static readonly string[] ShownWith = ["roads", "zones"];

    /// <summary>Show the grid whatever is open (storyboard screenshots).</summary>
    public bool AlwaysShow
    {
        get => _alwaysShow;
        set { _alwaysShow = value; UpdateShown(); }
    }
    private bool _alwaysShow;

    public override void _Ready()
    {
        if (Terrain is null || Network is null || Hud is null) { GD.PushError("ZoneGridView needs a Terrain, a Network and a Hud"); return; }
        foreach (var road in Hud.Library.Items.OfType<RoadType>()) _rows[road.Id] = road.ZoneRows;
        _mesh = new MeshInstance3D
        {
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = GridShader } },
        };
        AddChild(_mesh);
        Network.Changed += Rebuild;
        // Fired before the tray switches, so go by the category it passes, not Hud.IsOpen.
        Hud.CategoryOpened += c => { _open = c?.Id; UpdateShown(); };
        Rebuild();
        UpdateShown();
    }

    private string? _open;

    private void UpdateShown() => Visible = _alwaysShow || ShownWith.Contains(_open);

    /// <summary>The most rows a road type's sides get (its <c>ZoneRows</c>; 0 for an unknown profile).</summary>
    public int RowsOf(string profileId) => _rows.GetValueOrDefault(profileId);

    public void Rebuild()
    {
        if (Network is null) return;
        var runs = RoadFrontage.Runs(Network.Graph, Network.Footprints, RowsOf);
        Grid = ZoneGrid.Build(runs, Settings);
        Draw();
        Rebuilt?.Invoke(Grid);
    }

    private void Draw()
    {
        if (Terrain is null || Network?.Ground is not { } ground) return;
        Vector3 W(NumVector2 p) => Terrain.MapToWorld(p.X, p.Y, ground.GetHeight(p) + Lift);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var cell in Grid.Cells)
        {
            var poly = cell.Polygon;
            var c = W(cell.Centre);
            for (int i = 0; i < poly.Count; i++)
            {
                st.SetColor(Fill); st.AddVertex(c);
                st.SetColor(Fill); st.AddVertex(W(poly[i]));
                st.SetColor(Fill); st.AddVertex(W(poly[(i + 1) % poly.Count]));
            }
            for (int i = 0; i < poly.Count; i++) Segment(st, poly[i], poly[(i + 1) % poly.Count], W);
        }
        _mesh.Mesh = Grid.Cells.Count > 0 ? st.Commit() : null;
    }

    /// <summary>A thin flat ribbon along a cell edge, split every couple of metres so it follows the ground.</summary>
    private static void Segment(SurfaceTool st, NumVector2 a, NumVector2 b, System.Func<NumVector2, Vector3> w)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return;
        var side = new NumVector2(-d.Y, d.X) / len * (LineWidth / 2);
        int n = System.Math.Max(1, (int)System.MathF.Ceiling(len / 2f));
        for (int k = 0; k < n; k++)
        {
            var p0 = NumVector2.Lerp(a, b, (float)k / n);
            var p1 = NumVector2.Lerp(a, b, (float)(k + 1) / n);
            Vector3 a0 = w(p0 - side), a1 = w(p0 + side), b0 = w(p1 - side), b1 = w(p1 + side);
            a0.Y = a1.Y = w(p0).Y + 0.02f;
            b0.Y = b1.Y = w(p1).Y + 0.02f;
            foreach (var v in new[] { a0, b0, b1, a0, b1, a1 }) { st.SetColor(Line); st.AddVertex(v); }
        }
    }
}
