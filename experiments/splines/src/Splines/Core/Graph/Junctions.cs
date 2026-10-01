using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>A curb corner of a junction footprint: an arc from one arm's side to the next arm's side.</summary>
public readonly record struct Curb(Vector2 Centre, float Radius, Vector2 From, Vector2 To);

/// <summary>How far an arm's edge is cut back from the node centre so the curb corners fit.</summary>
public readonly record struct ArmCut(int EdgeId, bool AtStart, float CutBack);

/// <summary>
/// A <see cref="JunctionKind.Node"/> junction's footprint (DESIGN.md → Junctions), as data for the consumer to draw:
/// each arm's cut-back (a station along the arm from the node), the curb arcs between neighbouring arms, and the
/// outline polygon (each arm's curved sides, its cut end and the curbs, in order round the node). The outline isn't
/// always star-shaped around <see cref="Centre"/> when arms curve, so triangulate it rather than fanning.
/// <see cref="Continuous"/>: not a junction but one road running on (a width transition), so markings along the arms,
/// such as a centre line, carry on through it to the node.
/// </summary>
public sealed record JunctionFootprint(int NodeId, Vector2 Centre, IReadOnlyList<ArmCut> Cuts, IReadOnlyList<Curb> Curbs, IReadOnlyList<Vector2> Outline)
{
    public bool Continuous { get; init; }

    public float CutBack(int edgeId, bool atStart) =>
        Cuts.FirstOrDefault(c => c.EdgeId == edgeId && c.AtStart == atStart).CutBack;
}

/// <summary>
/// What a node is as a junction: its kind from the profiles meeting there, the angles between its arms, a short label
/// for tags (<c>T-junction · 90°</c>), and the footprint. Pure Core; the graph stores none of it (it's derived).
/// </summary>
public static class Junctions
{
    /// <summary>A pair of arms this close to straight on gets no curb (the sides just run on).</summary>
    private const float StraightGapDegrees = 178f;
    /// <summary>An arm's cut-back never takes more than this share of an edge with a footprint at its other end too,
    /// so both ends fit.</summary>
    private const float MaxCutShare = 0.45f;
    /// <summary>The share for an edge whose other end has no footprint (a dead end, a bend): nearly all of it.</summary>
    private const float MaxCutShareFree = 0.9f;
    /// <summary>A transition between two widths tapers over this many times the difference in width.</summary>
    private const float TaperPerWidth = 2.5f;
    /// <summary>How far past a short arm's end its side is followed to find where a squeezed pair of arms part.</summary>
    private const float Reach = 200f;

    /// <summary>The strictest kind among the arms: a turnout profile makes the node a turnout, then Node, then Join.</summary>
    public static JunctionKind KindOf(IReadOnlyList<Arm> arms)
    {
        if (arms.Any(a => a.Rules.JunctionKind == JunctionKind.Turnout)) return JunctionKind.Turnout;
        if (arms.Any(a => a.Rules.JunctionKind == JunctionKind.Node)) return JunctionKind.Node;
        return JunctionKind.Join;
    }

