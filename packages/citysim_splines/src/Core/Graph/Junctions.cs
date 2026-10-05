using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// A curb corner of a junction footprint: an arc from one arm's side to the next arm's side. One a kerb handle
/// moved (<see cref="Set"/>) may be lopsided: then it's a conic through <see cref="Control"/> (where the two sides'
/// tangents meet) with <see cref="Weight"/>, and it's rated by the arc each end would make on its own (its distance
/// from the control point as a tangent length): <see cref="Radius"/> at its tighter end, with that end's
/// <see cref="Centre"/>, and <see cref="MaxRadius"/> at the other. The kerb rules apply to both ends, so pulling one
/// end out reads as a bigger kerb (the conic's own sharpest bend, near the tighter end, is a little tighter still).
/// <see cref="Rules"/>: the narrower arm's profile, whose kerb rules it follows.
/// </summary>
public readonly record struct Curb(Vector2 Centre, float Radius, Vector2 From, Vector2 To)
{
    public float MaxRadius { get; init; } = Radius;
    public Vector2? Control { get; init; }
    public float Weight { get; init; }
    public bool Set { get; init; }
    public ProfileRules? Rules { get; init; }
}

/// <summary>
/// A road handle (the Edit tool on a selected junction; DESIGN.md → Junctions → Kerb handles): one per arm with a kerb
/// beside it, on its centre line where the outermost of those kerbs starts (<see cref="Station"/> from the node). It
/// scales this arm's end radius of each of those kerbs (<see cref="Sides"/>) by one factor, so a difference set with
/// their knobs is kept, from <see cref="FactorMin"/> to <see cref="FactorMax"/>: each end within the profile's kerb
/// radii (the minimum only without Anarchy), no more lopsided than <c>Junctions.MaxFlare</c> against its kerb's other
/// end, and starting within what fits; <see cref="MaxLimit"/> says which stops it growing. Each kerb starts where its
/// radius puts it for its own angle, so an arm between an acute and an obtuse kerb moves both. It slides from
/// <see cref="Min"/> to <see cref="Max"/> (<see cref="Track"/>). <see cref="Radii"/>: its kerbs' radii now (each kerb's
/// tighter and looser end).
/// </summary>
public sealed record KerbHandle(int EdgeId, bool AtStart, Vector2 Position, float Station, float Min, float Max, KerbLimit MaxLimit,
    IReadOnlyList<Vector2> Track, IReadOnlyList<(float Min, float Max)> Radii, ProfileRules Rules, bool IsSet)
{
    public float FactorMin { get; init; }
    public float FactorMax { get; init; }
    public IReadOnlyList<KerbSide> Sides { get; init; } = Array.Empty<KerbSide>();

    /// <summary>Where the handle sits with its ends scaled by <paramref name="factor"/>: the outermost kerb start.</summary>
    public float StationOf(float factor) => Sides.Max(s => s.X + s.Radius * factor / s.TanHalf);

    /// <summary>The factor that puts the handle at a station (the inverse of <see cref="StationOf"/>).</summary>
    public float FactorAt(float station) => Sides.Min(s => (station - s.X) * s.TanHalf / s.Radius);

    /// <summary>The side whose kerb starts outermost: the one the handle sits on.</summary>
    public KerbSide Outer => Sides.MaxBy(s => s.X + s.Radius / s.TanHalf);
}

/// <summary>A road handle's kerb on one side of its arm (+1 right, −1 left, as <see cref="KerbEnds"/>): where that
/// kerb's sides' tangents meet as a station along the arm, the tangent of half the angle there, its radius at this arm's
/// end now, and at its other end.</summary>
public readonly record struct KerbSide(int Side, float X, float TanHalf, float Radius, float Other, ProfileRules Rules);

/// <summary>
/// A kerb knob (the Edit tool on a selected junction): one per kerb, in its middle. Dragging it across the corner
/// makes the kerb round at the radius whose arc's middle is under the cursor (<see cref="RadiusAt"/>), from
/// <see cref="Min"/> to <see cref="Max"/> (the profile's kerb radii, the minimum only without Anarchy, and both ends
/// within what fits; <see cref="MaxLimit"/>). It's arm <see cref="EdgeId"/>'s right-hand kerb (+1) and arm
/// <see cref="OtherEdgeId"/>'s left-hand one (−1). <see cref="Radii"/>: its ends now.
/// </summary>
public sealed record KerbKnob(int EdgeId, bool AtStart, int OtherEdgeId, bool OtherAtStart, Vector2 Position, Vector2 Corner,
    Vector2 Bisector, float SinHalf, float Min, float Max, KerbLimit MaxLimit, (float Min, float Max) Radii, ProfileRules Rules, bool IsSet)
{
    /// <summary>The middle of the round kerb of radius r: its corner point plus r (1 / sin(half angle) − 1) along the bisector.</summary>
    public Vector2 At(float r) => Corner + Bisector * (r * (1 / SinHalf - 1));

    public float RadiusAt(Vector2 p) => MathF.Max(0, Vector2.Dot(p - Corner, Bisector)) / (1 / SinHalf - 1);
}

