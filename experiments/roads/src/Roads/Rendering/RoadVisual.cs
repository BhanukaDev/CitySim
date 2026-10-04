using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using Curve = CitySim.Splines.Curve;

namespace CitySim.Roads;

/// <summary>
/// The built road network as real road meshes (the splines addon's <see cref="INetworkVisual"/>): each edge extruded
/// from its <see cref="RoadSection"/> (crowned carriageway, gutters, kerb faces, raised sidewalks, a skirt into the
/// ground) with its painted lines, each junction footprint filled with asphalt inside a kerb and sidewalk that follow
/// its corners (<see cref="Junction"/>), and a halo under anything with an issue. Every vertex sits on the ground plus
/// its section height. Simple full rebuilds, like the addon's ribbons.
/// </summary>
public sealed partial class RoadVisual : INetworkVisual
{
    private const float Lift = 0.04f;       // the road above the terrain, so they don't z-fight
    private const float PaintLift = 0.012f; // a line above the surface it's painted on
    private const float Step = 2f;          // metres between cross-sections
    private const float CrownTaper = 4f;    // the crown fades out over this before a junction's flat asphalt

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private readonly RoadStyle _style;
    private readonly Func<string, RoadDef?> _defOf;
    private readonly SectionStyle _sectionStyle;
    private readonly Dictionary<string, RoadSection> _sections = new();
    private readonly Dictionary<SurfaceKind, Material> _fallbacks = new();
    private MeshInstance3D? _node;

    public RoadVisual(Node3D parent, IGround ground, RoadStyle style, Func<string, RoadDef?> defOf)
    {
        _parent = parent;
        _ground = ground;
        _style = style;
        _defOf = defOf;
        _sectionStyle = style.ToSection();
    }

    /// <summary>Triangles in the last build, per surface kind (for the demo's checks).</summary>
    public IReadOnlyDictionary<SurfaceKind, int> Counts { get; private set; } = new Dictionary<SurfaceKind, int>();

    public void SetNetwork(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IReadOnlyList<Issue> issues, IReadOnlySet<int>? hidden)
    {
        _node?.QueueFree();
        _node = null;
        var rm = new RoadMesh();
        foreach (var e in graph.Edges)
            if (hidden?.Contains(e.Id) != true) Segment(rm, graph, e, footprints);
        foreach (var f in footprints.Values)
            if (!f.Cuts.Any(c => hidden?.Contains(c.EdgeId) == true)) Junction(rm, graph, f);
        foreach (var n in graph.Nodes)
            if (!n.Edges.Any(e => hidden?.Contains(e) == true)) BendFill(rm, graph, n);

        var mesh = new ArrayMesh();
        rm.CommitTo(mesh, MaterialOf);
        Counts = Enum.GetValues<SurfaceKind>().ToDictionary(k => k, rm.Count);
        Halos(mesh, graph, issues, hidden);
        if (mesh.GetSurfaceCount() == 0) return;
        _node = new MeshInstance3D { Name = "Roads", Mesh = mesh };
        _parent.AddChild(_node);
    }

    /// <summary>The section an edge is drawn with: its road's, or a plain two-lane ribbon of its width for a profile that
    /// isn't a road.</summary>
    public RoadSection SectionOf(GraphEdge e)
    {
        if (_sections.TryGetValue(e.Rules.Id, out var s)) return s;
        var def = _defOf(e.Rules.Id) ?? new RoadDef { Id = e.Rules.Id, Lanes = 2, LaneWidth = e.Rules.Width / 2, Sidewalks = SidewalkLayout.None };
        return _sections[e.Rules.Id] = RoadSection.From(def, _sectionStyle);
    }

