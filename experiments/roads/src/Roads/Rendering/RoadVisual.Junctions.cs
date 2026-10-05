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
    /// the back. Junctions take the widest arm's section and sit level at the node's height; markings stop at the cuts.
    /// </summary>
    private void Junction(RoadMesh rm, SplineGraph g, JunctionFootprint f)
    {
        var sec = f.Cuts.Select(c => SectionOf(g.Edge(c.EdgeId))).MaxBy(s => s.HalfWidth)!;
        _level = LevelOf(g.Node(f.NodeId));
        var paths = Tracks(rm, g, f);
        var outline = f.Outline.Select(p => new Vector2(p.X, p.Y)).ToArray();
        var mouths = f.Cuts.Select(c => MouthCentre(g, c)).ToList();
        float sw = sec.HasSidewalks ? sec.HalfWidth - sec.HalfCarriageway : 0f;
        if (sw < 0.01f)
        {
            Fill(rm, sec.Bands[0].Surface, outline, 0);
            if (sec.Bands[0].Surface == SurfaceKind.Asphalt) Hatching(rm, new[] { outline }, paths);
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
        var cores = new List<Vector2[]>();
        foreach (var asphalt in Geometry2D.IntersectPolygons(kerbLine, outline))
        {
            if (gutterLine is null)
            {
                Fill(rm, SurfaceKind.Asphalt, asphalt, 0);
                cores.Add(asphalt);
            }
            else
            {
                foreach (var core in Geometry2D.IntersectPolygons(gutterLine, asphalt))
                {
                    Fill(rm, SurfaceKind.Asphalt, core, 0);
                    cores.Add(core);
                }
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
        Hatching(rm, cores, paths);
        if (f.Bend) BendLines(rm, g, f);
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

    // Share of each lane's traffic per movement (right-hand traffic), before normalising over the movements it has.
    private const float StraightShare = 0.55f, RightShare = 0.25f, LeftShare = 0.2f, UTurnShare = 0.05f;
    private const float TrackLift = 0.005f; // the wear above the junction's asphalt, under its paint

    /// <summary>The lanes of every arm of a junction at its mouth (<see cref="LaneLinks"/>), in the footprint's arm
    /// order; null when an arm has no paved lanes (nothing to tell where cars go).</summary>
    public List<ArmLanes>? LanesAt(SplineGraph g, JunctionFootprint f)
    {
        static System.Numerics.Vector2 N(Vector2 v) => new(v.X, v.Y);
        var arms = new List<ArmLanes>();
        foreach (var c in f.Cuts)
        {
            var e = g.Edge(c.EdgeId);
            var sec = SectionOf(e);
            if (!sec.Bands.Any(b => b.Lanes && b.Surface == SurfaceKind.Asphalt)) return null;
            var (pos, outward, left, _) = CutFrame(g, c);
            // Lanes coming in run toward the node: against the curve at its start. Kerbside first.
            List<System.Numerics.Vector2> Lanes(bool incoming) => sec.Lanes.Where(l => l.Forward != (c.AtStart == incoming))
                .OrderByDescending(l => MathF.Abs(l.Offset - sec.Split)).Select(l => N(pos + left * l.Offset)).ToList();
            var into = Lanes(true);
            // From the centre toward the kerb of the lanes coming in (the right of their travel).
            var kerb = into.Count > 0 ? System.Numerics.Vector2.Normalize(into[0] - N(pos + left * sec.Split)) : default;
            arms.Add(new ArmLanes(c.EdgeId, c.AtStart, N(pos), N(outward), kerb, into, Lanes(false), sec.LaneWidth));
        }
        return arms;
    }

    /// <summary>
    /// Tyre wear across a junction: a ribbon a lane wide along every lane link (<see cref="LaneLinks.Resolve"/>: the
    /// road's rule, or what the player set with the Lane Links tool), a cubic curve leaving and joining the lanes square
    /// to the cuts. They carry the lanes' wheel tracks and oil stripe on across the junction (a
    /// <see cref="SurfaceKind.Wear"/> overlay), so wear fans out into the turns instead of stopping at the junction's
    /// edge. Each ribbon's UV2.y is its lane's share of traffic for that movement (the shares of a lane add up to its own
    /// wear), UV2.x across it from −0.5 to 0.5 and UV = (metres along, how hard it turns 0..1). Returns the paths, for
    /// <see cref="Hatching"/>, so the chevrons cover whatever no link drives over; none when an arm has no lanes.
    /// </summary>
    private List<TrackPath> Tracks(RoadMesh rm, SplineGraph g, JunctionFootprint f, bool ribbons = true)
    {
        var paths = new List<TrackPath>();
        if (LanesAt(g, f) is not { } arms) return paths;
        var links = LaneLinks.Resolve(g, arms);
        static float Share(Move m) => m switch { Move.Straight => StraightShare, Move.Right => RightShare, Move.Left => LeftShare, _ => UTurnShare };
        var total = links.GroupBy(l => (l.From, l.FromLane)).ToDictionary(x => x.Key, x => x.Sum(l => Share(l.Move)));
        static Vector2 G(System.Numerics.Vector2 v) => new(v.X, v.Y);
        foreach (var l in links)
        {
            var (a, b) = (arms[l.From], arms[l.To]);
            float turn = l.Move switch
            {
                Move.Straight => 0,
                Move.Left => 1 - MathF.Max(0, System.Numerics.Vector2.Dot(-a.Outward, b.Outward)),
                _ => 1,
            };
            var path = new TrackPath(G(a.In[l.FromLane]), G(-a.Outward), G(b.Out[l.ToLane]), G(b.Outward), a.LaneWidth / 2, turn);
            if (ribbons) Ribbon(rm, path, rm.Wear * Share(l.Move) / total[(l.From, l.FromLane)]);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>A path cars take across a junction: from <see cref="P0"/> heading <see cref="D0"/> to <see cref="P3"/>
    /// heading <see cref="D3"/>, a lane (<see cref="Half"/> its half width) wide, turning 0..1.</summary>
    private readonly record struct TrackPath(Vector2 P0, Vector2 D0, Vector2 P3, Vector2 D3, float Half, float Turn)
    {
        /// <summary>Points along the path's centre with its heading (<see cref="LaneLinks.Path(System.Numerics.Vector2,
        /// System.Numerics.Vector2, System.Numerics.Vector2, System.Numerics.Vector2, bool)"/>).</summary>
        public IEnumerable<(Vector2 P, Vector2 D)> Points() =>
            LaneLinks.Path(new(P0.X, P0.Y), new(D0.X, D0.Y), new(P3.X, P3.Y), new(D3.X, D3.Y), Turn > 0)
                .Select(x => (new Vector2(x.P.X, x.P.Y), new Vector2(x.D.X, x.D.Y)));

        /// <summary>The ground the path sweeps, <paramref name="half"/> either side of its centre, run on a metre past
        /// both ends so it cuts clean through a junction's mouths.</summary>
        public Vector2[] Swept(float half)
        {
            var pts = Points().ToList();
            if (pts.Count == 0) return Array.Empty<Vector2>();
            pts.Insert(0, (pts[0].P - pts[0].D, pts[0].D));
            pts.Add((pts[^1].P + pts[^1].D, pts[^1].D));
            var left = pts.Select(x => x.P + new Vector2(-x.D.Y, x.D.X) * half);
            var right = pts.Select(x => x.P - new Vector2(-x.D.Y, x.D.X) * half).Reverse();
            return left.Concat(right).ToArray();
        }
    }

    /// <summary>One wear ribbon along a <see cref="TrackPath"/> (see <see cref="Tracks"/>).</summary>
    private void Ribbon(RoadMesh rm, TrackPath path, float share)
    {
        float turn = path.Turn;
        RoadMesh.Vertex? l0 = null, r0 = null;
        float along = 0;
        var last = path.P0;
        foreach (var (p, d) in path.Points())
        {
            var side = new Vector2(-d.Y, d.X) * path.Half;
            along += p.DistanceTo(last);
            last = p;
            var l1 = new RoadMesh.Vertex(Point(p + side, TrackLift), new Vector2(along, turn), new Vector2(-0.5f, share));
            var r1 = new RoadMesh.Vertex(Point(p - side, TrackLift), new Vector2(along, turn), new Vector2(0.5f, share));
            if (l0 is { } pl && r0 is { } pr) rm.Quad(SurfaceKind.Wear, pl, pr, r1, l1, Vector3.Up);
            (l0, r0) = (l1, r1);
        }
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
        _level = LevelOf(n);
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

    /// <summary>
    /// A road turning a corner at a node (<see cref="JunctionFootprint.Bend"/>): its lines carried on round the corner
    /// from one cut to the other, each a cubic curve leaving and joining its line square to the cuts (a circle's arc
    /// for straight arms), dashes stretched to whole ones. Only when both arms are the same road and neither has a
    /// crossing set there.
    /// </summary>
    private void BendLines(RoadMesh rm, SplineGraph g, JunctionFootprint f)
    {
        if (f.Cuts.Count != 2) return;
        var (ca, cb) = (f.Cuts[0], f.Cuts[1]);
        var (ea, eb) = (g.Edge(ca.EdgeId), g.Edge(cb.EdgeId));
        if (ea.Rules.Id != eb.Rules.Id || Marks.ContainsKey((ea.Id, ca.AtStart)) || Marks.ContainsKey((eb.Id, cb.AtStart))) return;
        var sec = SectionOf(ea);
        var (pa, outA, leftA, _) = CutFrame(g, ca);
        var (pb, outB, leftB, _) = CutFrame(g, cb);
        // Left of the way from arm a round to arm b, at each cut: an edge's left is against that way where it runs
        // the other way (away from the node at a, into it at b).
        var (la, lb) = (ca.AtStart ? -leftA : leftA, cb.AtStart ? leftB : -leftB);
        float turn = MathF.Acos(Math.Clamp((-outA).Dot(outB), -1f, 1f));
        foreach (var line in sec.Lines)
        {
            float t = ca.AtStart ? -line.Offset : line.Offset;
            Vector2 p0 = pa + la * t, p3 = pb + lb * t;
            float chord = p0.DistanceTo(p3);
            if (chord < 0.1f) continue;
            // Handles of a cubic that follows a circle's arc through the turn: (4/3) tan(θ/4) R, with R from the chord.
            float h = turn < 0.01f ? chord / 3 : chord * 2 / 3 * MathF.Tan(turn / 4) / MathF.Sin(turn / 2);
            Vector2 p1 = p0 - outA * h, p2 = p3 - outB * h;
            var pts = new List<Vector2>();
            int n = Math.Max(8, (int)MathF.Ceiling(chord * 2));
            for (int i = 0; i <= n; i++)
            {
                float k = i / (float)n, u = 1 - k;
                pts.Add(u * u * u * p0 + 3 * u * u * k * p1 + 3 * u * k * k * p2 + k * k * k * p3);
            }
            var at = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++) at[i] = at[i - 1] + pts[i].DistanceTo(pts[i - 1]);
            float len = at[^1];
            if (line.Dash <= 0)
            {
                BendPaint(rm, sec, pts, at, 0, len, line);
                continue;
            }
            float period = line.Dash + line.Gap;
            int dashes = Math.Max(1, (int)MathF.Round(len / period));
            float p = len / dashes, on = p * line.Dash / period;
            for (int k = 0; k < dashes; k++)
            {
                float d = k * p + (p - on) / 2;
                BendPaint(rm, sec, pts, at, d, d + on, line);
            }
        }
    }

    /// <summary>A painted strip along a polyline (<paramref name="at"/>: metres along it at each point) from
    /// <paramref name="a"/> to <paramref name="b"/>, on the junction's flat asphalt, with the UVs <see cref="Paint"/> gives.</summary>
    private void BendPaint(RoadMesh rm, RoadSection sec, List<Vector2> pts, float[] at, float a, float b, SectionLine line)
    {
        float half = line.Width / 2, height = sec.CarriagewayHeight(0, 0) + PaintLift;
        var stations = new List<float> { a };
        stations.AddRange(at.Where(x => x > a && x < b));
        stations.Add(b);
        RoadMesh.Vertex? l0 = null, r0 = null;
        foreach (float s in stations)
        {
            int i = Math.Clamp(Array.FindIndex(at, x => x >= s), 1, pts.Count - 1);
            var d = (pts[i] - pts[i - 1]).Normalized();
            float k = at[i] > at[i - 1] ? (s - at[i - 1]) / (at[i] - at[i - 1]) : 0;
            var c = pts[i - 1].Lerp(pts[i], k);
            var side = new Vector2(-d.Y, d.X) * half;
            var ends = new Vector2(s - a, b - s);
            var l1 = new RoadMesh.Vertex(Point(c + side, height), new Vector2(half, half), ends);
            var r1 = new RoadMesh.Vertex(Point(c - side, height), new Vector2(half, -half), ends);
            if (l0 is { } pl && r0 is { } pr) rm.Quad(SurfaceKind.Paint, pl, pr, r1, l1, Vector3.Up);
            (l0, r0) = (l1, r1);
        }
    }

    /// <summary>A flat polygon at a height above the junction's level, facing up.</summary>
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
        var centre = sample.Position + left * e.Offset; // the road's centre, beside the alignment by its offset
        return (new Vector2(centre.X, centre.Y), c.AtStart ? t : -t, new Vector2(left.X, left.Y), e.Rules.Width / 2);
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
