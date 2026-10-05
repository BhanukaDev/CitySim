using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CitySim.Splines;

namespace CitySim.Roads;

/// <summary>Which way a link goes across a junction, seen from the lane coming in (right-hand traffic).</summary>
public enum Move { Straight, Right, Left, UTurn }

/// <summary>The lanes of one arm at a junction's mouth, in plan metres: the lanes coming in and going out (kerbside
/// first), the direction away from the node at the mouth and from the centre toward the kerb of the lanes coming in.
/// <see cref="Heading"/>: the road's direction away from the node at the node (not the mouth's when the road curves).
/// <see cref="Spine"/>: the road's line (its alignment, not moved by an offset) from the mouth to the node, about a
/// metre apart, each point with its right as seen coming in.</summary>
public sealed record ArmLanes(int Edge, bool AtStart, Vector2 Mouth, Vector2 Outward, Vector2 Kerb,
    IReadOnlyList<Vector2> In, IReadOnlyList<Vector2> Out, float LaneWidth, Vector2 Heading,
    IReadOnlyList<(Vector2 P, Vector2 Right)> Spine);

/// <summary>A lane coming in from arm <see cref="From"/> linked to a lane going out of arm <see cref="To"/> (indices
/// into the junction's <see cref="ArmLanes"/> list, lanes kerbside first).</summary>
public readonly record struct LaneLink(int From, int FromLane, int To, int ToLane, Move Move);

/// <summary>What the player set for the links leaving one arm (<see cref="RoadEnd.Links"/>): the links, to arms named
/// by their <see cref="RoadEnd.Tag"/>, and the tags of the arms they were set against. An arm not in
/// <see cref="Known"/> (a road added to the junction later) gets the automatic links.</summary>
public sealed record LinkSet(IReadOnlyList<int> Known, IReadOnlyList<(int FromLane, int ToTag, int ToLane)> Links);

/// <summary>
/// Lane links: which lane coming into a junction may go to which lane going out. The road's rule (<see cref="Auto"/>):
/// straight on from every lane to the lane most in line with it, and into every lane out (so lanes merge where a road
/// narrows and branch where it widens), right turns from the kerbside lane to the kerbside lane, left turns
/// from the inside lane to the inside lane, no U-turns. The Lane Links tool overrides that per junction; the wear tracks
/// and the chevrons (<see cref="RoadVisual"/>) follow whatever the links are.
/// </summary>
public static class LaneLinks
{
    /// <summary>How square two arms must be to count as straight on (cosine).</summary>
    private const float StraightDot = 0.7f;

    /// <summary>The move from <paramref name="a"/> to <paramref name="b"/>, by the roads' directions at the node: a road
    /// curving through the junction is straight on, though its mouths are square to each other.</summary>
    public static Move MoveOf(ArmLanes a, ArmLanes b)
    {
        if (ReferenceEquals(a, b)) return Move.UTurn;
        if (Vector2.Dot(-a.Heading, b.Heading) > StraightDot) return Move.Straight;
        return Vector2.Dot(b.Heading, RightOf(a.Heading)) > 0 ? Move.Right : Move.Left;
    }

    /// <summary>The right of travel coming in along <paramref name="outward"/> (right-hand traffic: the kerb side).</summary>
    private static Vector2 RightOf(Vector2 outward) => new(outward.Y, -outward.X);

    /// <summary>A lane's metres right of the road's line, seen along its travel: coming in at <paramref name="a"/>, or
    /// going out at <paramref name="b"/>. A road moved sideways (an offset) counts from its line, so it lines up by
    /// where its lanes really are.</summary>
    private static float InAcross(ArmLanes a, Vector2 p) => Vector2.Dot(p - a.Spine[0].P, a.Spine[0].Right);
    private static float OutAcross(ArmLanes b, Vector2 q) => -Vector2.Dot(q - b.Spine[0].P, b.Spine[0].Right);

