using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Roads;

/// <summary>
/// The built road network as real road meshes (the splines addon's <see cref="INetworkVisual"/>): each edge extruded
/// from its <see cref="RoadSection"/> (crowned carriageway, gutters, kerb faces, raised sidewalks, a skirt into the
/// ground) with its painted lines, each junction footprint filled with asphalt inside a kerb and sidewalk that follow
/// its corners (<see cref="Junction"/>), and a halo under anything with an issue. Every vertex sits on the edge's height
/// line at its station (<see cref="GraphEdge.Heights"/>, level across, so the road never rolls with the ground) or a
/// junction's node height, plus its section height. Simple full rebuilds, like the addon's ribbons.
/// </summary>
public sealed partial class RoadVisual : INetworkVisual
{
    // The road above the ground shaped under it: clear of z-fighting and of the terrain renderer's smoothing, which
    // rounds a level junction into the slope beyond it a few centimetres high. The skirts hide the gap.
    private const float Lift = 0.12f;
    private const float PaintLift = 0.012f; // a line above the surface it's painted on
    private const float Step = 2f;          // metres between cross-sections
    private const float CrownTaper = 4f;    // the crown fades out over this before a junction's flat asphalt
    // How worn every road is (UV2.y) until traffic is simulated; the shaders vary it by place on their own.
    private const float DefaultWear = 1f;

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private readonly RoadStyle _style;
    private readonly Func<string, RoadDef?> _defOf;
    private readonly SectionStyle _sectionStyle;
    private readonly Dictionary<string, RoadSection> _sections = new();
    private readonly Dictionary<SurfaceKind, Material> _fallbacks = new();
    private MeshInstance3D? _node;
    /// <summary>The height <see cref="Point"/> builds on: the height line at the station being drawn, or the junction's
    /// node. NaN drapes on the ground (an edge or node with no height yet).</summary>
    private float _level = float.NaN;

    public RoadVisual(Node3D parent, IGround ground, RoadStyle style, Func<string, RoadDef?> defOf)
    {
        _parent = parent;
        _ground = ground;
        _style = style;
        _defOf = defOf;
        _sectionStyle = style.ToSection();
    }

    /// <summary>How old a road is, 0 (new, no cracks) to 1 (falling apart): set by the game from the road's age and
    /// damage (disasters, neglect), never by the player. A junction takes its oldest arm. Read on every rebuild.</summary>
    public Func<GraphEdge, float> AgeOf { get; set; } = _ => 0f;

    /// <summary>Triangles in the last build, per surface kind (for the demo's checks).</summary>
    public IReadOnlyDictionary<SurfaceKind, int> Counts { get; private set; } = new Dictionary<SurfaceKind, int>();

    /// <summary>The crossings and stop lines of the last build, per edge end (<see cref="Crossings.Resolve"/>).</summary>
    public IReadOnlyDictionary<(int Edge, bool AtStart), EndMarks> Marks { get; private set; } = new Dictionary<(int, bool), EndMarks>();

    public SectionStyle SectionStyle => _sectionStyle;

    public void SetNetwork(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IReadOnlyList<Issue> issues, IReadOnlySet<int>? hidden)
    {
        _node?.QueueFree();
        _node = null;
        var rm = new RoadMesh { Wear = DefaultWear };
        Hatches = 0;
        Marks = Crossings.Resolve(graph, footprints, SectionOf, _sectionStyle);
        foreach (var e in graph.Edges)
            if (hidden?.Contains(e.Id) != true)
            {
                rm.Age = Age(e);
                Segment(rm, graph, e, footprints);
            }
        foreach (var f in footprints.Values)
            if (!f.Cuts.Any(c => hidden?.Contains(c.EdgeId) == true))
            {
                rm.Age = f.Cuts.Max(c => Age(graph.Edge(c.EdgeId)));
                Junction(rm, graph, f);
            }
        foreach (var n in graph.Nodes)
            if (!n.Edges.Any(e => hidden?.Contains(e) == true))
            {
                rm.Age = n.Edges.Select(id => Age(graph.Edge(id))).DefaultIfEmpty(0).Max();
                BendFill(rm, graph, n);
            }

        var mesh = new ArrayMesh();
        rm.CommitTo(mesh, MaterialOf);
        Counts = Enum.GetValues<SurfaceKind>().ToDictionary(k => k, rm.Count);
        Halos(mesh, graph, issues, hidden);
        if (mesh.GetSurfaceCount() == 0) return;
        _node = new MeshInstance3D { Name = "Roads", Mesh = mesh };
        _parent.AddChild(_node);
    }

