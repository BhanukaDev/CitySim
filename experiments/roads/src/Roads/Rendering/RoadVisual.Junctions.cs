using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using Godot;
using GraphNode = CitySim.Splines.GraphNode;

namespace CitySim.Roads;

public sealed partial class RoadVisual
{
    /// <summary>
    /// A junction footprint (its outline is the back of the sidewalk all round, with a straight cut across each arm's
    /// mouth). The kerb line is that outline inset by the sidewalk width, with each mouth kept open: the outline is first
    /// extended past every cut, then inset, then clipped back to the outline. Inside the kerb line is flat asphalt; between
    /// it and the outline a kerb stone and the sidewalk at kerb height, a kerb face along the kerb line and a skirt round
    /// the back. Junctions take the widest arm's section; markings stop at the cuts.
    /// </summary>
    private void Junction(RoadMesh rm, SplineGraph g, JunctionFootprint f)
    {
        var sec = f.Cuts.Select(c => SectionOf(g.Edge(c.EdgeId))).MaxBy(s => s.HalfWidth)!;
        var outline = f.Outline.Select(p => new Vector2(p.X, p.Y)).ToArray();
        var mouths = f.Cuts.Select(c => MouthCentre(g, c)).ToList();
        float sw = sec.HasSidewalks ? sec.HalfWidth - sec.HalfCarriageway : 0f;
        if (sw < 0.01f)
        {
            Fill(rm, sec.Bands[0].Surface, outline, 0);
            return;
        }

        var merged = outline;
        foreach (var c in f.Cuts) merged = Largest(Geometry2D.MergePolygons(merged, Mouth(g, c, sw + 1f))) ?? merged;
        float kerbTop = MathF.Min(_sectionStyle.KerbTopWidth, sw);
        var kerbLine = Largest(Geometry2D.OffsetPolygon(merged, -sw));
        var kerbBack = Largest(Geometry2D.OffsetPolygon(merged, -(sw - kerbTop)));
        if (kerbLine is null || kerbBack is null)
        {
            Fill(rm, SurfaceKind.Asphalt, outline, 0);
            return;
        }

        float kh = _sectionStyle.KerbHeight;
        // The gutter runs on round the kerb line, inset the same way so it stays open across the mouths.
        var gutterLine = sec.GutterWidth > 0 ? Largest(Geometry2D.OffsetPolygon(merged, -(sw + sec.GutterWidth))) : null;
        foreach (var asphalt in Geometry2D.IntersectPolygons(kerbLine, outline))
        {
            if (gutterLine is null) Fill(rm, SurfaceKind.Asphalt, asphalt, 0);
            else
            {
                foreach (var core in Geometry2D.IntersectPolygons(gutterLine, asphalt)) Fill(rm, SurfaceKind.Asphalt, core, 0);
                foreach (var gutter in Geometry2D.ClipPolygons(asphalt, gutterLine)) Fill(rm, SurfaceKind.Gutter, gutter, 0);
            }
            // A kerb face along the kerb line, facing the road; none across a mouth, where the arm's asphalt carries on.
            bool ccw = SignedArea(asphalt) > 0;
            Edges(asphalt, (a, b) =>
            {
                if (OnOutline((a + b) / 2, outline)) return;
                var d = (b - a).Normalized();
                var inward = ccw ? new Vector2(-d.Y, d.X) : new Vector2(d.Y, -d.X);
                rm.Quad(SurfaceKind.Kerb, Point(a, 0), Point(b, 0), Point(b, kh), Point(a, kh), new Vector3(inward.X, 0, inward.Y));
            });
        }
        foreach (var top in Geometry2D.IntersectPolygons(kerbBack, outline))
            foreach (var kerb in Geometry2D.ClipPolygons(top, kerbLine))
                Fill(rm, SurfaceKind.Kerb, kerb, kh);
        foreach (var walk in Geometry2D.ClipPolygons(outline, kerbBack))
            Fill(rm, SurfaceKind.Sidewalk, walk, kh);

        // The skirt round the back of the sidewalk, except across the mouths.
        bool outCcw = SignedArea(outline) > 0;
        Edges(outline, (a, b) =>
        {
            var mid = (a + b) / 2;
            if (mouths.Any(m => m.DistanceTo(mid) < 0.25f)) return;
            var d = (b - a).Normalized();
            var outward = outCcw ? new Vector2(d.Y, -d.X) : new Vector2(-d.Y, d.X);
            rm.Quad(SurfaceKind.Sidewalk, Point(a, kh), Point(b, kh), Point(b, -_sectionStyle.SkirtDepth), Point(a, -_sectionStyle.SkirtDepth),
                new Vector3(outward.X, 0, outward.Y));
        });
    }