    /// <summary>The road's rule for the lanes from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static IEnumerable<(int FromLane, int ToLane)> Auto(ArmLanes a, ArmLanes b)
    {
        if (a.In.Count == 0 || b.Out.Count == 0) yield break;
        switch (MoveOf(a, b))
        {
            case Move.Straight:
                // Each lane on to the lane out most in line with it, then every lane out still unfed from the lane in
                // most in line with it: where lanes end they merge into a neighbour, where lanes start one branches into
                // both, and a road moved sideways (an offset) lines up by where its lanes really are.
                var ins = a.In.Select(p => InAcross(a, p)).ToList();
                var outs = b.Out.Select(q => OutAcross(b, q)).ToList();
                int Nearest(float x, List<float> lanes) =>
                    Enumerable.Range(0, lanes.Count).MinBy(i => MathF.Abs(lanes[i] - x) + i * 1e-4f);
                var fed = new HashSet<int>();
                for (int k = 0; k < a.In.Count; k++)
                {
                    int to = Nearest(ins[k], outs);
                    fed.Add(to);
                    yield return (k, to);
                }
                for (int j = 0; j < b.Out.Count; j++)
                    if (!fed.Contains(j)) yield return (Nearest(outs[j], ins), j);
                break;
            case Move.Right:
                yield return (0, 0);
                break;
            case Move.Left:
                yield return (a.In.Count - 1, b.Out.Count - 1);
                break;
        }
    }

    /// <summary>The links at a junction: what the player set where they set it, the road's rule everywhere else.</summary>
    public static List<LaneLink> Resolve(SplineGraph g, IReadOnlyList<ArmLanes> arms)
    {
        var ends = arms.Select(a => RoadEnd.Of(g.Edge(a.Edge), a.AtStart)).ToList();
        var links = new List<LaneLink>();
        for (int i = 0; i < arms.Count; i++)
        {
            var set = ends[i].Links;
            for (int j = 0; j < arms.Count; j++)
            {
                var (a, b) = (arms[i], arms[j]);
                var move = MoveOf(a, b);
                int tag = ends[j].Tag;
                if (set is not null && tag != 0 && set.Known.Contains(tag))
                {
                    foreach (var (from, to, toLane) in set.Links)
                        if (to == tag && from < a.In.Count && toLane < b.Out.Count) links.Add(new LaneLink(i, from, j, toLane, move));
                }
                else foreach (var (from, to) in Auto(a, b)) links.Add(new LaneLink(i, from, j, to, move));
            }
        }
        return links.Distinct().ToList();
    }

    /// <summary>Whether the player has set any links at the junction.</summary>
    public static bool Edited(SplineGraph g, IReadOnlyList<ArmLanes> arms) =>
        arms.Any(a => RoadEnd.Of(g.Edge(a.Edge), a.AtStart).Links is not null);

    /// <summary>Stores <paramref name="links"/> as the junction's links: every arm gets a tag (once) and every arm its
    /// links out, set against all the arms there now.</summary>
    public static void Set(SplineGraph g, IReadOnlyList<ArmLanes> arms, IEnumerable<LaneLink> links)
    {
        int next = g.Edges.SelectMany(e => new[] { RoadEnd.Of(e, true).Tag, RoadEnd.Of(e, false).Tag }).DefaultIfEmpty(0).Max() + 1;
        var tags = new int[arms.Count];
        for (int i = 0; i < arms.Count; i++)
        {
            var end = RoadEnd.Of(g.Edge(arms[i].Edge), arms[i].AtStart);
            tags[i] = end.Tag != 0 ? end.Tag : next++;
        }
        var all = links.ToList();
        for (int i = 0; i < arms.Count; i++)
        {
            var mine = all.Where(l => l.From == i).Select(l => (l.FromLane, tags[l.To], l.ToLane)).Distinct().ToList();
            var end = RoadEnd.Of(g.Edge(arms[i].Edge), arms[i].AtStart) with { Tag = tags[i], Links = new LinkSet(tags, mine) };
            g.SetEndData(arms[i].Edge, arms[i].AtStart, RoadEnd.Store(end));
        }
    }