/// <summary>What stops a kerb handle: the room on the arm, the profile's largest kerb, or a kerb's far end reaching
/// <c>Junctions.MaxFlare</c> times its near end.</summary>
public enum KerbLimit { Fit, Radius, Flare }

/// <summary>How far an arm's edge is cut back from the node centre so the curb corners fit. <see cref="Squeezed"/>: its
/// corners wanted more of the edge than it has (a sharp angle, or another junction close by), so it's cut back as far as
/// it can be and they don't fit.</summary>
public readonly record struct ArmCut(int EdgeId, bool AtStart, float CutBack)
{
    public bool Squeezed { get; init; }
}

/// <summary>
/// A <see cref="JunctionKind.Node"/> junction's footprint (DESIGN.md → Junctions), as data for the consumer to draw:
/// each arm's cut-back (a station along the arm from the node), the curb arcs between neighbouring arms, and the
/// outline polygon (each arm's curved sides, its cut end and the curbs, in order round the node). The outline isn't
/// always star-shaped around <see cref="Centre"/> when arms curve, so triangulate it rather than fanning.
/// <see cref="Continuous"/>: not a junction but one road running on (a width transition), so markings along the arms,
/// such as a centre line, carry on through it to the node. <see cref="Bend"/>: not a junction but one road turning a
/// corner at a node (<see cref="Junctions.IsBend"/>): a kerb round the inside, the outside rounded.
/// </summary>
public sealed record JunctionFootprint(int NodeId, Vector2 Centre, IReadOnlyList<ArmCut> Cuts, IReadOnlyList<Curb> Curbs, IReadOnlyList<Vector2> Outline)
{
    public bool Continuous { get; init; }
    public bool Bend { get; init; }

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
    /// <summary>How far a curb's circle may dip into a road it's fitted to (polyline and float slack).</summary>
    private const float CurbClearance = 0.5f;
    /// <summary>A transition between two widths tapers over this many times the difference in width.</summary>
    private const float TaperPerWidth = 2.5f;
    /// <summary>How far past a short arm's end its side is followed to find where a squeezed pair of arms part.</summary>
    private const float Reach = 200f;
    /// <summary>A lopsided kerb needs each end at least this far from where the two sides' tangents meet.</summary>
    private const float MinKerbLeg = 0.05f;
    /// <summary>The tightest kerb a handle reaches with Anarchy on.</summary>
    private const float AnarchyKerb = 0.5f;
    /// <summary>A lopsided kerb's longer end (from where the sides' tangents meet) is at most this many times its shorter.</summary>
    public const float MaxFlare = 4f;

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
    /// or more arms, a <see cref="IsTransition"/> or a <see cref="IsBend"/>.</summary>
    private static bool HasFootprint(IReadOnlyList<Arm> arms) =>
        (arms.Count >= 3 && KindOf(arms) == JunctionKind.Node) || IsTransition(arms) || IsBend(arms);

    /// <summary>Two arms of one width meeting at an angle at a <see cref="JunctionKind.Node"/> node (a grid's corner,
    /// two profiles, what a delete leaves): fitted like a junction, so the inside gets a kerb and both roads are cut
    /// back to it instead of running square into each other.</summary>
    public static bool IsBend(IReadOnlyList<Arm> arms) =>
        arms.Count == 2 && KindOf(arms) == JunctionKind.Node && !IsTransition(arms)
        && Sorted(arms).Min(x => x.Gap) < StraightGapDegrees;

    /// <summary>Two arms of different widths running on into each other (an avenue becoming a street): the wider one
    /// tapers down to the narrower over a short stretch instead of ending in a step.</summary>
    public static bool IsTransition(IReadOnlyList<Arm> arms) =>
        arms.Count == 2 && arms[0].EdgeId != arms[1].EdgeId && KindOf(arms) != JunctionKind.Join
        && MathF.Abs(arms[0].Rules.Width - arms[1].Rules.Width) > 0.01f;

