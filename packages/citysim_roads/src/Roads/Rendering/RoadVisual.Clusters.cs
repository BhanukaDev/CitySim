using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Roads;

public sealed partial class RoadVisual
{
    /// <summary>Sidewalk islands in the last build (for the demo's checks).</summary>
    public int Islands { get; private set; }

    /// <summary>Zebra crossings out to islands in the last build.</summary>
    public int IslandCrossings { get; private set; }

    // An island narrower than this across, or smaller than its square, is left as asphalt (and gets chevrons).
    private const float IslandMinWidth = 1.5f;

    /// <summary>
    /// Junctions too close to fit apart (<see cref="JunctionClusters"/>) as one paved area. Its asphalt is every
    /// carriageway in it together (the inner edges node to node, each arm out to its cut, a disc at each node), with the
    /// outside corners rounded at the kerb's radius. A space the carriageways close in on is a raised sidewalk island
    /// (its tips rounded as far as it fits), with a zebra out to the sidewalk across each road beside it where there's
    /// room. The sidewalk round the outside is the asphalt grown by the sidewalk's width, so it keeps its width all round.
    /// Asphalt no car needs gets chevrons, as in a single junction. Level between its nodes' heights.
    /// </summary>
    private void Cluster(RoadMesh rm, SplineGraph g, JunctionCluster cl, IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var inner = cl.Inner.Select(g.Edge).ToList();
        var all = inner.Concat(cl.Arms.Select(c => g.Edge(c.EdgeId))).ToList();
        var sec = all.Select(SectionOf).MaxBy(s => s.HalfWidth)!;
        float sw = sec.HasSidewalks ? sec.HalfWidth - sec.HalfCarriageway : 0f;
        float kerbR = all.Min(e => MathF.Max(e.Rules.KerbRadius, 0)) + sw;
        float ext = sw + 1f;
        float kh = _sectionStyle.KerbHeight, kerbTop = MathF.Min(_sectionStyle.KerbTopWidth, sw);
        _levelAt = LevelBetween(cl.Nodes.Select(g.Node).ToList());
        try
        {
            // Every carriageway in the cluster, the arms run on past their cuts so the mouths stay open.
            var parts = new List<Vector2[]>();
            foreach (var e in inner) parts.Add(Corridor(e, 0, e.Alignment.Length, SectionOf(e).HalfCarriageway));
            foreach (var c in cl.Arms)
            {
                var e = g.Edge(c.EdgeId);
                float len = e.Alignment.Length, reach = MathF.Min(c.CutBack + ext, len);
                parts.Add(c.AtStart ? Corridor(e, 0, reach, SectionOf(e).HalfCarriageway) : Corridor(e, len - reach, len, SectionOf(e).HalfCarriageway));
            }
            foreach (int id in cl.Nodes)
            {
                var n = g.Node(id);
                float r = n.Edges.Select(x => SectionOf(g.Edge(x)).HalfCarriageway).Min();
                parts.Add(Disc(new Vector2(n.Position.X, n.Position.Y), r));
            }
            var (outers, holes) = Union(parts);
            // Round the outside corners (a closing: grow by the kerb's radius, shrink back).
            var grown = Union(outers.SelectMany(o => Solid(Offset(o, kerbR)))).Outers;
            var road = grown.SelectMany(o => Solid(Offset(o, -kerbR))).ToList();
            var islands = holes.Select(RoundIsland).Where(i => i is not null).Select(i => i!).ToList();

            // Beyond each arm's cut the road's own segment takes over.
            var mouths = cl.Arms.Select(c => CutFrame(g, c)).ToList();
            var beyond = mouths.Select(m => Rect(m.Pos, m.Outward, m.Left, ext + sw + 1, m.Half + 0.5f)).ToArray();
            List<Vector2[]> Trim(IEnumerable<Vector2[]> polys) => beyond.Aggregate(polys.ToList(), (ps, b) => ps.SelectMany(p => Minus(p, b)).ToList());
            bool OnMouth(Vector2 p) => mouths.Any(m =>
                MathF.Abs((p - m.Pos).Dot(m.Outward)) < 0.03f && MathF.Abs((p - m.Pos).Dot(m.Left)) <= m.Half + 0.55f);

            var asphalt = Trim(road);
            var outside = sw > 0.01f ? Trim(road.SelectMany(r => Solid(Offset(r, sw)))) : asphalt;
            var paths = new List<TrackPath>();
            foreach (int id in cl.Nodes) paths.AddRange(Tracks(rm, g, footprints[id]));
            foreach (var e in inner) paths.AddRange(InnerTracks(rm, e, footprints));

            if (sw < 0.01f)
            {
                foreach (var p in MinusAll(asphalt, islands)) Fill(rm, sec.Bands[0].Surface, p, 0);
                return;
            }

            // Asphalt, a gutter along every kerb (the outside and round the islands), kerb faces.
            float gw = sec.GutterWidth;
            var core = gw > 0 ? Trim(road.SelectMany(r => Solid(Offset(r, -gw)))) : asphalt;
            var islandsOut = gw > 0 ? islands.SelectMany(i => Solid(Offset(i, gw))).ToList() : islands;
            foreach (var p in MinusAll(core, islandsOut)) Fill(rm, SurfaceKind.Asphalt, p, 0);
            if (gw > 0)
            {
                foreach (var p in MinusAll(asphalt, core)) Fill(rm, SurfaceKind.Gutter, p, 0);
                foreach (var p in MinusAll(islandsOut, islands)) Fill(rm, SurfaceKind.Gutter, p, 0);
            }
            foreach (var a in asphalt) KerbFaces(rm, a, facingIn: true, OnMouth);
            foreach (var i in islands) KerbFaces(rm, i, facingIn: false, _ => false);
            // Zebras out to the islands, kept clear of the chevrons.
            var zebras = islands.SelectMany(i => inner.Select(e => IslandCrossing(e, i, road, outside))).OfType<Zebra>().ToList();
            Hatching(rm, core, paths, islandsOut.Concat(zebras.Select(z => z.Clear)));
            foreach (var z in zebras) PaintZebra(rm, z);

            // The kerb stone and sidewalk round the outside, and the skirt behind it.
            var kerbBack = road.SelectMany(r => Solid(Offset(r, kerbTop))).ToList();
            foreach (var o in outside)
            {
                foreach (var k in kerbBack)
                    foreach (var top in Geometry2D.IntersectPolygons(o, k))
                        foreach (var p in MinusAll(new List<Vector2[]> { top }, road)) Fill(rm, SurfaceKind.Kerb, p, kh);
                foreach (var p in MinusAll(new List<Vector2[]> { o }, kerbBack)) Fill(rm, SurfaceKind.Sidewalk, p, kh);
                Skirt(rm, o, OnMouth);
            }

            // The islands: a kerb stone round a sidewalk top, and zebras out to them.
            foreach (var i in islands)
            {
                var top = Solid(Offset(i, -kerbTop, Geometry2D.PolyJoinType.Miter)).ToList();
                foreach (var p in MinusAll(new List<Vector2[]> { i }, top)) Fill(rm, SurfaceKind.Kerb, p, kh);
                foreach (var p in top) Fill(rm, SurfaceKind.Sidewalk, p, kh);
                Islands++;
            }
        }
        finally
        {
            _levelAt = null;
        }
    }