    /// <summary>The radius a U-turn swings round: most cars and vans make it in one go.</summary>
    public const float UTurnRadius = 5f;

    /// <summary>
    /// Points along a link's path with its heading, about a metre apart, leaving the lane coming in at
    /// <paramref name="p0"/> heading <paramref name="d0"/> and joining the lane going out at <paramref name="p3"/> heading
    /// <paramref name="d3"/>, square to the cuts. A turn is a cubic that follows a circle's arc between where the two
    /// lanes' lines meet (so it stays inside the corner they make, however hard the turn); straight on, or lanes that
    /// don't meet ahead, a cubic with a third of the chord for handles. A U-turn swings round
    /// <see cref="UTurnRadius"/> (<see cref="UTurn"/>). None for a path under half a metre. <paramref name="wide"/>
    /// scales a turn's handles: under 1 a driver cutting the corner, over 1 one swinging wide (<see cref="Lines"/>).
    /// </summary>
    public static IEnumerable<(Vector2 P, Vector2 D)> Path(Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d3, bool turn, float wide = 1)
    {
        if (Vector2.Dot(d0, d3) < -0.7f && Vector2.Distance(p0, p3) >= 0.5f) return UTurn(p0, d0, p3, d3);
        return Sweep(p0, d0, p3, d3, turn, wide);
    }