    /// <summary>The arms sorted by heading, with the angle (degrees) from each to the next one round.</summary>
    public static List<(Arm Arm, float Gap)> Sorted(IReadOnlyList<Arm> arms)
    {
        var sorted = arms.OrderBy(a => SplineMath.Angle(a.Direction)).ToList();
        var result = new List<(Arm, float)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var next = sorted[(i + 1) % sorted.Count];
            float gap = sorted.Count == 1 ? 360f
                : (SplineMath.Angle(next.Direction) - SplineMath.Angle(sorted[i].Direction) + MathF.Tau) % MathF.Tau * 180f / MathF.PI;
            if (sorted.Count > 1 && gap < 1e-3f && i == sorted.Count - 1) gap = 360f;
            result.Add((sorted[i], gap));
        }
        return result;
    }

    /// <summary>The smallest angle between two neighbouring arms (degrees), or null with fewer than two arms.</summary>
    public static float? SmallestGap(IReadOnlyList<Arm> arms) => arms.Count < 2 ? null : Sorted(arms).Min(x => x.Gap);

    /// <summary>How far an arm is off the line of another (0 = along it either way, 90 = square), in degrees.</summary>
    public static float OffLine(Vector2 a, Vector2 b)
    {
        float deg = MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        return MathF.Min(deg, 180f - deg);
    }

    /// <summary>
    /// For a turnout node: the arms that don't leave along another arm's line within the turnout angle (a square
    /// branch), each with how far off it is. A straight-through pair and a tangent branch pass.
    /// </summary>
    public static List<(Arm Arm, float Degrees)> TurnoutViolations(IReadOnlyList<Arm> arms)
    {
        var bad = new List<(Arm, float)>();
        if (arms.Count < 2) return bad;
        float max = arms.Where(a => a.Rules.JunctionKind == JunctionKind.Turnout).Select(a => a.Rules.TurnoutMaxAngle).DefaultIfEmpty(0).Min();
        for (int i = 0; i < arms.Count; i++)
        {
            float best = arms.Where((_, j) => j != i).Min(o => OffLine(arms[i].Direction, o.Direction));
            if (best > max + 0.5f) bad.Add((arms[i], best));
        }
        return bad;
    }

    /// <summary>The tag a junction gets while drawing and after a finish: <c>T-junction · 90°</c>, <c>4-way · 90°</c>,
    /// <c>turnout · R 300 m</c>, <c>join</c>. Null for a plain end or a continuation (two arms).</summary>
    public static string? Label(SplineGraph g, int nodeId)
    {
        var arms = g.Arms(nodeId);
        if (arms.Count < 3) return null;
        switch (KindOf(arms))
        {
            case JunctionKind.Turnout:
                if (TurnoutViolations(arms).Count > 0) return null;
                // The branch leaves along another arm (within the turnout angle) and curves away: the tightest first
                // corner among such arms is the switch curve.
                float max = MathF.Cos((arms.Max(a => a.Rules.TurnoutMaxAngle) + 0.5f) * MathF.PI / 180f);
                float r = arms.Where(a => arms.Any(o => !o.Equals(a) && Vector2.Dot(a.Direction, o.Direction) > max))
                    .Select(a => SwitchRadius(g.Edge(a.EdgeId).Alignment, a.AtStart)).Where(x => x > 0)
                    .DefaultIfEmpty(0).Min();
                return r > 0 ? $"turnout · R {r:0} m" : "turnout";
            case JunctionKind.Join:
                return "join";
            default:
                float gap = SmallestGap(arms) ?? 0;
                string name = arms.Count switch { 3 => "T-junction", 4 => "4-way", _ => $"{arms.Count}-way" };
                return $"{name} · {gap:0}°";
        }
    }

    private static float SwitchRadius(Alignment a, bool atStart)
    {
        if (a.Pis.Count < 3) return 0;
        return a.EffectiveRadius(atStart ? 1 : a.Pis.Count - 2);
    }

    /// <summary>Whether a node with these arms gets a footprint: a <see cref="JunctionKind.Node"/> junction with three
    /// or more arms, or a <see cref="IsTransition"/>.</summary>
    private static bool HasFootprint(IReadOnlyList<Arm> arms) =>
        (arms.Count >= 3 && KindOf(arms) == JunctionKind.Node) || IsTransition(arms);

    /// <summary>Two arms of different widths running on into each other (an avenue becoming a street): the wider one
    /// tapers down to the narrower over a short stretch instead of ending in a step.</summary>
    public static bool IsTransition(IReadOnlyList<Arm> arms) =>
        arms.Count == 2 && arms[0].EdgeId != arms[1].EdgeId && KindOf(arms) != JunctionKind.Join
        && MathF.Abs(arms[0].Rules.Width - arms[1].Rules.Width) > 0.01f;

    /// <summary>
    /// The footprint of a <see cref="JunctionKind.Node"/> junction with three or more arms: between each pair of
    /// neighbouring arms, a curb arc tangent to their facing sides, with the narrower arm's
    /// <see cref="ProfileRules.DefaultRadius"/>; each arm is cut back to where its curbs start. Arms are followed along
    /// their real curves (not their direction at the node), so a junction on a curve meets the ribbons exactly: a
    /// cut-back is a station along the arm, and the outline runs along each arm's curved sides. A pair of arms too
    /// sharp or too short for even a sharp corner is cut back as far as their edges allow and joined straight, so an
    /// arm never runs on across the others. For a <see cref="IsTransition"/>, the taper (<see cref="Transition"/>).
    /// Null otherwise.
    /// </summary>
    public static JunctionFootprint? Footprint(SplineGraph g, int nodeId)
    {
        var arms = g.Arms(nodeId);
        if (IsTransition(arms)) return Transition(g, nodeId, arms);
        if (arms.Count < 3 || KindOf(arms) != JunctionKind.Node) return null;
        var centre = g.Node(nodeId).Position;
        var sorted = Sorted(arms);
        int n = sorted.Count;
        var paths = sorted.Select(x => new ArmPath(g, x.Arm)).ToArray();
        var cut = new float[n];
        var curbs = new Curb?[n]; // curbs[i]: between arm i and arm i + 1
        var curbAt = new (float From, float To)[n]; // the stations along arm i and arm i + 1 where curbs[i] touches them
        var corner = new bool[n]; // arm i and arm i + 1 meet at curbAt[i]: a curb, or a sharp corner (curbs[i] null)

        for (int i = 0; i < n; i++)
        {
            var (a, gap) = sorted[i];
            int j = (i + 1) % n;
            var b = sorted[j].Arm;
            if (gap >= StraightGapDegrees) continue;
            var narrow = a.Rules.Width < b.Rules.Width || (a.Rules.Width == b.Rules.Width && a.Rules.DefaultRadius <= b.Rules.DefaultRadius) ? a.Rules : b.Rules;
            float r = MathF.Max(narrow.DefaultRadius, 0);
            // The curb's centre is r off both facing sides: where a's side toward b, pushed out by r, meets b's side
            // toward a, pushed out by r. The nearest such point to the node wins. The sides only run out to each arm's
            // cap, so a curb too big for a short arm or a sharp angle finds no crossing: it shrinks to the largest
            // radius that fits (down to a sharp corner) instead of being left out, which left the arms overlapping.
            var hit = CurbCentre(r);
            if (hit is null && r > 0)
            {
                float lo = 0, hi = r;
                var best = CurbCentre(0);
                for (int k = 0; k < 12 && best is not null; k++)
                {
                    float mid = (lo + hi) / 2;
                    if (CurbCentre(mid) is { } h) { lo = mid; best = h; }
                    else hi = mid;
                }
                (r, hit) = (lo, best);
            }
            if (hit is null)
            {
                // Not even a sharp corner fits within the caps. The sides are followed on past both ends (straight on
                // past a short arm's far end, and back through the node) to where they do cross, which is the sharp
                // corner: a squeezed pair cut back there (the caps clamp it below, and the outline then joins the
                // cut ends straight), or, for a wide gap, one side running on behind the node until the other road
                // leaves it. Arms that never part (near parallel) are cut as far as they can go. Leaving them uncut
                // drew one arm's end across the other road.
                var far = FirstCross(paths[i].Side(+1, a.Rules.Width / 2, paths[i].Length, Reach), paths[j].Side(-1, b.Rules.Width / 2, paths[j].Length, Reach), nearest: true);
                if (far is null)
                {
                    if (gap >= 90f) continue;
                    cut[i] = cut[j] = float.MaxValue;
                    continue;
                }
                hit = far;
                r = 0;
            }
            cut[i] = MathF.Max(cut[i], hit.Value.SA);
            cut[j] = MathF.Max(cut[j], hit.Value.SB);
            curbAt[i] = (hit.Value.SA, hit.Value.SB);
            corner[i] = true;
            if (r > 0.1f)
                curbs[i] = new Curb(hit.Value.Point, r, paths[i].SidePoint(hit.Value.SA, +1, a.Rules.Width / 2), paths[j].SidePoint(hit.Value.SB, -1, b.Rules.Width / 2));

            CurveHit? CurbCentre(float radius) =>
                FirstCross(paths[i].Side(+1, a.Rules.Width / 2 + radius), paths[j].Side(-1, b.Rules.Width / 2 + radius));
        }

        // Keep both ends of a short edge room: cap each cut-back at a share of its edge.
        for (int i = 0; i < n; i++)
            cut[i] = Math.Clamp(cut[i], 0, paths[i].Cap);

        // Round the outline: for each arm, its side toward the previous arm (from that curb out to the cut), the cut
        // end, its side toward the next arm (back in to that curb), then the curb itself.
        var outline = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n, j = (i + 1) % n;
            float w = sorted[i].Arm.Rules.Width / 2;
            bool prevFits = corner[prev] && Fits(prev);
            bool nextFits = corner[i] && Fits(i);
            outline.AddRange(paths[i].SideRun(-1, w, prevFits ? curbAt[prev].To : cut[i], cut[i]));
            outline.AddRange(paths[i].SideRun(+1, w, cut[i], nextFits ? curbAt[i].From : cut[i]));
            if (nextFits && curbs[i] is { } curb) outline.AddRange(ArcPoints(curb));

            bool Fits(int k) => cut[k] >= curbAt[k].From - 1e-3f && cut[(k + 1) % n] >= curbAt[k].To - 1e-3f;
        }
        var cuts = sorted.Select((x, i) => new ArmCut(x.Arm.EdgeId, x.Arm.AtStart, cut[i])).ToList();
        return new JunctionFootprint(nodeId, centre, cuts, curbs.Where(c => c is not null).Select(c => c!.Value).ToList(), Dedupe(outline));
    }

    /// <summary>An arm's edge seen from the node: station 0 at the node, growing away from it.</summary>
    private readonly struct ArmPath
    {
        /// <summary>Spacing of the side polylines the curbs are fitted to (a 1 m chord on R 60 is off by 2 mm).</summary>
        private const float Step = 1f;

        private readonly Curve _curve;
        private readonly bool _atStart;

        public ArmPath(SplineGraph g, Arm arm)
        {
            var e = g.Edge(arm.EdgeId);
            _curve = e.Alignment.Curve;
            _atStart = arm.AtStart;
            int far = arm.AtStart ? e.End : e.Start;
            var farArms = g.Arms(far);
            bool shared = e.Start == e.End || HasFootprint(farArms);
            Cap = _curve.Length * (shared ? MaxCutShare : MaxCutShareFree);
        }

        public float Length => _curve.Length;

        /// <summary>The furthest this arm can be cut back: a share of its edge, leaving room for a footprint at the
        /// other end if there is one.</summary>
        public float Cap { get; }

        /// <summary>The centre point and the direction away from the node at station <paramref name="s"/>; past the far
        /// end (or behind the node, at a negative station), straight on along the direction there.</summary>
        public (Vector2 Position, Vector2 Direction) At(float s)
        {
            float over = s > _curve.Length ? s - _curve.Length : MathF.Min(s, 0);
            var c = _curve.Sample(_atStart ? s - over : _curve.Length - s + over);
            var d = _atStart ? c.Tangent : -c.Tangent;
            return (c.Position + d * over, d);
        }

        /// <summary>The point <paramref name="offset"/> off the centre, on the side toward the next arm (+1) or the
        /// previous one (−1).</summary>
        public Vector2 SidePoint(float s, int side, float offset)
        {
            var (p, d) = At(s);
            return p + TowardNext(d) * (side * offset);
        }

        /// <summary>The side at <paramref name="offset"/> as a polyline of (station, point), out to
        /// <paramref name="max"/> (the cut-back cap by default). With <paramref name="reach"/>, it also runs that far
        /// straight on behind the node and past <paramref name="max"/>.</summary>
        public List<(float S, Vector2 P)> Side(int side, float offset, float? max = null, float reach = 0)
        {
            float to = max ?? Cap;
            var pts = new List<(float, Vector2)>();
            if (reach > 0) pts.Add((-reach, SidePoint(-reach, side, offset)));
            for (float s = 0; ; s += Step)
            {
                s = MathF.Min(s, to);
                pts.Add((s, SidePoint(s, side, offset)));
                if (s >= to) break;
            }
            if (reach > 0) pts.Add((to + reach, SidePoint(to + reach, side, offset)));
            return pts;
        }

        /// <summary>Like <see cref="SideRun"/>, but the offset goes from <paramref name="w0"/> at <paramref name="s0"/> to
        /// <paramref name="w1"/> at <paramref name="s1"/>, eased at both ends so the edge leaves and meets the
        /// straight sides tangentially.</summary>
        public IEnumerable<Vector2> TaperRun(int side, float s0, float w0, float s1, float w1)
        {
            int k = Math.Max(4, (int)MathF.Ceiling(MathF.Abs(s1 - s0) / Step));
            for (int i = 0; i <= k; i++)
            {
                float t = (float)i / k, e = t * t * (3 - 2 * t);
                yield return SidePoint(s0 + (s1 - s0) * t, side, w0 + (w1 - w0) * e);
            }
        }

        /// <summary>Points along a side from station <paramref name="s0"/> to <paramref name="s1"/> (either way round),
        /// both ends included.</summary>
        public IEnumerable<Vector2> SideRun(int side, float offset, float s0, float s1)
        {
            int k = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(s1 - s0) / (Step * 2)));
            if (MathF.Abs(s1 - s0) < 1e-3f) k = 0;
            for (int i = 0; i <= k; i++)
                yield return SidePoint(k == 0 ? s0 : s0 + (s1 - s0) * i / k, side, offset);
        }
    }

    /// <summary>
    /// The footprint of a <see cref="IsTransition"/>: the wider arm is cut back by a taper length (a few times the
    /// difference in width, within its cap) and the outline narrows smoothly from its full width there to the narrower
    /// arm's width at the node, where the narrower arm carries on uncut. Drawn in the wider arm's colour.
    /// </summary>
    private static JunctionFootprint Transition(SplineGraph g, int nodeId, IReadOnlyList<Arm> arms)
    {
        var wide = arms[0].Rules.Width > arms[1].Rules.Width ? arms[0] : arms[1];
        var narrow = wide.Equals(arms[0]) ? arms[1] : arms[0];
        var path = new ArmPath(g, wide);
        float ww = wide.Rules.Width / 2, wn = narrow.Rules.Width / 2;
        float len = MathF.Min((ww - wn) * 2 * TaperPerWidth, path.Cap);
        var outline = new List<Vector2>();
        outline.AddRange(path.TaperRun(-1, len, ww, 0, wn));
        outline.AddRange(path.TaperRun(+1, 0, wn, len, ww));
        var cuts = new List<ArmCut> { new(wide.EdgeId, wide.AtStart, len), new(narrow.EdgeId, narrow.AtStart, 0) };
        return new JunctionFootprint(nodeId, g.Node(nodeId).Position, cuts, Array.Empty<Curb>(), Dedupe(outline)) { Continuous = true };
    }

    /// <summary>The crossing of two side polylines nearest the node (smallest station sum, or with
    /// <paramref name="nearest"/> the smallest distance either way along them), with its stations.</summary>
    private static CurveHit? FirstCross(List<(float S, Vector2 P)> a, List<(float S, Vector2 P)> b, bool nearest = false)
    {
        Func<float, float, float> key = nearest ? (x, y) => MathF.Abs(x) + MathF.Abs(y) : (x, y) => x + y;
        CurveHit? best = null;
        for (int i = 0; i + 1 < a.Count; i++)
        {
            var (sa0, a0) = a[i];
            var (sa1, a1) = a[i + 1];
            for (int k = 0; k + 1 < b.Count; k++)
            {
                var (sb0, b0) = b[k];
                var (sb1, b1) = b[k + 1];
                if (!nearest && best is { } bb && sa0 + sb0 >= bb.SA + bb.SB) break;
                var da = a1 - a0;
                var db = b1 - b0;
                float den = SplineMath.Cross(da, db);
                if (MathF.Abs(den) < 1e-9f) continue;
                float t = SplineMath.Cross(b0 - a0, db) / den;
                float u = SplineMath.Cross(b0 - a0, da) / den;
                if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                float sa = sa0 + (sa1 - sa0) * t, sb = sb0 + (sb1 - sb0) * u;
                if (best is null || key(sa, sb) < key(best.Value.SA, best.Value.SB))
                    best = new CurveHit(sa, sb, a0 + da * t);
            }
        }
        return best;
    }

    private static List<Vector2> Dedupe(List<Vector2> pts)
    {
        var result = new List<Vector2>(pts.Count);
        foreach (var p in pts)
            if (result.Count == 0 || Vector2.DistanceSquared(result[^1], p) > 1e-6f) result.Add(p);
        if (result.Count > 1 && Vector2.DistanceSquared(result[0], result[^1]) <= 1e-6f) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>
    /// The fill for a bend where exactly two arms meet at an angle (two profiles that can't be one edge, or what a
    /// delete leaves): the outside of the bend, where the arms' square ends leave a notch, as an arc from one arm's
    /// outer side to the other's (its radius going from one half width to the other). A fan from the node fills it.
    /// Null for other nodes and for two arms running straight through.
    /// </summary>
    public static IReadOnlyList<Vector2>? BendFill(SplineGraph g, int nodeId, int n = 12)
    {
        var arms = g.Arms(nodeId);
        if (arms.Count != 2) return null;
        var sorted = Sorted(arms);
        int i = sorted[0].Gap >= sorted[1].Gap ? 0 : 1; // the outside is the wider gap
        var (a, gap) = sorted[i];
        var b = sorted[1 - i].Arm;
        if (gap < 360f - StraightGapDegrees) return null;
        var centre = g.Node(nodeId).Position;
        float from = SplineMath.Angle(a.Direction) + MathF.PI / 2, sweep = (gap - 180f) * MathF.PI / 180f;
        float ra = a.Rules.Width / 2, rb = b.Rules.Width / 2;
        if (IsTransition(arms)) ra = rb = MathF.Min(ra, rb); // the wider arm has tapered down to the narrower by here
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            float t = (float)k / n;
            pts.Add(centre + SplineMath.Direction(from + sweep * t) * (ra + (rb - ra) * t));
        }
        return pts;
    }

    /// <summary>Every footprint in the graph, by node.</summary>
    public static Dictionary<int, JunctionFootprint> Footprints(SplineGraph g)
    {
        var result = new Dictionary<int, JunctionFootprint>();
        foreach (var n in g.Nodes)
            if (Footprint(g, n.Id) is { } f) result[n.Id] = f;
        return result;
    }

    /// <summary>Whether an edge's end runs on through a <see cref="JunctionFootprint.Continuous"/> footprint.</summary>
    public static bool RunsOn(GraphEdge e, bool atStart, IReadOnlyDictionary<int, JunctionFootprint> footprints) =>
        footprints.TryGetValue(atStart ? e.Start : e.End, out var f) && f.Continuous;

    /// <summary>How far an edge is cut back at each end by the footprints at its nodes.</summary>
    public static (float Start, float End) CutBacks(GraphEdge e, IReadOnlyDictionary<int, JunctionFootprint> footprints) =>
        (footprints.TryGetValue(e.Start, out var fs) ? fs.CutBack(e.Id, true) : 0,
         footprints.TryGetValue(e.End, out var fe) ? fe.CutBack(e.Id, false) : 0);

    /// <summary>
    /// The nearest legal turnout to a square branch attempt (the storyboard's ghost): from <paramref name="start"/> on a
    /// line running along <paramref name="lineDirection"/>, a branch that leaves along the line and curves at
    /// <paramref name="radius"/> toward <paramref name="target"/>. Its one corner sits on the line, placed so the arc
    /// starts right at the switch. A target too close ahead for the radius (a square attempt) is moved further along
    /// the line, keeping its distance to the side, so the ghost still reaches that far out. Null when the target is
    /// on the line.
    /// </summary>
    public static Alignment? TurnoutGhost(Vector2 start, Vector2 lineDirection, Vector2 target, float radius)
    {
        var dir = Vector2.Normalize(lineDirection);
        if (Vector2.Dot(dir, target - start) < 0) dir = -dir;
        float ahead = Vector2.Dot(target - start, dir);
        float side = SplineMath.Cross(dir, target - start);
        if (radius <= 0 || MathF.Abs(side) < 1f) return null;
        if (ahead <= radius + MathF.Abs(side))
        {
            ahead = radius + MathF.Abs(side);
            target = start + dir * ahead + (target - start - dir * Vector2.Dot(target - start, dir));
        }

        // The corner d along the line, where d equals the tangent length R·tan(Δ/2) (so the arc starts at the switch).
        // f(d) = d − R·tan(Δ(d)/2) is negative at 0 and positive where the target is square to the corner (d = ahead).
        float lo = 0, hi = ahead;
        for (int k = 0; k < 40; k++)
        {
            float d = (lo + hi) / 2;
            var to = target - (start + dir * d);
            float delta = MathF.Acos(Math.Clamp(Vector2.Dot(dir, Vector2.Normalize(to)), -1f, 1f));
            if (d - radius * MathF.Tan(delta / 2) < 0) lo = d; else hi = d;
        }
        var corner = start + dir * ((lo + hi) / 2);
        return new Alignment(new[] { new Pi(start), new Pi(corner, radius), new Pi(target) });
    }

    /// <summary>A turnout's ratio as players know it: 1:9 for 6.3°.</summary>
    public static string TurnoutRatio(float degrees) =>
        degrees > 0 ? $"1:{1f / MathF.Tan(degrees * MathF.PI / 180f):0}" : "";

    /// <summary>Points along a curb arc, from its <see cref="Curb.From"/> to its <see cref="Curb.To"/> the short way.</summary>
    public static List<Vector2> ArcPoints(Curb c, int n = 12)
    {
        float a0 = SplineMath.Angle(c.From - c.Centre);
        float sweep = SplineMath.Wrap(SplineMath.Angle(c.To - c.Centre) - a0);
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++) pts.Add(c.Centre + SplineMath.Direction(a0 + sweep * k / n) * c.Radius);
        return pts;
    }

    /// <summary>The unit normal on the side of an arm where the next arm (by heading) lies.</summary>
    private static Vector2 TowardNext(Vector2 dir) => new(-dir.Y, dir.X);
}