    /// <summary>A strip <paramref name="half"/> either side of an edge's centre from station <paramref name="s0"/> to
    /// <paramref name="s1"/>.</summary>
    private static Vector2[] Corridor(GraphEdge e, float s0, float s1, float half)
    {
        var left = new List<Vector2>();
        var right = new List<Vector2>();
        foreach (float s in Stations(s0, s1))
        {
            var sample = e.Alignment.Curve.Sample(s);
            var l = SplineMath.Left(sample.Tangent) * half;
            var c = sample.Position + SplineMath.Left(sample.Tangent) * e.Offset;
            left.Add(new Vector2(c.X + l.X, c.Y + l.Y));
            right.Add(new Vector2(c.X - l.X, c.Y - l.Y));
        }
        right.Reverse();
        return left.Concat(right).ToArray();
    }

    private static Vector2[] Disc(Vector2 c, float r, int n = 24) =>
        Enumerable.Range(0, n).Select(k => c + Vector2.FromAngle(MathF.Tau * k / n) * r).ToArray();

    /// <summary>A rectangle from <paramref name="pos"/> out along <paramref name="outward"/> for <paramref name="length"/>,
    /// <paramref name="half"/> either side.</summary>
    private static Vector2[] Rect(Vector2 pos, Vector2 outward, Vector2 left, float length, float half)
    {
        var p1 = pos + outward * length;
        return new[] { pos + left * half, p1 + left * half, p1 - left * half, pos - left * half };
    }