    private void Segment(RoadMesh rm, SplineGraph g, GraphEdge e, IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var sec = SectionOf(e);
        var curve = e.Alignment.Curve;
        var (cs, ce) = Junctions.CutBacks(e, footprints);
        float s0 = cs, s1 = curve.Length - ce;
        bool atStart = footprints.ContainsKey(e.Start), atEnd = footprints.ContainsKey(e.End);
        float Crown(float s) => MathF.Min(
            atStart ? Math.Clamp((s - s0) / CrownTaper, 0, 1) : 1,
            atEnd ? Math.Clamp((s1 - s) / CrownTaper, 0, 1) : 1);

        if (s1 - s0 > 1e-3f)
        {
            Vector3[]? prev = null;
            List<SectionPoint>? outline = null;
            foreach (float s in Stations(s0, s1))
            {
                outline = sec.Outline(Crown(s));
                var sample = curve.Sample(s);
                var left = SplineMath.Left(sample.Tangent);
                var ring = outline.Select(p => Point(sample.Position + left * p.Offset, p.Height)).ToArray();
                if (prev is not null)
                    for (int i = 0; i + 1 < outline.Count; i++)
                        if (ProfileNormal(outline[i], outline[i + 1], left) is { } n)
                            rm.Quad(outline[i].Surface, prev[i], prev[i + 1], ring[i + 1], ring[i], n);
                prev = ring;
            }
            // A dead end closes the road with an end face; at a junction only a raised median needs one.
            if (g.Node(e.Start).Edges.Count == 1) Cap(rm, sec, curve, s0, -1);
            else if (atStart) MedianEnds(rm, sec, curve, s0, -1);
            if (g.Node(e.End).Edges.Count == 1) Cap(rm, sec, curve, s1, +1);
            else if (atEnd) MedianEnds(rm, sec, curve, s1, +1);
        }

        foreach (var line in sec.Lines)
        {
            // The centre line runs on through a width transition to the node, into the next road.
            float a = line.Centre && Junctions.RunsOn(e, true, footprints) ? 0 : s0;
            float b = line.Centre && Junctions.RunsOn(e, false, footprints) ? curve.Length : s1;
            if (b - a <= 1e-3f) continue;
            if (line.Dash <= 0) { Paint(rm, sec, curve, a, b, line, Crown); continue; }
            float period = line.Dash + line.Gap;
            int n = Math.Max(1, (int)MathF.Round((b - a) / period));
            float p = (b - a) / n, on = p * line.Dash / period;
            // Stretched to whole dashes with half a gap at each end, so two pieces meeting end to end read as one line.
            for (int k = 0; k < n; k++)
            {
                float d = a + k * p + (p - on) / 2;
                Paint(rm, sec, curve, d, d + on, line, Crown);
            }
        }
    }

    /// <summary>A painted strip along the curve from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private void Paint(RoadMesh rm, RoadSection sec, Curve curve, float a, float b, SectionLine line, Func<float, float> crown)
    {
        Vector3? l0 = null, r0 = null;
        foreach (float s in Stations(a, b))
        {
            var sample = curve.Sample(s);
            var left = SplineMath.Left(sample.Tangent);
            float h = sec.CarriagewayHeight(line.Offset, crown(s)) + PaintLift;
            var l1 = Point(sample.Position + left * (line.Offset + line.Width / 2), h);
            var r1 = Point(sample.Position + left * (line.Offset - line.Width / 2), h);
            if (l0 is { } pl && r0 is { } pr) rm.Quad(SurfaceKind.Paint, pl, pr, r1, l1, Vector3.Up);
            (l0, r0) = (l1, r1);
        }
    }

