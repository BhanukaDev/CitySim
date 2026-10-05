using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Junctions too close to fit apart, drawn as one (DESIGN.md → Junctions → Clusters): <see cref="Nodes"/> and the
/// <see cref="Inner"/> edges between them, which have no road of their own left. <see cref="Arms"/>: every other edge end
/// at those nodes, the roads leading in, cut back as their node's footprint cuts them. The consumer draws the lot as one
/// paved area (the space the inner edges enclose is an island) instead of each footprint and inner edge on its own.
/// </summary>
public sealed record JunctionCluster(IReadOnlyList<int> Nodes, IReadOnlyList<int> Inner, IReadOnlyList<ArmCut> Arms)
{
    public bool Has(int nodeId) => Nodes.Contains(nodeId);
}

public static class JunctionClusters
{
    /// <summary>Two junctions on one edge with less than this much of it left between their cut-backs are one.</summary>
    private const float MinRun = 1f;
    /// <summary>An edge longer than this many times its width is a road of its own, even between squeezed junctions.</summary>
    private const float MaxInnerWidths = 4f;

    /// <summary>
    /// The clusters among some footprints: junctions (not width transitions) joined by an edge whose footprints overlap,
    /// leave less than <see cref="MinRun"/> of it between them, or either is squeezed on it (<see cref="ArmCut.Squeezed"/>:
    /// a corner that doesn't fit, such as a road joining another at a sharp angle near a junction). That's what the
    /// footprints can't fix on their own: a corner of one reaching into the other's road (two junctions a few metres
    /// apart, a road crossing two arms of a junction near it). An edge more than <see cref="MaxInnerWidths"/> times its
    /// width long only joins them if they overlap.
    /// </summary>
    public static List<JunctionCluster> Find(SplineGraph g, IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var parent = new Dictionary<int, int>();
        int Root(int n)
        {
            while (parent[n] != n) n = parent[n];
            return n;
        }
        var inner = new List<GraphEdge>();
        foreach (var e in g.Edges)
        {
            if (e.Start == e.End || !footprints.TryGetValue(e.Start, out var fa) || !footprints.TryGetValue(e.End, out var fb)) continue;
            if (fa.Continuous || fb.Continuous) continue;
            float run = e.Alignment.Length - fa.CutBack(e.Id, true) - fb.CutBack(e.Id, false);
            bool squeezed = e.Alignment.Length <= MaxInnerWidths * e.Rules.Width
                && (fa.Cuts.Any(c => c.EdgeId == e.Id && c.AtStart && c.Squeezed) || fb.Cuts.Any(c => c.EdgeId == e.Id && !c.AtStart && c.Squeezed));
            if (run >= MinRun && !squeezed && !Overlap(fa.Outline, fb.Outline)) continue;
            inner.Add(e);
            parent.TryAdd(e.Start, e.Start);
            parent.TryAdd(e.End, e.End);
            var (ra, rb) = (Root(e.Start), Root(e.End));
            if (ra != rb) parent[ra] = rb;
        }
        // An edge between two nodes of one cluster is inner too, even if its own footprints didn't overlap.
        var clusters = new List<JunctionCluster>();
        foreach (var group in parent.Keys.GroupBy(Root))
        {
            var nodes = group.OrderBy(n => n).ToList();
            var set = nodes.ToHashSet();
            var edges = g.Edges.Where(e => e.Start != e.End && set.Contains(e.Start) && set.Contains(e.End)).Select(e => e.Id).ToList();
            var arms = nodes.SelectMany(n => footprints[n].Cuts).Where(c => !edges.Contains(c.EdgeId)).ToList();
            clusters.Add(new JunctionCluster(nodes, edges, arms));
        }
        return clusters;
    }

    /// <summary>Whether two simple polygons overlap: their outlines cross, or one has a point inside the other.</summary>
    public static bool Overlap(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        var (amin, amax) = Bounds(a);
        var (bmin, bmax) = Bounds(b);
        if (amax.X < bmin.X || bmax.X < amin.X || amax.Y < bmin.Y || bmax.Y < amin.Y) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var (p0, p1) = (a[i], a[(i + 1) % a.Count]);
            for (int j = 0; j < b.Count; j++)
                if (Cross(p0, p1, b[j], b[(j + 1) % b.Count])) return true;
        }
        return Inside(a[0], b) || Inside(b[0], a);
    }

    private static (Vector2 Min, Vector2 Max) Bounds(IReadOnlyList<Vector2> p) =>
        (new Vector2(p.Min(v => v.X), p.Min(v => v.Y)), new Vector2(p.Max(v => v.X), p.Max(v => v.Y)));

    /// <summary>Whether two segments cross properly (touching or running along each other doesn't count).</summary>
    private static bool Cross(Vector2 p0, Vector2 p1, Vector2 q0, Vector2 q1)
    {
        const float eps = 1e-3f;
        float d1 = SplineMath.Cross(p1 - p0, q0 - p0), d2 = SplineMath.Cross(p1 - p0, q1 - p0);
        float d3 = SplineMath.Cross(q1 - q0, p0 - q0), d4 = SplineMath.Cross(q1 - q0, p1 - q0);
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    private static bool Inside(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }
}
