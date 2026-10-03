using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>Warn: amber, still builds. Invalid: red, builds only with Anarchy (and stays red once built).</summary>
public enum Severity { Warn, Invalid }

/// <summary>One validation result, in plain words with the fix (DESIGN.md → Validation), and where it is.</summary>
public sealed record Issue(Severity Severity, string Code, string Message, Vector2 Where, int? EdgeId = null, int? NodeId = null);

/// <summary>
/// The checks (DESIGN.md → Validation): a corner built below the profile's minimum radius (Invalid, whether it was
/// asked for or clamped to fit; a clamp above the minimum isn't an issue, the corner just takes what fits), a
/// junction angle below the minimum (Warn), a square branch off a turnout profile (Invalid), a crossing or overlap
/// with an edge it doesn't connect to (Invalid), crossing itself (Invalid), and an edge too short for the junctions
/// at its ends (Warn). Severity is the same with or without Anarchy: Anarchy only lets Invalid build. Grade comes
/// with S8. Plain loops over every edge; S11 adds a spatial index.
/// </summary>
public static class Validation
{
    private const float SampleSpacing = 2f;
    /// <summary>Corridors may touch (a parallel road at a 0 m gap); only a real overlap counts.</summary>
    private const float OverlapSlack = 0.1f;

    /// <summary>Every issue in the graph.</summary>
    public static List<Issue> Check(SplineGraph g) => Check(g, g.Edges.Select(e => e.Id), g.Nodes.Select(n => n.Id));

    /// <summary>The issues of some edges and nodes (the draw preview checks only what the new spline touches).</summary>
    public static List<Issue> Check(SplineGraph g, IEnumerable<int> edges, IEnumerable<int> nodes)
    {
        var issues = new List<Issue>();
        var footprints = Junctions.Footprints(g);
        foreach (int id in edges) CheckEdge(g, g.Edge(id), footprints, issues);
        foreach (int id in nodes) CheckNode(g, id, issues);
        return issues;
    }

    public static Severity? Worst(IEnumerable<Issue> issues) =>
        issues.Select(i => (Severity?)i.Severity).DefaultIfEmpty(null).Max();

    private static void CheckEdge(SplineGraph g, GraphEdge e, IReadOnlyDictionary<int, JunctionFootprint> footprints, List<Issue> issues)
    {
        var a = e.Alignment;
        var rules = e.Rules;
        for (int i = 1; i < a.Pis.Count - 1; i++)
        {
            var c = a.Corner(i);
            if (a.Pis[i].Hard || c.Radius <= 0) continue;
            if (c.Radius < rules.MinRadius - 1e-2f)
                issues.Add(new Issue(Severity.Invalid, "radius-min", $"R {c.Radius:0} m, min {rules.MinRadius:0} m · Ctrl+A allows", c.Mid, e.Id));
        }

        // A loop edge's two ends meet at its node; that's not a crossing.
        foreach (var hit in a.Curve.SelfIntersect())
            if (!(hit.SA < SplineGraph.NodeTolerance && hit.SB > a.Length - SplineGraph.NodeTolerance))
                issues.Add(new Issue(Severity.Invalid, "self-cross", "crosses itself", hit.Point, e.Id));

        // A dead end left lying on its own road (dropped beside it, not joined to it) overlaps it. Only the road further
        // along than twice its width counts, so a tight corner next to the end isn't an overlap.
        foreach (var (s, node) in new[] { (0f, e.Start), (a.Length, e.End) })
        {
            if (g.Node(node).Edges.Count != 1) continue;
            var end = a.Curve.Sample(s).Position;
            float limit = rules.Width - OverlapSlack;
            if (a.Curve.SampleEvery(SampleSpacing).Any(x => MathF.Abs(x.S - s) > 2 * rules.Width
                && Vector2.Distance(x.Sample.Position, end) < limit))
            {
                issues.Add(new Issue(Severity.Invalid, "overlap", "overlaps itself", end, e.Id, node));
            }
        }

        var (cutStart, cutEnd) = Junctions.CutBacks(e, footprints);
        if (cutStart + cutEnd > 0 && a.Length < cutStart + cutEnd + 1f)
            issues.Add(new Issue(Severity.Warn, "short", "too short for its junctions", a.Curve.Sample(a.Length / 2).Position, e.Id));

        foreach (var other in g.Edges)
        {
            if (other.Id == e.Id) continue;
            if (Shares(e, other))
            {
                // Joined at a junction, so their corridors meet there; only leaving it along the same line (drawn
                // over an existing road) is an overlap. A turnout's branch does that on purpose.
                if (RunsAlong(g, e, other) is { } along)
                    issues.Add(new Issue(Severity.Invalid, "overlap", $"overlaps {other.Rules.Id}", along, e.Id));
                continue;
            }
            if (a.Curve.Intersect(other.Alignment.Curve) is { Count: > 0 } hits)
            {
                issues.Add(new Issue(Severity.Invalid, "crossing", $"crosses {other.Rules.Id} · not connected", hits[0].Point, e.Id));
                continue;
            }
            if (Overlap(e, other) is { } at)
                issues.Add(new Issue(Severity.Invalid, "overlap", $"overlaps {other.Rules.Id}", at, e.Id));
        }
    }