    /// <summary>
    /// The wedge on the outside of a hard corner where two roads meet with no footprint (<see cref="Junctions.BendFill"/>):
    /// a fan round the node out to the back of the sidewalk. The carriageway part keeps the crown by distance from the
    /// node (as both roads have it there), then a kerb, the sidewalk and a skirt.
    /// </summary>
    private void BendFill(RoadMesh rm, SplineGraph g, GraphNode n)
    {
        if (Junctions.BendFill(g, n.Id) is not { } bend || bend.Count < 2) return;
        var sec = n.Edges.Select(id => SectionOf(g.Edge(id))).MaxBy(s => s.HalfWidth)!;
        var c = new Vector2(n.Position.X, n.Position.Y);
        float sw = sec.HasSidewalks ? sec.HalfWidth - sec.HalfCarriageway : 0f;
        float kerbTop = MathF.Min(_sectionStyle.KerbTopWidth, sw), kh = _sectionStyle.KerbHeight;
        var pts = bend.Select(p => new Vector2(p.X, p.Y)).ToList();
        Vector2 At(Vector2 p, float fromBack)
        {
            float r = c.DistanceTo(p);
            return r < 1e-3f ? p : c + (p - c) * (MathF.Max(0, r - fromBack) / r);
        }
        float Crown(Vector2 p) => sec.CarriagewayHeight(c.DistanceTo(p));
        var carriage = sec.Bands.First(b => !b.Raised).Surface;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            var (ka, kb) = (At(a, sw), At(b, sw));
            rm.Tri(carriage, Point(c, Crown(c)), Point(ka, Crown(ka)), Point(kb, Crown(kb)), Vector3.Up);
            if (sw < 0.01f) continue;
            var (ta, tb) = (At(a, sw - kerbTop), At(b, sw - kerbTop));
            var inward = (c - (ka + kb) / 2).Normalized();
            rm.Quad(SurfaceKind.Kerb, Point(ka, 0), Point(kb, 0), Point(kb, kh), Point(ka, kh), new Vector3(inward.X, 0, inward.Y));
            rm.Quad(SurfaceKind.Kerb, Point(ka, kh), Point(kb, kh), Point(tb, kh), Point(ta, kh), Vector3.Up);
            rm.Quad(SurfaceKind.Sidewalk, Point(ta, kh), Point(tb, kh), Point(b, kh), Point(a, kh), Vector3.Up);
            rm.Quad(SurfaceKind.Sidewalk, Point(a, kh), Point(b, kh), Point(b, -_sectionStyle.SkirtDepth), Point(a, -_sectionStyle.SkirtDepth),
                new Vector3(-inward.X, 0, -inward.Y));
        }
    }

    /// <summary>A flat polygon (draped on the ground) at a height, facing up.</summary>
    private void Fill(RoadMesh rm, SurfaceKind kind, Vector2[] poly, float height)
    {
        if (poly.Length < 3) return;
        var tris = Geometry2D.TriangulatePolygon(poly);
        if (tris.Length >= 3)
        {
            for (int i = 0; i + 2 < tris.Length; i += 3)
                rm.Tri(kind, Point(poly[tris[i]], height), Point(poly[tris[i + 1]], height), Point(poly[tris[i + 2]], height), Vector3.Up);
            return;
        }
        // Not a simple polygon (overlapping arms under Anarchy): a fan still covers most of it.
        var centre = poly.Aggregate(Vector2.Zero, (s, p) => s + p) / poly.Length;
        for (int i = 0; i < poly.Length; i++)
            rm.Tri(kind, Point(centre, height), Point(poly[i], height), Point(poly[(i + 1) % poly.Length], height), Vector3.Up);
    }

    /// <summary>A rectangle over an arm from just inside its cut out to <paramref name="length"/> past it, as wide as the
    /// arm, so insetting the outline keeps the mouth open.</summary>
    private static Vector2[] Mouth(SplineGraph g, ArmCut c, float length)
    {
        var (pos, outward, left, half) = CutFrame(g, c);
        var p0 = pos - outward * 0.05f;
        var p1 = pos + outward * length;
        half += 0.01f;
        return new[] { p0 + left * half, p1 + left * half, p1 - left * half, p0 - left * half };
    }

    private static Vector2 MouthCentre(SplineGraph g, ArmCut c) => CutFrame(g, c).Pos;

    /// <summary>Where an arm is cut: the point on its centre line, the direction away from the node, its left, its half
    /// width.</summary>
    private static (Vector2 Pos, Vector2 Outward, Vector2 Left, float Half) CutFrame(SplineGraph g, ArmCut c)
    {
        var e = g.Edge(c.EdgeId);
        float s = c.AtStart ? c.CutBack : e.Alignment.Length - c.CutBack;
        var sample = e.Alignment.Curve.Sample(s);
        var t = new Vector2(sample.Tangent.X, sample.Tangent.Y);
        var left = SplineMath.Left(sample.Tangent);
        return (new Vector2(sample.Position.X, sample.Position.Y), c.AtStart ? t : -t, new Vector2(left.X, left.Y), e.Rules.Width / 2);
    }

    /// <summary>The biggest of some polygons by area; null if there are none.</summary>
    private static Vector2[]? Largest(IEnumerable<Vector2[]> polys) =>
        polys.Where(p => p.Length >= 3).MaxBy(p => MathF.Abs(SignedArea(p)));

    private static float SignedArea(Vector2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) a += p[i].Cross(p[(i + 1) % p.Length]);
        return a / 2;
    }

    private static void Edges(Vector2[] poly, Action<Vector2, Vector2> each)
    {
        for (int i = 0; i < poly.Length; i++) each(poly[i], poly[(i + 1) % poly.Length]);
    }

    /// <summary>Whether a point lies on a polygon's boundary (within a couple of centimetres).</summary>
    private static bool OnOutline(Vector2 p, Vector2[] poly)
    {
        for (int i = 0; i < poly.Length; i++)
            if (Geometry2D.GetClosestPointToSegment(p, poly[i], poly[(i + 1) % poly.Length]).DistanceTo(p) < 0.02f) return true;
        return false;
    }
}