    /// <summary>
    /// The footprint of a <see cref="JunctionKind.Node"/> junction with three or more arms, or of a
    /// <see cref="IsBend"/> (one curb on the inside, the outside rounded as for arms running on): between each pair of
    /// neighbouring arms, a curb arc tangent to their facing sides, with the narrower arm's
    /// <see cref="ProfileRules.KerbRadius"/>; each arm is cut back to where its curbs start. Arms are followed along
    /// their real curves (not their direction at the node), so a junction on a curve meets the ribbons exactly: a
    /// cut-back is a station along the arm, and the outline runs along each arm's curved sides. A pair of arms too
    /// sharp or too short for even a sharp corner is cut back as far as their edges allow and joined straight, so an
    /// arm never runs on across the others. An arm with a kerb handle set (<see cref="GraphEdge.KerbStart"/>) starts
    /// the curbs beside it there instead (<see cref="Layout"/>). For a <see cref="IsTransition"/>, the taper
    /// (<see cref="Transition"/>). Null otherwise.
    /// </summary>
    public static JunctionFootprint? Footprint(SplineGraph g, int nodeId)
    {
        var arms = g.Arms(nodeId);
        if (IsTransition(arms)) return Transition(g, nodeId, arms);
        if (Fit(g, nodeId, arms) is not { } f) return null;
        int n = f.Sorted.Count;
        var (paths, cut, curbs, curbAt, corner) = (f.Paths, (float[])f.Cut.Clone(), f.Curbs, f.CurbAt, f.Corner);
        float Half(int k) => f.Sorted[k].Arm.Rules.Width / 2;
        // Two arms of different widths running on through the node (an avenue becoming a street at a T): the wider
        // one's side there tapers down to the narrower's by the node, as a plain transition does, instead of stepping.
        // The wider arm is cut back at least that far, so the taper is all in the footprint.
        var taper = new float[n];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (corner[i] || f.Sorted[i].Gap < StraightGapDegrees || MathF.Abs(Half(i) - Half(j)) < 0.01f) continue;
            int wide = Half(i) > Half(j) ? i : j;
            taper[wide] = MathF.Min(MathF.Abs(Half(i) - Half(j)) * 2 * TaperPerWidth, paths[wide].Cap);
            cut[wide] = MathF.Max(cut[wide], taper[wide]);
        }

        // Round the outline: for each arm, its side toward the previous arm (from that curb out to the cut), the cut
        // end, its side toward the next arm (back in to that curb), then the curb itself. Two arms with no curb between
        // them because they run on (straight through, or the outside of a gap wider than that) are joined along their
        // own sides in to the node and round the outside of it, so a junction on a curve keeps the curve on its far
        // side and the outside of a wide gap isn't cut off by a chord between the cut ends.
        // A bend with a kerb inside: the outside is that kerb pushed out by the road's width (concentric for a round
        // one), from the cut on one arm to the cut on the other, so the road turns the corner at its own width.
        bool bend = n == 2 && curbs.Any(c => c is not null);
        var outline = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n;
            int next = (i + 1) % n;
            float w = Half(i);
            bool prevFits = corner[prev] && Fits(prev);
            bool nextFits = corner[i] && Fits(i);
            if (RunsOn(prev) && Tapers(prev, i))
            {
                outline.AddRange(paths[i].TaperRun(-1, 0, Half(prev), taper[i], w));
                outline.AddRange(paths[i].SideRun(-1, w, taper[i], cut[i]));
            }
            else outline.AddRange(paths[i].SideRun(-1, w, prevFits ? curbAt[prev].To : RunsOn(prev) && !bend ? 0 : cut[i], cut[i]));
            if (RunsOn(i) && Tapers(i, i))
            {
                outline.AddRange(paths[i].SideRun(+1, w, cut[i], taper[i]));
                outline.AddRange(paths[i].TaperRun(+1, taper[i], w, 0, Half(next)));
            }
            else outline.AddRange(paths[i].SideRun(+1, w, cut[i], nextFits ? curbAt[i].From : RunsOn(i) && !bend ? 0 : cut[i]));
            if (nextFits && curbs[i] is { } curb) outline.AddRange(ArcPoints(curb));
            else if (bend && curbs[next] is { } inside) outline.AddRange(Outside(inside, w + Half(next)));
            else if (RunsOn(i))
            {
                // Round the outside at the narrower width when the pair tapers (the wider one is down to it by the node).
                float ra = w, rb = Half(next);
                if (taper[i] > 0 || taper[next] > 0) ra = rb = MathF.Min(ra, rb);
                outline.AddRange(RoundOutside(g.Node(nodeId).Position, paths[i].At(0).Direction, f.Sorted[i].Gap, ra, rb));
            }

