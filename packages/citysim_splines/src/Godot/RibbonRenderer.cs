using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Turns <see cref="RibbonGeometry"/> slices into flat-ribbon meshes draped on the ground (DESIGN.md's placeholder
/// flat-ribbon visual): the preview is a translucent light-blue "ghost" (its outline and dashed legs are drawn by
/// <see cref="SplineOverlay"/>) with an amber or red halo under it when it has a warning or is refused; a turnout
/// suggestion is a fainter ghost. The built network is each edge in its profile colour with a lighter dashed centre
/// line, cut back at junctions, each Node junction's footprint filled flat in the widest arm's colour, and a halo under
/// every edge or junction with an issue. Simple full rebuilds — no incremental updates until S11.
/// </summary>
public sealed class RibbonRenderer
{
    private const float Lift = 0.05f; // avoids z-fighting with the terrain
    /// <summary>Footprints sit just above the ribbons, so where a squeezed junction can't cut an arm back far enough the
    /// footprint still covers its end instead of z-fighting with it.</summary>
    private const float FootprintLift = Lift * 1.5f;
    private const float CentreWidth = 0.5f;
    private const float DashOn = 3f, DashOff = 3f;

    private static readonly Color GhostFill = new(0.42f, 0.72f, 1f, 0.45f);
    private static readonly Color SuggestFill = new(0.42f, 0.72f, 1f, 0.22f);

    private static Color HaloOf(Severity s) => s == Severity.Invalid ? SplineOverlay.Bad with { A = 0.6f } : SplineOverlay.Warn with { A = 0.45f };

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private MeshInstance3D? _preview, _ghost, _network;

    public RibbonRenderer(Node3D parent, IGround ground)
    {
        _parent = parent;
        _ground = ground;
    }

    /// <summary>Rebuilds the preview ghost from the in-progress alignment, with a halo in the worst issue's colour
    /// (a clamped corner is amber even before validation says so). Stations before <paramref name="solidUntil"/> and
    /// after <paramref name="solidFrom"/> are road already built that the draw continues unchanged: they're drawn as
    /// built, in <paramref name="solidColor"/>, and only the rest is the ghost. <paramref name="kept"/> are other
    /// profiles' roads the draw shortens to its joint, drawn as built too. Null clears it.</summary>
    public void SetPreview(Alignment? alignment, float width, Severity? worst = null,
        float solidUntil = 0, float solidFrom = float.PositiveInfinity, Color? solidColor = null,
        IReadOnlyList<(Alignment Alignment, float Width, Color Color)>? kept = null)
    {
        if (alignment is null || alignment.Curve.Length <= 0f)
        {
            Clear(ref _preview);
            return;
        }
        _preview ??= NewInstance();
        var mesh = new ArrayMesh();
        var curve = alignment.Curve;
        float s0 = Math.Clamp(solidUntil, 0, curve.Length), s1 = Math.Clamp(solidFrom, s0, curve.Length);
        if (s1 > s0)
        {
            if (worst is { } w)
            {
                var halo = NewStrip();
                if (Span(halo, curve, s0, s1, width + 5f, Lift * 0.5f) > 0) AddSurface(mesh, halo, HaloOf(w));
            }
            var ghost = NewStrip();
            if (Span(ghost, curve, s0, s1, width, Lift) > 0) AddSurface(mesh, ghost, GhostFill);
        }
        if (solidColor is { } color && (s0 > 0 || s1 < curve.Length))
        {
            var fill = NewStrip();
            var centre = NewStrip();
            int quads = 0, dashes = 0;
            foreach (var (a, b) in new[] { (0f, s0), (s1, curve.Length) })
            {
                if (b - a < 1e-3f) continue;
                quads += Span(fill, curve, a, b, width, Lift);
                dashes += Dashes(centre, curve, a, b);
            }
            if (quads > 0) AddSurface(mesh, fill, color, opaque: true);
            if (dashes > 0) AddSurface(mesh, centre, color.Lightened(0.55f), opaque: true);
        }
        foreach (var (a, w, keptColor) in kept ?? Array.Empty<(Alignment, float, Color)>())
        {
            var fill = NewStrip();
            var centre = NewStrip();
            if (Span(fill, a.Curve, 0, a.Length, w, Lift) > 0) AddSurface(mesh, fill, keptColor, opaque: true);
            if (Dashes(centre, a.Curve, 0, a.Length) > 0) AddSurface(mesh, centre, keptColor.Lightened(0.55f), opaque: true);
        }
        _preview.Mesh = mesh;
    }

    /// <summary>A suggested alignment (the legal turnout offered for a refused branch), fainter than the preview.</summary>
    public void SetGhost(Alignment? alignment, float width)
    {
        if (alignment is null || alignment.Curve.Length <= 0f)
        {
            Clear(ref _ghost);
            return;
        }
        _ghost ??= NewInstance();
        var mesh = new ArrayMesh();
        AddSurface(mesh, Strip(alignment.Curve, width, 0, Lift * 1.5f), SuggestFill);
        _ghost.Mesh = mesh;
    }

    /// <summary>Rebuilds every built edge, junction footprint and bend fill (one mesh, a surface per colour and
    /// layer). <paramref name="hidden"/> edges are left out (the ones a draw in progress is continuing).</summary>
    public void SetNetwork(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IReadOnlyList<Issue> issues, Func<string, Color> colorOf, IReadOnlySet<int>? hidden = null)
    {
        Clear(ref _network);
        if (graph.EdgeCount == 0) return;
        _network = NewInstance();
        var mesh = new ArrayMesh();

        var edgeWorst = new Dictionary<int, Severity>();
        var nodeWorst = new Dictionary<int, Severity>();
        foreach (var i in issues)
        {
            if (i.NodeId is { } n) nodeWorst[n] = Max(nodeWorst, n, i.Severity);
            else if (i.EdgeId is { } e) edgeWorst[e] = Max(edgeWorst, e, i.Severity);
        }

        foreach (Severity sev in new[] { Severity.Warn, Severity.Invalid })
        {
            var halo = NewStrip();
            int quads = 0;
            foreach (var e in graph.Edges)
                if (edgeWorst.TryGetValue(e.Id, out var w) && w == sev && hidden?.Contains(e.Id) != true)
                    quads += Span(halo, e.Alignment.Curve, 0, e.Alignment.Length, e.Rules.Width + 5f, Lift * 0.5f);
            foreach (var n in graph.Nodes)
                if (nodeWorst.TryGetValue(n.Id, out var w) && w == sev)
                    quads += Disc(halo, n.Position, MaxWidth(graph, n.Id) * 0.5f + 6f, Lift * 0.5f);
            if (quads > 0) AddSurface(mesh, halo, HaloOf(sev), opaque: false);
        }

        foreach (var group in graph.Edges.Where(e => hidden?.Contains(e.Id) != true).GroupBy(e => e.Rules.Id))
        {
            var fill = NewStrip();
            var centre = NewStrip();
            int quads = 0, dashes = 0;
            foreach (var e in group)
            {
                var (cs, ce) = Junctions.CutBacks(e, footprints);
                float s0 = cs, s1 = e.Alignment.Length - ce;
                if (s1 <= s0) continue;
                quads += Span(fill, e.Alignment.Curve, s0, s1, e.Rules.Width, Lift);
                // The centre line runs on through a width transition to the node, so it carries on into the next road.
                float d0 = Junctions.RunsOn(e, true, footprints) ? 0 : s0;
                float d1 = Junctions.RunsOn(e, false, footprints) ? e.Alignment.Length : s1;
                dashes += Dashes(centre, e.Alignment.Curve, d0, d1);
            }
            var color = colorOf(group.Key);
            if (quads > 0) AddSurface(mesh, fill, color, opaque: true);
            if (dashes > 0) AddSurface(mesh, centre, color.Lightened(0.55f), opaque: true);
        }

        foreach (var f in footprints.Values)
        {
            var st = NewStrip();
            var poly = f.Outline.Select(p => new Vector2(p.X, p.Y)).ToArray();
            var tris = Geometry2D.TriangulatePolygon(poly);
            if (tris.Length > 0)
                foreach (int i in tris) st.AddVertex(Drape(f.Outline[i], FootprintLift));
            else // not a simple polygon (arms overlapping under Anarchy): a fan still covers most of it
                for (int i = 0; i < f.Outline.Count; i++)
                {
                    st.AddVertex(Drape(f.Centre, FootprintLift));
                    st.AddVertex(Drape(f.Outline[i], FootprintLift));
                    st.AddVertex(Drape(f.Outline[(i + 1) % f.Outline.Count], FootprintLift));
                }
            var widest = f.Cuts.Select(c => graph.Edge(c.EdgeId)).OrderByDescending(e => e.Rules.Width).First();
            AddSurface(mesh, st, colorOf(widest.Rules.Id), opaque: true);
        }

        foreach (var n in graph.Nodes)
        {
            if (n.Edges.Any(e => hidden?.Contains(e) == true) || Junctions.BendFill(graph, n.Id) is not { } bend) continue;
            var st = NewStrip();
            for (int i = 0; i + 1 < bend.Count; i++)
            {
                st.AddVertex(Drape(n.Position, Lift));
                st.AddVertex(Drape(bend[i], Lift));
                st.AddVertex(Drape(bend[i + 1], Lift));
            }
            var widest = n.Edges.Select(graph.Edge).OrderByDescending(e => e.Rules.Width).First();
            AddSurface(mesh, st, colorOf(widest.Rules.Id), opaque: true);
        }
        _network.Mesh = mesh;

        static Severity Max(Dictionary<int, Severity> d, int key, Severity s) => d.TryGetValue(key, out var old) && old > s ? old : s;
    }

    private static float MaxWidth(SplineGraph g, int node) => g.Node(node).Edges.Select(e => g.Edge(e).Rules.Width).DefaultIfEmpty(0).Max();

    private static void Clear(ref MeshInstance3D? node)
    {
        node?.QueueFree();
        node = null;
    }

    private static SurfaceTool NewStrip()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        return st;
    }

    /// <summary>A flat disc of triangles around a point.</summary>
    private int Disc(SurfaceTool st, NumVector2 centre, float radius, float lift)
    {
        const int n = 24;
        for (int i = 0; i < n; i++)
        {
            st.AddVertex(Drape(centre, lift));
            st.AddVertex(Drape(centre + SplineMath.Direction(MathF.Tau * i / n) * radius, lift));
            st.AddVertex(Drape(centre + SplineMath.Direction(MathF.Tau * (i + 1) / n) * radius, lift));
        }
        return n;
    }

    private MeshInstance3D NewInstance()
    {
        var node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _parent.AddChild(node);
        return node;
    }

    private static void AddSurface(ArrayMesh mesh, SurfaceTool? st, Color color, bool opaque = false)
    {
        if (st is null) return;
        st.Commit(mesh);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = opaque ? BaseMaterial3D.TransparencyEnum.Disabled : BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = color,
        });
    }

    /// <summary>A ribbon <paramref name="width"/> wide along the curve; dashed (<paramref name="dash"/> on, the same
    /// off) when <paramref name="dash"/> &gt; 0. Null when there's nothing to draw.</summary>
    private SurfaceTool? Strip(Curve curve, float width, float dash, float lift)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = 0;
        if (dash <= 0) quads += Span(st, curve, 0, curve.Length, width, lift);
        else
            for (float s = DashOff / 2; s < curve.Length; s += DashOn + DashOff)
                quads += Span(st, curve, s, MathF.Min(s + DashOn, curve.Length), width, lift);
        return quads > 0 ? st : null;
    }

    /// <summary>A dashed centre line from <paramref name="s0"/> to <paramref name="s1"/>, the pattern stretched to a
    /// whole number of dashes with half a gap at each end, so two pieces meeting end to end read as one line.</summary>
    private int Dashes(SurfaceTool st, Curve curve, float s0, float s1)
    {
        const float period = DashOn + DashOff;
        float len = s1 - s0;
        if (len <= 0) return 0;
        int n = Math.Max(1, (int)MathF.Round(len / period));
        float p = len / n, on = p * DashOn / period;
        int quads = 0;
        for (int k = 0; k < n; k++)
        {
            float a = s0 + k * p + (p - on) / 2;
            quads += Span(st, curve, a, a + on, CentreWidth, Lift * 2);
        }
        return quads;
    }

    private int Span(SurfaceTool st, Curve curve, float s0, float s1, float width, float lift)
    {
        float half = width / 2f;
        int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 2f));
        Vector3? l0 = null, r0 = null;
        for (int k = 0; k <= n; k++)
        {
            var sample = curve.Sample(s0 + (s1 - s0) * k / n);
            var left = SplineMath.Left(sample.Tangent) * half;
            var l1 = Drape(sample.Position + left, lift);
            var r1 = Drape(sample.Position - left, lift);
            if (l0 is { } a && r0 is { } b)
            {
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(l1);
                st.AddVertex(l1); st.AddVertex(b); st.AddVertex(r1);
            }
            l0 = l1;
            r0 = r1;
        }
        return n;
    }

    private Vector3 Drape(NumVector2 plan, float lift) => new(plan.X, _ground.GetHeight(plan) + lift, plan.Y);
}