    /// <summary>An end face across the road at <paramref name="s"/>, facing along (+1) or against (−1) the curve.</summary>
    private void Cap(RoadMesh rm, RoadSection sec, Curve curve, float s, int facing)
    {
        var outline = sec.Outline();
        var sample = curve.Sample(s);
        var left = SplineMath.Left(sample.Tangent);
        var normal = new Vector3(sample.Tangent.X, 0, sample.Tangent.Y) * facing;
        var poly = outline.Select(p => new Vector2(p.Offset, p.Height)).ToArray();
        var tris = Geometry2D.TriangulatePolygon(poly);
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            Vector3 P(int k) => Point(sample.Position + left * outline[tris[k]].Offset, outline[tris[k]].Height);
            rm.Tri(SurfaceKind.Kerb, P(i), P(i + 1), P(i + 2), normal);
        }
    }

    /// <summary>End faces for the raised bands inside the carriageway (a median) where the road meets a junction.</summary>
    private void MedianEnds(RoadMesh rm, RoadSection sec, Curve curve, float s, int facing)
    {
        var sample = curve.Sample(s);
        var left = SplineMath.Left(sample.Tangent);
        var normal = new Vector3(sample.Tangent.X, 0, sample.Tangent.Y) * facing;
        float kh = sec.Style.KerbHeight;
        foreach (var b in sec.Bands.Where(b => b.Raised && MathF.Max(MathF.Abs(b.Left), MathF.Abs(b.Right)) <= sec.HalfCarriageway))
        {
            var (l, r) = (sample.Position + left * b.Left, sample.Position + left * b.Right);
            rm.Quad(SurfaceKind.Kerb, Point(l, 0), Point(r, 0), Point(r, kh), Point(l, kh), normal);
        }
    }

    /// <summary>Where a cross-section span from <paramref name="a"/> to <paramref name="b"/> faces: up for the tops, toward
    /// the road for a kerb face, outward for a skirt. Null for a span of no length.</summary>
    private static Vector3? ProfileNormal(SectionPoint a, SectionPoint b, NumVector2 left)
    {
        float dOff = b.Offset - a.Offset, dh = b.Height - a.Height;
        var left3 = new Vector3(left.X, 0, left.Y);
        var n = left3 * dh - Vector3.Up * dOff;
        return n.LengthSquared() < 1e-10f ? null : n.Normalized();
    }

    /// <summary>Stations from <paramref name="a"/> to <paramref name="b"/>, both included, at most <see cref="Step"/> apart.</summary>
    private static IEnumerable<float> Stations(float a, float b)
    {
        int n = Math.Max(1, (int)MathF.Ceiling((b - a) / Step));
        for (int k = 0; k <= n; k++) yield return a + (b - a) * k / n;
    }

    /// <summary>A plan point at a height above the ground.</summary>
    private Vector3 Point(NumVector2 plan, float height) => new(plan.X, _ground.GetHeight(plan) + Lift + height, plan.Y);

    private Vector3 Point(Vector2 plan, float height) => Point(new NumVector2(plan.X, plan.Y), height);

    private Material MaterialOf(SurfaceKind kind)
    {
        if (_style.MaterialOf(kind) is { } m) return m;
        if (_fallbacks.TryGetValue(kind, out var f)) return f;
        return _fallbacks[kind] = new StandardMaterial3D { AlbedoColor = kind == SurfaceKind.Paint ? Colors.White : new Color(0.4f, 0.4f, 0.4f) };
    }

    /// <summary>A translucent halo under every edge and junction with an issue, in the addon's warning colours.</summary>
    private void Halos(ArrayMesh mesh, SplineGraph graph, IReadOnlyList<Issue> issues, IReadOnlySet<int>? hidden)
    {
        var edgeWorst = new Dictionary<int, Severity>();
        var nodeWorst = new Dictionary<int, Severity>();
        foreach (var i in issues)
        {
            if (i.NodeId is { } n) nodeWorst[n] = Worse(nodeWorst, n, i.Severity);
            else if (i.EdgeId is { } e) edgeWorst[e] = Worse(edgeWorst, e, i.Severity);
        }
        foreach (Severity sev in new[] { Severity.Warn, Severity.Invalid })
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            int tris = 0;
            foreach (var e in graph.Edges)
            {
                if (!edgeWorst.TryGetValue(e.Id, out var w) || w != sev || hidden?.Contains(e.Id) == true) continue;
                float half = e.Rules.Width / 2 + 2.5f;
                Vector3? l0 = null, r0 = null;
                foreach (float s in Stations(0, e.Alignment.Length))
                {
                    var sample = e.Alignment.Curve.Sample(s);
                    var left = SplineMath.Left(sample.Tangent) * half;
                    var l1 = Point(sample.Position + left, -Lift / 2);
                    var r1 = Point(sample.Position - left, -Lift / 2);
                    if (l0 is { } a && r0 is { } b)
                    {
                        st.AddVertex(a); st.AddVertex(b); st.AddVertex(l1);
                        st.AddVertex(l1); st.AddVertex(b); st.AddVertex(r1);
                        tris += 2;
                    }
                    (l0, r0) = (l1, r1);
                }
            }
            foreach (var n in graph.Nodes)
            {
                if (!nodeWorst.TryGetValue(n.Id, out var w) || w != sev) continue;
                float r = n.Edges.Select(id => graph.Edge(id).Rules.Width).DefaultIfEmpty(0).Max() / 2 + 6f;
                const int k = 24;
                for (int i = 0; i < k; i++)
                {
                    st.AddVertex(Point(n.Position, -Lift / 2));
                    st.AddVertex(Point(n.Position + SplineMath.Direction(MathF.Tau * i / k) * r, -Lift / 2));
                    st.AddVertex(Point(n.Position + SplineMath.Direction(MathF.Tau * (i + 1) / k) * r, -Lift / 2));
                    tris++;
                }
            }
            if (tris == 0) continue;
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = (sev == Severity.Invalid ? SplineOverlay.Bad : SplineOverlay.Warn) with { A = 0.55f },
            });
        }

        static Severity Worse(Dictionary<int, Severity> d, int key, Severity s) => d.TryGetValue(key, out var old) && old > s ? old : s;
    }
}