    /// <summary>A hole the carriageways close in on, as an island: its tips rounded at the largest radius that keeps it
    /// (up to 1.5 m), or null if it's too small to stand on.</summary>
    private static Vector2[]? RoundIsland(Vector2[] hole)
    {
        if (MathF.Abs(SignedArea(hole)) < IslandMinWidth * IslandMinWidth
            || !Offset(hole, -IslandMinWidth / 2).Any()) return null;
        foreach (float r in new[] { 1.5f, 1f, 0.5f })
        {
            var shrunk = Solid(Offset(hole, -r)).ToList();
            if (shrunk.Count != 1) continue;
            var round = Solid(Offset(shrunk[0], r)).FirstOrDefault();
            if (round is not null) return round;
        }
        return hole;
    }

    /// <summary>
    /// A zebra from an island across an inner edge to the sidewalk: square to the edge, at the place nearest the island's
    /// middle where the island is right beside the road and there's sidewalk (not a corner's road) across it for the
    /// crossing's whole width. None if there's no such place.
    /// </summary>
    private Zebra? IslandCrossing(GraphEdge e, Vector2[] island, List<Vector2[]> road, List<Vector2[]> outside)
    {
        var st = _sectionStyle;
        var sec = SectionOf(e);
        if (!Crossings.Crossable(sec)) return null;
        var mid = island.Aggregate(Vector2.Zero, (s, p) => s + p) / island.Length;
        var curve = e.Alignment.Curve;
        float hc = sec.HalfCarriageway, half = st.CrossingWidth / 2, clear = 0.4f;
        bool In(Vector2 p, IEnumerable<Vector2[]> polys) => polys.Any(q => Geometry2D.IsPointInPolygon(p, q));
        (Vector2 C, Vector2 T, Vector2 Left) Frame(float s)
        {
            var sample = curve.Sample(s);
            var t = new Vector2(sample.Tangent.X, sample.Tangent.Y).Normalized();
            var c = sample.Position + SplineMath.Left(sample.Tangent) * e.Offset;
            return (new Vector2(c.X, c.Y), t, new Vector2(-t.Y, t.X));
        }
        bool Fits(float s)
        {
            var (c, t, left) = Frame(s);
            int side = (mid - c).Dot(left) > 0 ? 1 : -1;
            for (float u = -half; u <= half + 1e-3f; u += half / 2)
            {
                var along = c + t * u;
                if (!Geometry2D.IsPointInPolygon(along + left * side * (hc + clear), island)) return false;
                var far = along - left * side * (hc + clear);
                if (In(far, road) || !In(far, outside)) return false;
            }
            return true;
        }
        var stations = new List<float>();
        for (float s = half; s <= curve.Length - half; s += 0.25f) stations.Add(s);
        float? at = stations.Where(s => Frame(s).C.DistanceTo(mid) < hc + 8f)
            .OrderBy(s => Frame(s).C.DistanceTo(mid)).Cast<float?>().FirstOrDefault(s => Fits(s!.Value));
        if (at is not { } best) return null;
        var (cc, tt, ll) = Frame(best);
        return new Zebra(cc, tt, ll, sec, Rect(cc - tt * (half + 0.3f), tt, ll, st.CrossingWidth + 0.6f, hc + 0.5f));
    }

    /// <summary>A zebra square to a road at <see cref="C"/> (<see cref="T"/> along the road, <see cref="Left"/> across
    /// it), and the ground it keeps clear of chevrons.</summary>
    private sealed record Zebra(Vector2 C, Vector2 T, Vector2 Left, RoadSection Sec, Vector2[] Clear);