    private float Age(GraphEdge e) => Math.Clamp(AgeOf(e), 0, 1);

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
        // Junction mouths on a paved road get a stop line and solid approach lines (European), and a crossing where
        // Crossings.Resolve puts one; a crossing anywhere else gets stop lines on both sides. The lanes coming into a
        // junction are stained where cars queue for the stop line.
        var st = _sectionStyle;
        var markA = Marks.TryGetValue((e.Id, true), out var ma) ? ma : (EndMarks?)null;
        var markB = Marks.TryGetValue((e.Id, false), out var mb) ? mb : (EndMarks?)null;
        float uA = markA?.LinesFrom(st) ?? 0, uB = markB?.LinesFrom(st) ?? 0;
        bool queueA = markA?.Junction == true, queueB = markB?.Junction == true;
        float split = RoadMesh.SplitCode(sec.Split);
        Vector3 QueueAt(float s) => new(queueB ? Queue(s1 - s - uB) : 0, queueA ? Queue(s - s0 - uA) : 0, split);

        if (s1 - s0 > 1e-3f)
        {
            Vector3[]? prev = null;
            float prevS = 0;
            Vector3 prevQ = default;
            foreach (float s in Stations(s0, s1))
            {
                var q = QueueAt(s);
                var outline = sec.Outline(Crown(s));
                var sample = curve.Sample(s);
                _level = LevelAt(e, s);
                var left = SplineMath.Left(sample.Tangent);
                var ring = outline.Select(p => Point(sample.Position + left * p.Offset, p.Height)).ToArray();
                if (prev is not null)
                    for (int i = 0; i + 1 < outline.Count; i++)
                        if (ProfileNormal(outline[i], outline[i + 1], left) is { } n)
                        {
                            // Both ends of a span take its lane data, so a strip next to a lane stays unworn.
                            var (pa, pb, lanes) = (outline[i], outline[i + 1], outline[i].Lanes);
                            rm.Quad(pa.Surface, V(prev[i], prevS, pa.Offset, prevQ), V(prev[i + 1], prevS, pb.Offset, prevQ),
                                V(ring[i + 1], s, pb.Offset, q), V(ring[i], s, pa.Offset, q), n);
                            RoadMesh.Vertex V(Vector3 pos, float along, float offset, Vector3 queue) => new(pos, new Vector2(along, offset),
                                new Vector2(lanes ? sec.LaneCoord(offset) : RoadMesh.NoLane, rm.Wear), queue);
                        }
                prev = ring;
                prevS = s;
                prevQ = q;
            }
            // A dead end closes the road with an end face; at a junction only a raised median needs one.
            if (g.Node(e.Start).Edges.Count == 1) Cap(rm, sec, e, s0, -1);
            else if (atStart) MedianEnds(rm, sec, e, s0, -1);
            if (g.Node(e.End).Edges.Count == 1) Cap(rm, sec, e, s1, +1);
            else if (atEnd) MedianEnds(rm, sec, e, s1, +1);
        }

