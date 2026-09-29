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
/// each arm's cut-back, the curb arcs between neighbouring arms, and the outline polygon (the arm ends and curbs in
/// order, star-shaped around the node, so a fan from <see cref="Centre"/> fills it).
/// </summary>
public sealed record JunctionFootprint(int NodeId, Vector2 Centre, IReadOnlyList<ArmCut> Cuts, IReadOnlyList<Curb> Curbs, IReadOnlyList<Vector2> Outline)
{
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
    /// <summary>An arm's cut-back never takes more than this share of its edge, so both ends fit.</summary>
    private const float MaxCutShare = 0.45f;

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

    /// <summary>
    /// The footprint of a <see cref="JunctionKind.Node"/> junction with three or more arms: between each pair of
    /// neighbouring arms, a curb arc tangent to their facing sides, with the narrower arm's
    /// <see cref="ProfileRules.DefaultRadius"/>; each arm is cut back to where its curbs start. Null otherwise.
    /// </summary>
    public static JunctionFootprint? Footprint(SplineGraph g, int nodeId)
    {
        var arms = g.Arms(nodeId);
        if (arms.Count < 3 || KindOf(arms) != JunctionKind.Node) return null;
        var centre = g.Node(nodeId).Position;
        var sorted = Sorted(arms);
        int n = sorted.Count;
        var cut = new float[n];
        var curbs = new Curb?[n]; // curbs[i]: between arm i and arm i + 1

        for (int i = 0; i < n; i++)
        {
            var (a, gap) = sorted[i];
            var b = sorted[(i + 1) % n].Arm;
            if (gap >= StraightGapDegrees) continue;
            float wa = a.Rules.Width / 2, wb = b.Rules.Width / 2;
            var narrow = a.Rules.Width < b.Rules.Width || (a.Rules.Width == b.Rules.Width && a.Rules.DefaultRadius <= b.Rules.DefaultRadius) ? a.Rules : b.Rules;
            float r = narrow.DefaultRadius;
            // The sides facing each other: a's side toward b (heading grows toward b), b's side toward a.
            var sideA = centre + TowardNext(a.Direction) * wa;
            var sideB = centre - TowardNext(b.Direction) * wb;
            if (LineCross(sideA, a.Direction, sideB, b.Direction) is not { } x) continue;
            float theta = gap * MathF.PI / 180f;
            float tl = r / MathF.Tan(theta / 2);
            var from = x + a.Direction * tl;
            var to = x + b.Direction * tl;
            cut[i] = MathF.Max(cut[i], Vector2.Dot(from - centre, a.Direction));
            int j = (i + 1) % n;
            cut[j] = MathF.Max(cut[j], Vector2.Dot(to - centre, b.Direction));
            if (r > 0)
                curbs[i] = new Curb(x + Vector2.Normalize(a.Direction + b.Direction) * (r / MathF.Sin(theta / 2)), r, from, to);
        }

        // Keep both ends of a short edge room: cap each cut-back at a share of its edge.
        for (int i = 0; i < n; i++)
            cut[i] = Math.Clamp(cut[i], 0, g.Edge(sorted[i].Arm.EdgeId).Alignment.Length * MaxCutShare);

        var outline = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            var a = sorted[i].Arm;
            float w = a.Rules.Width / 2;
            var end = centre + a.Direction * cut[i];
            outline.Add(end - TowardNext(a.Direction) * w);
            outline.Add(end + TowardNext(a.Direction) * w);
            if (curbs[i] is { } c && cut[i] >= Vector2.Dot(c.From - centre, a.Direction) - 1e-3f)
            {
                var b = sorted[(i + 1) % n].Arm;
                bool fits = cut[(i + 1) % n] >= Vector2.Dot(c.To - centre, b.Direction) - 1e-3f;
                if (fits) outline.AddRange(ArcPoints(c));
            }
        }
        var cuts = sorted.Select((x, i) => new ArmCut(x.Arm.EdgeId, x.Arm.AtStart, cut[i])).ToList();
        return new JunctionFootprint(nodeId, centre, cuts, curbs.Where(c => c is not null).Select(c => c!.Value).ToList(), outline);
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

    private static Vector2? LineCross(Vector2 p, Vector2 dp, Vector2 q, Vector2 dq)
    {
        float den = SplineMath.Cross(dp, dq);
        if (MathF.Abs(den) < 1e-6f) return null;
        return p + dp * (SplineMath.Cross(q - p, dq) / den);
    }
}