    /// <summary>A zebra's bars, along the road and across the carriageway as at a junction's mouth.</summary>
    private void PaintZebra(RoadMesh rm, Zebra z)
    {
        var st = _sectionStyle;
        float half = st.CrossingWidth / 2, reach = z.Sec.HalfCarriageway - z.Sec.GutterWidth - 0.2f, pitch = st.CrossingBar + st.CrossingGap;
        int bars = (int)((2 * reach + st.CrossingGap) / pitch);
        float first = (bars * pitch - st.CrossingGap) / 2 - st.CrossingBar / 2, bh = st.CrossingBar / 2;
        for (int k = 0; k < bars; k++)
        {
            float o = first - k * pitch;
            var bar = new[] { z.C + z.Left * (o + bh) - z.T * half, z.C + z.Left * (o + bh) + z.T * half, z.C + z.Left * (o - bh) + z.T * half, z.C + z.Left * (o - bh) - z.T * half };
            PaintFill(rm, bar, p => (p - z.C).Dot(z.Left) - o, bh);
        }
        IslandCrossings++;
    }

    /// <summary>Wear along an inner edge's lanes between its two junctions' cuts; returns their paths for the hatching.</summary>
    private IEnumerable<TrackPath> InnerTracks(RoadMesh rm, GraphEdge e, IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var sec = SectionOf(e);
        var (c0, c1) = Junctions.CutBacks(e, footprints);
        var curve = e.Alignment.Curve;
        float s0 = MathF.Min(c0, curve.Length / 2), s1 = MathF.Max(curve.Length - c1, s0);
        var a = curve.Sample(s0);
        var b = curve.Sample(s1);
        Vector2 P(NumVector2 v) => new(v.X, v.Y);
        var (ta, tb) = (P(a.Tangent).Normalized(), P(b.Tangent).Normalized());
        var (la, lb) = (P(SplineMath.Left(a.Tangent)), P(SplineMath.Left(b.Tangent)));
        a = a with { Position = a.Position + SplineMath.Left(a.Tangent) * e.Offset };
        b = b with { Position = b.Position + SplineMath.Left(b.Tangent) * e.Offset };
        var paths = new List<TrackPath>();
        foreach (var lane in sec.Lanes)
        {
            var path = lane.Forward
                ? new TrackPath(P(a.Position) + la * lane.Offset, ta, P(b.Position) + lb * lane.Offset, tb, sec.LaneWidth / 2, 0)
                : new TrackPath(P(b.Position) + lb * lane.Offset, -tb, P(a.Position) + la * lane.Offset, -ta, sec.LaneWidth / 2, 0);
            Ribbon(rm, path, rm.Wear);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Kerb faces along a polygon's edges, facing into it (<paramref name="facingIn"/>, the road inside a
    /// kerb) or out of it (an island), except where <paramref name="skip"/> says (a mouth).</summary>
    private void KerbFaces(RoadMesh rm, Vector2[] poly, bool facingIn, Func<Vector2, bool> skip)
    {
        float kh = _sectionStyle.KerbHeight;
        bool ccw = SignedArea(poly) > 0;
        Edges(poly, (a, b) =>
        {
            if (skip((a + b) / 2)) return;
            var d = (b - a).Normalized();
            var inward = ccw ? new Vector2(-d.Y, d.X) : new Vector2(d.Y, -d.X);
            var n = facingIn ? inward : -inward;
            rm.Quad(SurfaceKind.Kerb, Point(a, 0), Point(b, 0), Point(b, kh), Point(a, kh), new Vector3(n.X, 0, n.Y));
        });
    }

    /// <summary>The skirt round the back of a sidewalk, down into the ground, except where <paramref name="skip"/> says.</summary>
    private void Skirt(RoadMesh rm, Vector2[] poly, Func<Vector2, bool> skip)
    {
        float kh = _sectionStyle.KerbHeight;
        bool ccw = SignedArea(poly) > 0;
        Edges(poly, (a, b) =>
        {
            if (skip((a + b) / 2)) return;
            var d = (b - a).Normalized();
            var outward = ccw ? new Vector2(d.Y, -d.X) : new Vector2(-d.Y, d.X);
            rm.Quad(SurfaceKind.Sidewalk, Point(a, kh), Point(b, kh), Point(b, -_sectionStyle.SkirtDepth), Point(a, -_sectionStyle.SkirtDepth),
                new Vector3(outward.X, 0, outward.Y));
        });
    }

    /// <summary>The height across a cluster: its nodes' heights, each weighted by how near it is.</summary>
    private static Func<NumVector2, float>? LevelBetween(List<CitySim.Splines.GraphNode> nodes)
    {
        if (nodes.Any(n => n.Height is null)) return null;
        return p =>
        {
            float sum = 0, weight = 0;
            foreach (var n in nodes)
            {
                float w = 1 / (NumVector2.DistanceSquared(p, n.Position) + 1);
                sum += n.Height!.Value * w;
                weight += w;
            }
            return sum / weight;
        };
    }

    // Polygon sets with Godot's Geometry2D, which takes and gives simple polygons: a hole comes back as a polygon wound
    // the other way, and can't be passed back in. These keep holes apart, and cut a region in two through a hole
    // (MinusAll) so every piece drawn is a simple polygon.

    /// <summary>The union of some polygons: the outlines, and the holes they close in.</summary>
    private static (List<Vector2[]> Outers, List<Vector2[]> Holes) Union(IEnumerable<Vector2[]> polys)
    {
        var outers = new List<Vector2[]>();
        var holes = new List<Vector2[]>();
        foreach (var p in polys)
        {
            if (p.Length < 3) continue;
            holes = holes.SelectMany(h => Minus(h, p)).ToList();
            var merged = p;
            var keep = new List<Vector2[]>();
            foreach (var o in outers)
            {
                if (Geometry2D.IntersectPolygons(o, merged).Count == 0) { keep.Add(o); continue; }
                var res = Geometry2D.MergePolygons(o, merged);
                var big = res.MaxBy(r => MathF.Abs(SignedArea(r)))!;
                bool cw = Geometry2D.IsPolygonClockwise(big);
                merged = big;
                holes.AddRange(res.Where(r => r.Length >= 3 && Geometry2D.IsPolygonClockwise(r) != cw));
            }
            keep.Add(merged);
            outers = keep;
        }
        return (outers, holes);
    }

    // Godot rounds an offset's arcs to a fixed tolerance, coarse at a kerb's few metres: offset at this many times the
    // size and scale back, for smooth corners.
    private const float OffsetScale = 16f;

    /// <summary>A polygon grown (or shrunk, negative) by <paramref name="delta"/> with round corners.</summary>
    private static IEnumerable<Vector2[]> Offset(Vector2[] poly, float delta, Geometry2D.PolyJoinType join = Geometry2D.PolyJoinType.Round) =>
        Geometry2D.OffsetPolygon(poly.Select(p => p * OffsetScale).ToArray(), delta * OffsetScale, join)
            .Select(r => r.Select(p => p / OffsetScale).ToArray());

    /// <summary>The outlines among a boolean result, without its holes.</summary>
    private static IEnumerable<Vector2[]> Solid(IEnumerable<Vector2[]> polys)
    {
        var list = polys.Where(p => p.Length >= 3).ToList();
        if (list.Count == 0) return list;
        bool cw = Geometry2D.IsPolygonClockwise(list.MaxBy(p => MathF.Abs(SignedArea(p)))!);
        return list.Where(p => Geometry2D.IsPolygonClockwise(p) == cw);
    }

    /// <summary><paramref name="a"/> minus <paramref name="b"/> as simple polygons: if <paramref name="b"/> would leave a
    /// hole, <paramref name="a"/> is cut in two through it first.</summary>
    private static List<Vector2[]> Minus(Vector2[] a, Vector2[] b)
    {
        var r = Geometry2D.ClipPolygons(a, b).Where(p => p.Length >= 3).ToList();
        if (r.Select(Geometry2D.IsPolygonClockwise).Distinct().Count() <= 1) return r;
        float x = (b.Min(p => p.X) + b.Max(p => p.X)) / 2;
        float x0 = a.Min(p => p.X) - 1, x1 = a.Max(p => p.X) + 1, y0 = a.Min(p => p.Y) - 1, y1 = a.Max(p => p.Y) + 1;
        var halves = new[]
        {
            new[] { new Vector2(x0, y0), new Vector2(x, y0), new Vector2(x, y1), new Vector2(x0, y1) },
            new[] { new Vector2(x, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x, y1) },
        };
        return halves.SelectMany(h => Geometry2D.IntersectPolygons(a, h))
            .SelectMany(p => Solid(Geometry2D.ClipPolygons(p, b))).ToList();
    }

    private static List<Vector2[]> MinusAll(IEnumerable<Vector2[]> polys, IEnumerable<Vector2[]> cuts) =>
        cuts.Aggregate(polys.ToList(), (ps, c) => ps.SelectMany(p => Minus(p, c)).ToList());
}