            bool Fits(int k) => cut[k] >= curbAt[k].From - 1e-3f && cut[(k + 1) % n] >= curbAt[k].To - 1e-3f;
            bool RunsOn(int k) => !corner[k] && f.Sorted[k].Gap >= StraightGapDegrees;
            // Whether arm a tapers on its side toward the run-on pair k (arms k and k + 1): it's the wider of the two.
            bool Tapers(int k, int a) => taper[a] > 0 && Half(a) > Half(a == k ? (k + 1) % n : k) + 0.01f;
        }
        var cuts = f.Sorted.Select((x, i) => new ArmCut(x.Arm.EdgeId, x.Arm.AtStart, cut[i]) { Squeezed = f.Squeezed[i] }).ToList();
        return new JunctionFootprint(nodeId, g.Node(nodeId).Position, cuts, curbs.Where(c => c is not null).Select(c => c!.Value).ToList(), Dedupe(outline))
            { Bend = n == 2 };
    }

    /// <summary>A Node junction's curbs and cut-backs, round the node: <c>Curbs[i]</c> and <c>CurbAt[i]</c> are between
    /// arm i and arm i + 1 (the stations along each where the curb touches), <c>Corner[i]</c> says they meet there (a
    /// curb, or a sharp corner when <c>Curbs[i]</c> is null).
    /// <c>Legs[i]</c>: for a curb, where its two sides' tangents meet as a station along each arm, and the tangent of half
    /// the angle there, so an end radius R starts it at <c>X + R / TanHalf</c>.</summary>
    private sealed record Layout(List<(Arm Arm, float Gap)> Sorted, ArmPath[] Paths, float[] Cut, Curb?[] Curbs, (float From, float To)[] CurbAt, bool[] Corner,
        (float XA, float XB, float TanHalf)?[] Legs, bool[] Squeezed);

    /// <summary>The curbs and cut-backs of a <see cref="JunctionKind.Node"/> junction with three or more arms, or a
    /// <see cref="IsBend"/> (see <see cref="Footprint"/>), null for any other node.</summary>
    private static Layout? Fit(SplineGraph g, int nodeId, IReadOnlyList<Arm> arms)
    {
        if ((arms.Count < 3 || KindOf(arms) != JunctionKind.Node) && !IsBend(arms)) return null;
        var sorted = Sorted(arms);
        int n = sorted.Count;
        var paths = sorted.Select(x => new ArmPath(g, x.Arm)).ToArray();
        var cut = new float[n];
        var curbs = new Curb?[n];
        var curbAt = new (float From, float To)[n];
        var corner = new bool[n];
        var legs = new (float XA, float XB, float TanHalf)?[n];

        for (int i = 0; i < n; i++)
        {
            var (a, gap) = sorted[i];
            int j = (i + 1) % n;
            var b = sorted[j].Arm;
            if (gap >= StraightGapDegrees) continue;
            var narrow = a.Rules.Width < b.Rules.Width || (a.Rules.Width == b.Rules.Width && a.Rules.KerbRadius <= b.Rules.KerbRadius) ? a.Rules : b.Rules;
            float r = MathF.Max(narrow.KerbRadius, 0);
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
            curbAt[i] = (hit.Value.SA, hit.Value.SB);
            corner[i] = true;
            if (r > 0.1f)
            {
                curbs[i] = new Curb(hit.Value.Point, r, paths[i].SidePoint(hit.Value.SA, +1, a.Rules.Width / 2), paths[j].SidePoint(hit.Value.SB, -1, b.Rules.Width / 2)) { Rules = narrow };
                // Back from the arc's ends along the sides by its tangent length (exact for straight arms).
                float cos = Math.Clamp(Vector2.Dot(paths[i].At(hit.Value.SA).Direction, paths[j].At(hit.Value.SB).Direction), -1f, 1f);
                float tanHalf = MathF.Sqrt((1 - cos) / MathF.Max(1 + cos, 1e-6f));
                if (tanHalf > 1e-3f) legs[i] = (hit.Value.SA - r / tanHalf, hit.Value.SB - r / tanHalf, tanHalf);
            }

            // A side pushed out further than the radius of a corner on it folds back on itself, and crosses the other
            // side somewhere bogus (a short arm into a tight corner cut back past the corner). A curb only counts where
            // its circle clears both roads, so then it shrinks until it does. The sides also run back behind the node
            // by the other road's width: a small kerb in a wide obtuse corner (a street leaving an avenue at 135°)
            // touches the avenue's side before it gets to the node.
            CurveHit? CurbCentre(float radius) =>
                FirstCross(paths[i].Side(+1, a.Rules.Width / 2 + radius, paths[i].CurbLimit, back: b.Rules.Width + radius),
                    paths[j].Side(-1, b.Rules.Width / 2 + radius, paths[j].CurbLimit, back: a.Rules.Width + radius),
                    accept: p => paths[i].Distance(p) >= a.Rules.Width / 2 + radius - CurbClearance
                        && paths[j].Distance(p) >= b.Rules.Width / 2 + radius - CurbClearance);
        }

        // Kerb knobs and road handles: a radius set at an arm's end for the curb on one side of it, each curb's other
        // end keeping its own, so a curb with two different ends is lopsided. Each end starts where its radius puts it
        // for the curb's own angle. A handle squeezed past what fits now (the road was edited since) is held at the arm's
        // curb limit; one that no longer makes a curb leaves the profile's.
        var set = sorted.Select(x => g.Edge(x.Arm.EdgeId).KerbAt(x.Arm.AtStart)).ToArray();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            // The curb between arm i and arm j is arm i's right-hand one and arm j's left-hand one.
            if (curbs[i] is not { } curb || legs[i] is not { } l || (set[i].Right is null && set[j].Left is null)) continue;
            float sa = set[i].Right is { } ri ? MathF.Min(l.XA + ri / l.TanHalf, paths[i].CurbLimit) : curbAt[i].From;
            float sb = set[j].Left is { } rj ? MathF.Min(l.XB + rj / l.TanHalf, paths[j].CurbLimit) : curbAt[i].To;
            if (Kerb(paths[i], paths[j], sorted[i].Arm.Rules.Width / 2, sorted[j].Arm.Rules.Width / 2, sa, sb) is not { } k) continue;
            // Rated as the handles see it (on a curved arm the conic's own tangents meet a little elsewhere).
            float ra = (sa - l.XA) * l.TanHalf, rb = (sb - l.XB) * l.TanHalf;
            curbs[i] = k with { Radius = MathF.Min(ra, rb), MaxRadius = MathF.Max(ra, rb), Rules = curb.Rules, Set = true };
            curbAt[i] = (sa, sb);
        }

        // Each arm is cut back to where its curbs (or sharp corners) start, keeping both ends of a short edge room.
        for (int i = 0; i < n; i++)
        {
            if (!corner[i]) continue;
            int j = (i + 1) % n;
            cut[i] = MathF.Max(cut[i], curbAt[i].From);
            cut[j] = MathF.Max(cut[j], curbAt[i].To);
        }
        var squeezed = new bool[n];
        for (int i = 0; i < n; i++)
        {
            squeezed[i] = cut[i] > paths[i].Cap + 1e-3f;
            cut[i] = Math.Clamp(cut[i], 0, paths[i].Cap);
        }
        return new Layout(sorted, paths, cut, curbs, curbAt, corner, legs, squeezed);
    }

    /// <summary>
    /// A curb from station <paramref name="sa"/> on arm a's side toward b to <paramref name="sb"/> on b's side toward a:
    /// the conic tangent to both sides there, through the point where their tangents meet. With both ends the same
    /// distance from that point it's the circular arc. Null when an end is at or behind that point (the curb would
    /// fold back).
    /// </summary>
    private static Curb? Kerb(ArmPath a, ArmPath b, float wa, float wb, float sa, float sb, int samples = 24) =>
        KerbAndFlare(a, b, wa, wb, sa, sb, samples)?.Curb;

    /// <summary><see cref="Kerb"/>, and how lopsided it is: its longer end over its shorter (1 = a circular arc).</summary>
    private static (Curb Curb, float Flare)? KerbAndFlare(ArmPath a, ArmPath b, float wa, float wb, float sa, float sb, int samples)
    {
        var da = a.At(sa).Direction;
        var db = b.At(sb).Direction;
        var p0 = a.SidePoint(sa, +1, wa);
        var p2 = b.SidePoint(sb, -1, wb);
        // p0 − u·da = p2 − v·db: back along each side toward the node, to where they meet.
        var d = p2 - p0;
        float den = SplineMath.Cross(-da, db);
        if (MathF.Abs(den) < 1e-6f) return null;
        float u = SplineMath.Cross(d, db) / den, v = SplineMath.Cross(-da, d) / den;
        if (u < MinKerbLeg || v < MinKerbLeg) return null;
        var control = p0 - da * u;
        float cos = Math.Clamp(Vector2.Dot(da, db), -1f, 1f); // of the angle between the sides at the control point
        float w = MathF.Sqrt((1 - cos) / 2); // its half angle's sine: a circle when both ends are as far
        float tanHalf = MathF.Sqrt((1 - cos) / MathF.Max(1 + cos, 1e-6f));
        // Each end rated as the arc it would make with the other end as far out: R = tangent length · tan(half angle).
        float ra = u * tanHalf, rb = v * tanHalf;
        var centre = ra <= rb ? p0 + TowardNext(da) * ra : p2 - TowardNext(db) * rb;
        return (new Curb(centre, MathF.Min(ra, rb), p0, p2) { MaxRadius = MathF.Max(ra, rb), Control = control, Weight = w }, MathF.Max(u, v) / MathF.Min(u, v));
    }

    private static List<Vector2> ConicPoints(Vector2 p0, Vector2 p1, Vector2 p2, float w, int n)
    {
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            float t = (float)k / n, a = (1 - t) * (1 - t), b = 2 * t * (1 - t) * w, c = t * t;
            pts.Add((p0 * a + p1 * b + p2 * c) / (a + b + c));
        }
        return pts;
    }

    /// <summary>
    /// The road handles of a Node junction (DESIGN.md → Junctions → Kerb handles): one per arm with a curb beside it,
    /// on its centre line where the outermost of those curbs starts, with the range of the factor it scales this arm's
    /// end of each of them by. The other arms are held as they are.
    /// </summary>
    public static List<KerbHandle> KerbHandles(SplineGraph g, int nodeId, bool anarchy)
    {
        var list = new List<KerbHandle>();
        var arms = g.Arms(nodeId);
        if (IsTransition(arms) || Fit(g, nodeId, arms) is not { } f) return list;
        int n = f.Sorted.Count;
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n;
            var sides = new List<KerbSide>();
            if (EndOf(f, prev, first: false) is { } left) sides.Add(left);
            if (EndOf(f, i, first: true) is { } right) sides.Add(right);
            if (sides.Count == 0) continue;
            var arm = f.Sorted[i].Arm;
            float cap = f.Paths[i].CurbLimit;
            // Each end within min..max, at most MaxFlare against its kerb's other end, and starting within the arm's limit.
            float lo = 0, hi = float.PositiveInfinity;
            var why = KerbLimit.Fit;
            foreach (var k in sides)
            {
                lo = MathF.Max(lo, MathF.Max((anarchy ? AnarchyKerb : k.Rules.MinKerbRadius) / k.Radius, k.Other / (MaxFlare * k.Radius)));
                Tighten(k.Rules.MaxKerbRadius / k.Radius, KerbLimit.Radius);
                Tighten(MaxFlare * k.Other / k.Radius, KerbLimit.Flare);
                Tighten((cap - k.X) * k.TanHalf / k.Radius, KerbLimit.Fit);
            }
            if (hi < lo) lo = hi = 1; // nothing fits both ways (an end already out of range): it stays as it is
            float StationOf(float x) => sides.Max(k => k.X + k.Radius * x / k.TanHalf);
            float station = StationOf(1), min = MathF.Min(StationOf(lo), station), max = MathF.Max(StationOf(MathF.Min(hi, 50)), station);
            var radii = sides.Select(k => (MathF.Min(k.Radius, k.Other), MathF.Max(k.Radius, k.Other))).ToList();
            list.Add(new KerbHandle(arm.EdgeId, arm.AtStart, f.Paths[i].At(station).Position, station, min, max, why,
                Track(f.Paths[i], min, max), radii, sides[0].Rules, g.Edge(arm.EdgeId).KerbAt(arm.AtStart).IsSet)
            { FactorMin = lo, FactorMax = hi, Sides = sides });

            void Tighten(float v, KerbLimit limit)
            {
                if (v >= hi) return;
                hi = v;
                why = limit;
            }
        }
        return list;
    }

    /// <summary>The kerb knobs of a Node junction: one per curb, in its middle, with the range its round radius may take.</summary>
    public static List<KerbKnob> KerbKnobs(SplineGraph g, int nodeId, bool anarchy)
    {
        var list = new List<KerbKnob>();
        var arms = g.Arms(nodeId);
        if (IsTransition(arms) || Fit(g, nodeId, arms) is not { } f) return list;
        int n = f.Sorted.Count;
        for (int i = 0; i < n; i++)
        {
            if (f.Curbs[i] is not { } curb || f.Legs[i] is not { } l || curb.Rules is not { } rules) continue;
            int j = (i + 1) % n;
            var (a, b) = (f.Sorted[i].Arm, f.Sorted[j].Arm);
            // The corner point and bisector from the legs: where the two sides' tangents meet, at the arm stations XA, XB.
            var corner = f.Paths[i].SidePoint(l.XA, +1, a.Rules.Width / 2);
            var bisector = Vector2.Normalize(f.Paths[i].At(l.XA).Direction + f.Paths[j].At(l.XB).Direction);
            float sinHalf = l.TanHalf / MathF.Sqrt(1 + l.TanHalf * l.TanHalf);
            float fit = MathF.Min((f.Paths[i].CurbLimit - l.XA) * l.TanHalf, (f.Paths[j].CurbLimit - l.XB) * l.TanHalf);
            float min = anarchy ? AnarchyKerb : rules.MinKerbRadius, max = MathF.Max(min, MathF.Min(rules.MaxKerbRadius, fit));
            var pts = ArcPoints(curb);
            bool set = g.Edge(a.EdgeId).KerbAt(a.AtStart).Right is not null || g.Edge(b.EdgeId).KerbAt(b.AtStart).Left is not null;
            list.Add(new KerbKnob(a.EdgeId, a.AtStart, b.EdgeId, b.AtStart, pts[pts.Count / 2], corner, bisector, sinHalf, min, max,
                fit < rules.MaxKerbRadius ? KerbLimit.Fit : KerbLimit.Radius, (curb.Radius, curb.MaxRadius), rules, set));
        }
        return list;
    }

    /// <summary>One end of curb <paramref name="pair"/> as a road handle sees it: at its first arm (the arm's right-hand
    /// curb) or its second (left-hand), with its radius there and at the other end.</summary>
    private static KerbSide? EndOf(Layout f, int pair, bool first)
    {
        if (f.Curbs[pair] is not { Rules: { } rules } || f.Legs[pair] is not { } l) return null;
        float ra = (f.CurbAt[pair].From - l.XA) * l.TanHalf, rb = (f.CurbAt[pair].To - l.XB) * l.TanHalf;
        if (ra <= 0 || rb <= 0) return null;
        return first ? new KerbSide(+1, l.XA, l.TanHalf, ra, rb, rules) : new KerbSide(-1, l.XB, l.TanHalf, rb, ra, rules);
    }

    private static List<Vector2> Track(ArmPath path, float min, float max)
    {
        var track = new List<Vector2>();
        for (float s = min; ; s += 1f)
        {
            s = MathF.Min(s, max);
            track.Add(path.At(s).Position);
            if (s >= max) break;
        }
        return track;
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
            CurbLimit = Cap;
            // Up to the end of the first corner that turns ahead of the node (not one the node sits inside: a junction
            // on a curve fits its curbs along it).
            var al = e.Alignment;
            int count = al.Pis.Count;
            for (int k = 1; k < count - 1; k++)
            {
                int i = arm.AtStart ? k : count - 1 - k;
                var legIn = al.Pis[i].Position - al.Pis[i - 1].Position;
                var legOut = al.Pis[i + 1].Position - al.Pis[i].Position;
                if (legIn.Length() < SplineMath.Epsilon || legOut.Length() < SplineMath.Epsilon
                    || MathF.Abs(SplineMath.Turn(Vector2.Normalize(legIn), Vector2.Normalize(legOut))) < MathF.PI / 180) continue;
                var (s0, s1) = al.CornerStations(i);
                if ((arm.AtStart ? s0 : al.Length - s1) < 1f) continue;
                CurbLimit = MathF.Min(Cap, arm.AtStart ? s1 : al.Length - s0);
                break;
            }
        }

        public float Length => _curve.Length;

        /// <summary>How far a point is from the arm's whole edge (its centre line), run on straight behind the node.</summary>
        public float Distance(Vector2 p)
        {
            var (node, d) = At(0);
            if (Vector2.Dot(p - node, d) < 0) return MathF.Abs(SplineMath.Cross(d, p - node));
            return Vector2.Distance(_curve.ClosestPoint(p).Position, p);
        }

        /// <summary>The furthest this arm can be cut back: a share of its edge, leaving room for a footprint at the
        /// other end if there is one.</summary>
        public float Cap { get; }

        /// <summary>How far along the arm a curb may touch: not past the end of its first corner, so a curb never takes a
        /// short arm round the corner onto the road beyond and cuts the corner away (it shrinks to fit instead).</summary>
        public float CurbLimit { get; }

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
        /// straight on behind the node and past <paramref name="max"/>; with <paramref name="back"/>, that far behind
        /// the node only.</summary>
        public List<(float S, Vector2 P)> Side(int side, float offset, float? max = null, float reach = 0, float back = 0)
        {
            float to = max ?? Cap;
            var pts = new List<(float, Vector2)>();
            back = MathF.Max(back, reach);
            if (back > 0) pts.Add((-back, SidePoint(-back, side, offset)));
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
    private static CurveHit? FirstCross(List<(float S, Vector2 P)> a, List<(float S, Vector2 P)> b, bool nearest = false,
        Func<Vector2, bool>? accept = null)
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
                if (best is not null && key(sa, sb) >= key(best.Value.SA, best.Value.SB)) continue;
                if (accept?.Invoke(a0 + da * t) == false) continue;
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
    /// Null for other nodes, for two arms running straight through and for a <see cref="IsBend"/> (it has a footprint).
    /// </summary>
    public static IReadOnlyList<Vector2>? BendFill(SplineGraph g, int nodeId, int n = 12)
    {
        var arms = g.Arms(nodeId);
        if (arms.Count != 2 || IsBend(arms)) return null;
        var sorted = Sorted(arms);
        int i = sorted[0].Gap >= sorted[1].Gap ? 0 : 1; // the outside is the wider gap
        var (a, gap) = sorted[i];
        var b = sorted[1 - i].Arm;
        if (gap < 360f - StraightGapDegrees) return null;
        float ra = a.Rules.Width / 2, rb = b.Rules.Width / 2;
        if (IsTransition(arms)) ra = rb = MathF.Min(ra, rb); // the wider arm has tapered down to the narrower by here
        return RoundOutside(g.Node(nodeId).Position, a.Direction, gap, ra, rb, n);
    }

    /// <summary>The outside of two arms meeting at a node with <paramref name="gap"/> degrees (≥ 180) between them: an
    /// arc round the node from the first arm's side toward the next (<paramref name="ra"/> off its centre line) to the
    /// next arm's side (<paramref name="rb"/>), its radius going from one to the other. Just the two side points when
    /// they run straight through.</summary>
    private static List<Vector2> RoundOutside(Vector2 centre, Vector2 direction, float gap, float ra, float rb, int n = 12)
    {
        float from = SplineMath.Angle(direction) + MathF.PI / 2, sweep = MathF.Max(0, gap - 180f) * MathF.PI / 180f;
        if (sweep < 1e-3f) n = 1;
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

    /// <summary>Points along a curb arc, from its <see cref="Curb.From"/> to its <see cref="Curb.To"/> the short way (a
    /// lopsided one along its conic).</summary>
    public static List<Vector2> ArcPoints(Curb c, int n = 12)
    {
        if (c.Control is { } control) return ConicPoints(c.From, control, c.To, c.Weight, n * 2);
        float a0 = SplineMath.Angle(c.From - c.Centre);
        float sweep = SplineMath.Wrap(SplineMath.Angle(c.To - c.Centre) - a0);
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++) pts.Add(c.Centre + SplineMath.Direction(a0 + sweep * k / n) * c.Radius);
        return pts;
    }

    /// <summary>A bend's outside: its inside kerb pushed out by <paramref name="width"/> (away from the kerb's centre),
    /// run the other way, from the kerb's second arm to its first.</summary>
    private static List<Vector2> Outside(Curb c, float width)
    {
        var pts = ArcPoints(c);
        var result = new List<Vector2>(pts.Count);
        for (int k = 0; k < pts.Count; k++)
        {
            var t = Vector2.Normalize(pts[Math.Min(k + 1, pts.Count - 1)] - pts[Math.Max(k - 1, 0)]);
            var normal = new Vector2(-t.Y, t.X);
            if (Vector2.Dot(normal, pts[k] - c.Centre) < 0) normal = -normal;
            result.Add(pts[k] + normal * width);
        }
        result.Reverse();
        return result;
    }

    /// <summary>The unit normal on the side of an arm where the next arm (by heading) lies.</summary>
    private static Vector2 TowardNext(Vector2 dir) => new(-dir.Y, dir.X);
}