    private static void CheckNode(SplineGraph g, int nodeId, List<Issue> issues)
    {
        var arms = g.Arms(nodeId);
        if (arms.Count < 2) return;
        var at = g.Node(nodeId).Position;
        switch (Junctions.KindOf(arms))
        {
            case JunctionKind.Node:
            {
                float min = arms.Where(x => x.Rules.JunctionKind == JunctionKind.Node).Max(x => x.Rules.MinJunctionAngle);
                if (Junctions.SmallestGap(arms) is { } gap && gap < min - 0.5f)
                    issues.Add(new Issue(Severity.Warn, "junction-angle", $"{gap:0}°, min {min:0}° · Ctrl+A allows", at, NodeId: nodeId));
                break;
            }
            case JunctionKind.Turnout:
                foreach (var (arm, deg) in Junctions.TurnoutViolations(arms))
                    issues.Add(new Issue(Severity.Invalid, "turnout", $"{deg:0}° not allowed", at, arm.EdgeId, nodeId));
                break;
        }
    }

    private static bool Shares(GraphEdge a, GraphEdge b) =>
        a.Start == b.Start || a.Start == b.End || a.End == b.Start || a.End == b.End;

    /// <summary>Where two edges leave a shared (non-turnout) node in the same direction, if they do.</summary>
    private static Vector2? RunsAlong(SplineGraph g, GraphEdge a, GraphEdge b)
    {
        foreach (int n in new[] { a.Start, a.End }.Distinct())
        {
            if (n != b.Start && n != b.End) continue;
            var arms = g.Arms(n);
            if (Junctions.KindOf(arms) == JunctionKind.Turnout) continue;
            foreach (var x in arms.Where(x => x.EdgeId == a.Id))
                foreach (var y in arms.Where(y => y.EdgeId == b.Id))
                    if (Vector2.Dot(x.Direction, y.Direction) > 0.9998f) return g.Node(n).Position + x.Direction * (a.Rules.Width / 2);
        }
        return null;
    }

    /// <summary>A point where two corridors overlap (centre lines closer than their half widths added), if any.</summary>
    private static Vector2? Overlap(GraphEdge a, GraphEdge b)
    {
        float limit = (a.Rules.Width + b.Rules.Width) / 2 - OverlapSlack;
        if (limit <= 0) return null;
        foreach (var (_, s) in a.Alignment.Curve.SampleEvery(SampleSpacing))
        {
            var cp = b.Alignment.Curve.ClosestPoint(s.Position);
            if (MathF.Abs(cp.Offset) < limit && Vector2.Distance(cp.Position, s.Position) < limit) return s.Position;
        }
        return null;
    }
}