        foreach (var line in sec.Lines)
        {
            // The centre line runs on through a width transition to the node, into the next road.
            float a = line.Centre && Junctions.RunsOn(e, true, footprints) ? 0 : s0;
            float b = line.Centre && Junctions.RunsOn(e, false, footprints) ? curve.Length : s1;
            // At a junction every line stops at the stop line; the centre line and lines between lanes coming in are
            // solid on the approach (no overtaking or lane changes just before a junction).
            float solidA = 0, solidB = 0;
            if (markA is not null)
            {
                a = s0 + uA;
                if (Approach(sec, line, forward: false)) solidA = st.SolidApproach;
            }
            if (markB is not null)
            {
                b = s1 - uB;
                if (Approach(sec, line, forward: true)) solidB = st.SolidApproach;
            }
            if (b - a <= 1e-3f) continue;
            if (line.Dash <= 0 || solidA + solidB >= b - a) { Paint(rm, sec, e, a, b, line, Crown); continue; }
            if (solidA > 0) Paint(rm, sec, e, a, a + solidA, line, Crown);
            if (solidB > 0) Paint(rm, sec, e, b - solidB, b, line, Crown);
            Dashes(rm, sec, e, a + solidA, b - solidB, line, Crown);
        }

        if (markA is { } mA && s1 - s0 > uA) Mouth(rm, sec, e, s0, +1, forward: false, mA, Crown);
        if (markB is { } mB && s1 - s0 > uB) Mouth(rm, sec, e, s1, -1, forward: true, mB, Crown);
    }

    /// <summary>How much cars stand at a distance past the stop line: most right behind it, fading over a queue's length.</summary>
    private static float Queue(float u) => u < 0 ? 0 : Math.Clamp(u / 1.5f, 0, 1) * (1 - SmoothStep(0, 45, u));

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Whether a line is solid on the approach to a junction that the <paramref name="forward"/> lanes run into:
    /// the centre line, or a line between two lanes both coming in.</summary>
    private static bool Approach(RoadSection sec, SectionLine line, bool forward)
    {
        if (line.Centre) return true;
        if (line.Dash <= 0) return false;
        float h = sec.LaneWidth / 2 + 0.05f;
        var near = sec.Lanes.Where(l => MathF.Abs(l.Offset - line.Offset) < h).ToList();
        return near.Count == 2 && near.All(l => l.Forward == forward);
    }

    /// <summary>Dashes from <paramref name="a"/> to <paramref name="b"/>, stretched to whole dashes with half a gap at
    /// each end, so two pieces meeting end to end read as one line.</summary>
    private void Dashes(RoadMesh rm, RoadSection sec, GraphEdge e, float a, float b, SectionLine line, Func<float, float> crown)
    {
        if (b - a <= 1e-3f) return;
        float period = line.Dash + line.Gap;
        int n = Math.Max(1, (int)MathF.Round((b - a) / period));
        float p = (b - a) / n, on = p * line.Dash / period;
        for (int k = 0; k < n; k++)
        {
            float d = a + k * p + (p - on) / 2;
            Paint(rm, sec, e, d, d + on, line, crown);
        }
    }

    /// <summary>
    /// The markings at one end of a road, from <paramref name="cut"/> (<paramref name="inward"/> +1 = the road runs on
    /// along the curve from there), as <paramref name="marks"/> places them: a zebra crossing between the sidewalks,
    /// bars along the road across the whole carriageway, and a stop line across the lanes coming in (those running
    /// <paramref name="forward"/>).
    /// </summary>
    private void Mouth(RoadMesh rm, RoadSection sec, GraphEdge e, float cut, int inward, bool forward, EndMarks marks, Func<float, float> crown)
    {
        var st = _sectionStyle;
        float At(float u) => cut + inward * u;
        (float, float) Span(float u0, float u1) => inward > 0 ? (At(u0), At(u1)) : (At(u1), At(u0));
        if (marks.Zebra)
        {
            float reach = sec.HalfCarriageway - sec.GutterWidth - 0.2f, pitch = st.CrossingBar + st.CrossingGap;
            int bars = (int)((2 * reach + st.CrossingGap) / pitch);
            float first = (bars * pitch - st.CrossingGap) / 2 - st.CrossingBar / 2;
            var (a, b) = Span(marks.ZebraFrom, marks.ZebraFrom + st.CrossingWidth);
            for (int k = 0; k < bars; k++)
                Paint(rm, sec, e, a, b, new SectionLine(first - k * pitch, st.CrossingBar, 0, 0), crown);
        }
        if (marks.StopAt is not { } uStop) return;
        var lanes = sec.Lanes.Where(l => l.Forward == forward).ToList();
        if (lanes.Count == 0) return;
        float hw = sec.LaneWidth / 2;
        float left = lanes.Max(l => l.Offset) + hw, right = lanes.Min(l => l.Offset) - hw;
        // From the centre line's middle to the edge line (or the kerb's gutter with none).
        bool lineLeft = sec.Lines.Any(l => MathF.Abs(l.Offset - left) < 0.2f), lineRight = sec.Lines.Any(l => MathF.Abs(l.Offset - right) < 0.2f);
        if (!lineLeft) left = MathF.Min(left, sec.HalfCarriageway - sec.GutterWidth);
        if (!lineRight) right = MathF.Max(right, -(sec.HalfCarriageway - sec.GutterWidth));
        var (s0, s1) = Span(uStop, uStop + st.StopLineWidth);
        Paint(rm, sec, e, s0, s1, new SectionLine((left + right) / 2, left - right, 0, 0), crown);
    }

    /// <summary>A painted strip along the curve from <paramref name="a"/> to <paramref name="b"/>. Its UV is its half
    /// width and metres across it from its middle; UV2 is metres from the strip's start and to its end, so the paint
    /// shader can fray the sides and the ends.</summary>
    private void Paint(RoadMesh rm, RoadSection sec, GraphEdge e, float a, float b, SectionLine line, Func<float, float> crown)
    {
        float half = line.Width / 2;
        // Columns across the strip: its sides, and the crown's ridge if it crosses it, so a wide line follows the crown.
        var across = new List<float> { half };
        if (line.Offset + half > 0 && line.Offset - half < 0) across.Add(-line.Offset);
        across.Add(-half);
        RoadMesh.Vertex[]? prev = null;
        foreach (float s in Stations(a, b))
        {
            var sample = e.Alignment.Curve.Sample(s);
            _level = LevelAt(e, s);
            var left = SplineMath.Left(sample.Tangent);
            var ends = new Vector2(s - a, b - s);
            var row = across.Select(x => new RoadMesh.Vertex(
                Point(sample.Position + left * (line.Offset + x), sec.CarriagewayHeight(line.Offset + x, crown(s)) + PaintLift),
                new Vector2(half, x), ends)).ToArray();
            if (prev is not null)
                for (int i = 0; i + 1 < row.Length; i++) rm.Quad(SurfaceKind.Paint, prev[i], prev[i + 1], row[i + 1], row[i], Vector3.Up);
            prev = row;
        }
    }

    /// <summary>An end face across the road at <paramref name="s"/>, facing along (+1) or against (−1) the curve.</summary>
    private void Cap(RoadMesh rm, RoadSection sec, GraphEdge e, float s, int facing)
    {
        var outline = sec.Outline();
        var sample = e.Alignment.Curve.Sample(s);
        _level = LevelAt(e, s);
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
    private void MedianEnds(RoadMesh rm, RoadSection sec, GraphEdge e, float s, int facing)
    {
        var sample = e.Alignment.Curve.Sample(s);
        _level = LevelAt(e, s);
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

    /// <summary>A plan point at a height above <see cref="_level"/> (or the ground, with no level).</summary>
    private Vector3 Point(NumVector2 plan, float height) =>
        new(plan.X, (float.IsNaN(_level) ? _ground.GetHeight(plan) : _level) + Lift + height, plan.Y);

    private static float LevelAt(GraphEdge e, float s) => e.Heights?.At(s) ?? float.NaN;

    private static float LevelOf(CitySim.Splines.GraphNode n) => n.Height ?? float.NaN;

    private Vector3 Point(Vector2 plan, float height) => Point(new NumVector2(plan.X, plan.Y), height);

    private Material? MaterialOf(SurfaceKind kind)
    {
        if (_style.MaterialOf(kind) is { } m) return m;
        if (kind == SurfaceKind.Wear) return null;
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
                    _level = LevelAt(e, s);
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
                _level = LevelOf(n);
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