    /// <summary><see cref="Path(Vector2, Vector2, Vector2, Vector2, bool, float)"/> with no U-turn: one cubic however
    /// hard the turn (a 135° turn off a bend still sweeps round, not swing out as a bulb).</summary>
    private static IEnumerable<(Vector2 P, Vector2 D)> Sweep(Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d3, bool turn, float wide)
    {
        float chord = Vector2.Distance(p0, p3);
        if (chord < 0.5f) return Enumerable.Empty<(Vector2, Vector2)>();
        float h0 = chord / 3, h3 = chord / 3;
        float angle = MathF.Acos(Math.Clamp(Vector2.Dot(d0, d3), -1f, 1f));
        // Where the lane in's line (on from p0) meets the lane out's (back from p3): t and s metres along them.
        float den = Cross(d0, d3);
        if (turn && angle > 0.25f && MathF.Abs(den) > 1e-4f)
        {
            float t = Cross(p3 - p0, d3) / den, s = Cross(d0, p3 - p0) / den;
            if (t > 0.1f && s > 0.1f)
            {
                // A circle's arc turning θ has handles (4/3) tan(θ/4) R and meets the corner tan(θ/2) R from each end.
                float k = 4f / 3 * MathF.Tan(angle / 4) / MathF.Tan(angle / 2);
                (h0, h3) = (MathF.Min(k * t, chord), MathF.Min(k * s, chord));
            }
            else (h0, h3) = (chord * 0.39f, chord * 0.39f);
            (h0, h3) = (h0 * wide, h3 * wide);
        }
        return Cubic(p0, p0 + d0 * h0, p3 - d3 * h3, p3, d0);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Points about a metre apart along a cubic, with its heading (<paramref name="d0"/> where it has none).</summary>
    private static IEnumerable<(Vector2 P, Vector2 D)> Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Vector2 d0, bool first = true)
    {
        float len = Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
        int n = Math.Max(6, (int)MathF.Ceiling(len));
        for (int i = first ? 0 : 1; i <= n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var d = 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2);
            yield return (u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3,
                d.LengthSquared() < 1e-12f ? d0 : Vector2.Normalize(d));
        }
    }

    /// <summary>
    /// A U-turn as a "bulb": the lanes in and out are only lanes apart, too close for a car to turn between, so the path
    /// swings out away from the turn, round a half circle of <see cref="UTurnRadius"/> centred between the lanes, and
    /// back in to the lane going out. Lanes at least two radii apart just turn round a half circle between them.
    /// </summary>
    private static IEnumerable<(Vector2 P, Vector2 D)> UTurn(Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d3)
    {
        var x = new Vector2(-d0.Y, d0.X);                 // across, toward the lane going out
        if (Vector2.Dot(p3 - p0, x) < 0) x = -x;
        float w = Vector2.Dot(p3 - p0, x), ahead = MathF.Max(0, Vector2.Dot(p3 - p0, d0));
        float r = MathF.Max(UTurnRadius, w / 2), swing = r - w / 2;
        // How far in the half circle's centre is: room for the swing out, past wherever the lane out's end is.
        var c = p0 + x * (w / 2) + d0 * (ahead + MathF.Max(1.2f * swing, 1f));
        var c0 = c - x * r;                               // where the half circle starts, heading d0
        var c1 = c + x * r;                               // and ends, heading back
        float lead = Vector2.Dot(c0 - p0, d0) / 2;
        foreach (var q in Cubic(p0, p0 + d0 * lead, c0 - d0 * lead, c0, d0)) yield return q;
        int n = Math.Max(8, (int)MathF.Ceiling(MathF.PI * r));
        for (int i = 1; i <= n; i++)
        {
            float a = MathF.PI * (1 - i / (float)n);      // from π (c0) over the top to 0 (c1)
            yield return (c + x * (r * MathF.Cos(a)) + d0 * (r * MathF.Sin(a)), x * MathF.Sin(a) - d0 * MathF.Cos(a));
        }
        float back = MathF.Max(0.5f, Vector2.Dot(c1 - p3, d0)) / 2;
        foreach (var q in Cubic(c1, c1 - d0 * back, p3 - d3 * back, p3, -d0, first: false)) yield return q;
    }

    /// <summary>The lines drivers take through a turn, from cutting the corner to swinging wide (handle scales for
    /// <see cref="Path(Vector2, Vector2, Vector2, Vector2, bool, float)"/>), and the wear each lays: they meet in the
    /// lanes at both ends and spread apart mid-turn, so a turn's wear is wider than a lane. The wear adds up to more than
    /// the turn's share, as tyres scrub harder on a turn than going straight.</summary>
    public static readonly (float Wide, float Share)[] Lines = [(0.75f, 0.25f), (0.88f, 0.3f), (1f, 0.4f), (1.12f, 0.3f), (1.25f, 0.25f)];

    /// <summary>A link's path: <see cref="Path(Vector2, Vector2, Vector2, Vector2, bool, float)"/>, or straight on
    /// through a road that curves through the junction, along the road's own line (<see cref="Along"/>).
    /// <paramref name="wide"/>: the driver's line through a turn (<see cref="Lines"/>).</summary>
    public static IEnumerable<(Vector2 P, Vector2 D)> Path(IReadOnlyList<ArmLanes> arms, LaneLink l, float wide = 1) =>
        Path(arms, l.From, l.FromLane, l.To, l.ToLane, wide);

    /// <summary>
    /// A turn is one smooth curve from mouth to mouth where that works (it turns one way only, never further than the
    /// lanes' directions need, and stays in front of both mouths), the way a car sweeps round however the arms curve
    /// near the node (user report, 2026-10-05: turns off a bend's curved arms ran on up the lane and hooked back).
    /// Otherwise it follows the lanes to near their corner (<see cref="Turn"/>).
    /// </summary>
    public static IEnumerable<(Vector2 P, Vector2 D)> Path(IReadOnlyList<ArmLanes> arms, int from, int fromLane, int to, int toLane,
        float wide = 1)
    {
        var (a, b) = (arms[from], arms[to]);
        var move = MoveOf(a, b);
        if (move == Move.Straight && Vector2.Dot(-a.Heading, b.Heading) > 0.995f && Vector2.Dot(-a.Outward, b.Outward) < 0.995f)
            return Along(a, InAcross(a, a.In[fromLane]), b, OutAcross(b, b.Out[toLane]));
        var (p0, d0, p3, d3) = (a.In[fromLane], -a.Outward, b.Out[toLane], b.Outward);
        if (move is Move.Left or Move.Right)
        {
            var sweep = Sweep(p0, d0, p3, d3, turn: true, wide).ToList();
            if (Sweeps(sweep, p0, d0, p3, d3, move == Move.Left)) return sweep;
            if (Turn(a, InAcross(a, p0), b, OutAcross(b, p3), wide) is { } turn) return turn;
        }
        return Path(p0, d0, p3, d3, move != Move.Straight, wide);
    }

    /// <summary>Whether a mouth-to-mouth curve is one a car drives: it turns only <paramref name="left"/> (or only
    /// right), through no more than the lanes' directions, and never behind either mouth.</summary>
    private static bool Sweeps(List<(Vector2 P, Vector2 D)> path, Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d3, bool left)
    {
        if (path.Count < 2) return false;
        float net = MathF.Acos(Math.Clamp(Vector2.Dot(d0, d3), -1f, 1f));
        if (Swept(path) > net + 0.1f) return false;
        for (int i = 1; i < path.Count; i++)
        {
            // Turning right swings the heading toward its right (RightOf): a positive cross product.
            float c = Cross(path[i - 1].D, path[i].D);
            if (left ? c > 1e-3f : c < -1e-3f) return false;
        }
        return path.All(x => Vector2.Dot(x.P - p0, d0) > -0.05f && Vector2.Dot(x.P - p3, d3) < 0.05f);
    }

    /// <summary>
    /// A turn the way a car takes it: along the lane coming in (round the road's curve, where it curves) to where the
    /// turn starts, a circle's arc into the lane going out, then along that lane to its mouth. The two lanes' lines
    /// (each run on past the node) meet at a corner; the arc starts and ends as far back from it as the shorter lane
    /// allows, so it's as wide as fits. With straight arms that's the arc from mouth to mouth, as before. Null when the
    /// lines don't meet.
    /// </summary>
    private static List<(Vector2 P, Vector2 D)>? Turn(ArmLanes a, float from, ArmLanes b, float to, float wide = 1)
    {
        const float run = 100f; // the lines run on this far past the node
        var inLane = a.Spine.Select(x => x.P + x.Right * from).ToList();
        inLane.Add(inLane[^1] - a.Heading * run);
        var outLane = b.Spine.Select(x => x.P - x.Right * to).Reverse().ToList();
        outLane.Insert(0, outLane[0] - b.Heading * run);
        if (Meet(inLane, outLane) is not var (sa, sb, corner)) return null;
        float lb = Length(outLane);
        var (_, inAtCorner) = At(inLane, sa);
        var (_, outAtCorner) = At(outLane, sb);
        // As far back as the shorter lane allows, then nearer the corner while that would start the turn where a curving
        // lane still points elsewhere (a wide sweep) or the arc would turn past the lane and hook back into it.
        for (float back = MathF.Min(sa, lb - sb); back >= 0.5f; back = back > 1f ? back * 0.85f : 0)
        {
            var (p0, d0) = At(inLane, sa - back);
            var (p3, d3) = At(outLane, sb + back);
            if (Vector2.Dot(d0, inAtCorner) < LaneStraight || Vector2.Dot(d3, outAtCorner) < LaneStraight) continue;
            var arc = Path(p0, d0, p3, d3, turn: true, wide).ToList();
            if (Swept(arc) > MathF.Acos(Math.Clamp(Vector2.Dot(d0, d3), -1f, 1f)) + 0.1f) continue;
            var pts = Part(inLane, 0, sa - back).ToList();
            pts.AddRange(arc.Skip(1));
            pts.AddRange(Part(outLane, sb + back, lb).Skip(1));
            return pts;
        }
        return null;
    }

    /// <summary>How square a lane must stay to its direction at the corner where a turn starts or ends (cos 15°).</summary>
    private const float LaneStraight = 0.966f;

    /// <summary>The total angle a path turns through, both ways added.</summary>
    private static float Swept(List<(Vector2 P, Vector2 D)> path)
    {
        float sum = 0;
        for (int i = 1; i < path.Count; i++)
            sum += MathF.Abs(MathF.Atan2(Cross(path[i - 1].D, path[i].D), Vector2.Dot(path[i - 1].D, path[i].D)));
        return sum;
    }

    /// <summary>Where two polylines first cross: metres along each and the point.</summary>
    private static (float A, float B, Vector2 P)? Meet(List<Vector2> a, List<Vector2> b)
    {
        float sa = 0;
        for (int i = 0; i + 1 < a.Count; i++)
        {
            float sb = 0;
            var r = a[i + 1] - a[i];
            for (int j = 0; j + 1 < b.Count; j++)
            {
                var q = b[j + 1] - b[j];
                float den = Cross(r, q);
                if (MathF.Abs(den) > 1e-6f)
                {
                    float t = Cross(b[j] - a[i], q) / den, u = Cross(b[j] - a[i], r) / den;
                    if (t is >= 0 and <= 1 && u is >= 0 and <= 1)
                        return (sa + t * r.Length(), sb + u * q.Length(), a[i] + r * t);
                }
                sb += q.Length();
            }
            sa += r.Length();
        }
        return null;
    }

    private static float Length(List<Vector2> line)
    {
        float l = 0;
        for (int i = 1; i < line.Count; i++) l += Vector2.Distance(line[i], line[i - 1]);
        return l;
    }

    /// <summary>The point <paramref name="s"/> metres along a polyline, and its direction there.</summary>
    private static (Vector2 P, Vector2 D) At(List<Vector2> line, float s)
    {
        for (int i = 0; i + 1 < line.Count; i++)
        {
            float l = Vector2.Distance(line[i], line[i + 1]);
            if ((s <= l || i + 2 == line.Count) && l > 1e-6f)
                return (line[i] + (line[i + 1] - line[i]) * MathF.Min(s / l, 1), (line[i + 1] - line[i]) / l);
            s -= l;
        }
        return (line[^1], Vector2.Normalize(line[^1] - line[0]));
    }

    /// <summary>A polyline from <paramref name="s0"/> to <paramref name="s1"/> metres along, with its directions.</summary>
    private static IEnumerable<(Vector2 P, Vector2 D)> Part(List<Vector2> line, float s0, float s1)
    {
        yield return At(line, s0);
        float s = 0;
        for (int i = 1; i < line.Count; i++)
        {
            s += Vector2.Distance(line[i], line[i - 1]);
            if (s > s0 + 0.05f && s < s1 - 0.05f) yield return (line[i], Vector2.Normalize(line[i] - line[i - 1]));
        }
        if (s1 > s0 + 0.05f) yield return At(line, s1);
    }

    /// <summary>Straight on through a road that curves through the junction: along the road's line from
    /// <paramref name="a"/>'s mouth through the node to <paramref name="b"/>'s, <paramref name="from"/> metres right of
    /// it easing to <paramref name="to"/> (a lane that moves over, where lanes are added or dropped).</summary>
    private static IEnumerable<(Vector2 P, Vector2 D)> Along(ArmLanes a, float from, ArmLanes b, float to)
    {
        // The line and the right of travel along it: a's coming in, then b's going out.
        var line = a.Spine.Concat(b.Spine.Reverse().Skip(1).Select(x => (x.P, Right: -x.Right))).ToList();
        var at = new float[line.Count];
        for (int i = 1; i < line.Count; i++) at[i] = at[i - 1] + Vector2.Distance(line[i].P, line[i - 1].P);
        var pts = new List<Vector2>();
        for (int i = 0; i < line.Count; i++)
        {
            float k = at[^1] > 0 ? at[i] / at[^1] : 0;
            float off = from + (to - from) * k * k * (3 - 2 * k);
            pts.Add(line[i].P + line[i].Right * off);
        }
        for (int i = 0; i < pts.Count; i++)
        {
            var d = pts[Math.Min(i + 1, pts.Count - 1)] - pts[Math.Max(i - 1, 0)];
            yield return (pts[i], d.LengthSquared() < 1e-12f ? -a.Outward : Vector2.Normalize(d));
        }
    }
}
