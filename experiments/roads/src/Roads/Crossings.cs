using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;

namespace CitySim.Roads;

/// <summary>What the player set for the crossing on one arm of a node: <see cref="Auto"/> follows the rules
/// (<see cref="Crossings"/>), <see cref="Yes"/> and <see cref="No"/> are set with the Crossings tool and stay.</summary>
public enum CrossingMode { Auto, Yes, No }

/// <summary>What a road keeps at each end of an edge (the splines graph's <see cref="GraphEdge.DataStart"/> /
/// <see cref="GraphEdge.DataEnd"/>): kept through splits, merges, edits and undo with the end it belongs to.</summary>
public sealed record RoadEnd(CrossingMode Crossing);

/// <summary>
/// The markings at one end of an edge, as stations from where the road starts there (its junction cut-back, or the
/// node): a zebra from <see cref="ZebraFrom"/> for the crossing's width, and a stop line at <see cref="StopAt"/> across the
/// lanes coming in. <see cref="Junction"/>: the end meets a junction (queues and wear build up behind it).
/// </summary>
public readonly record struct EndMarks(bool Zebra, float ZebraFrom, float? StopAt, bool Junction)
{
    /// <summary>Where lane lines stop: behind the stop line (0 with none).</summary>
    public float LinesFrom(SectionStyle st) => StopAt is { } s ? s + st.StopLineWidth : 0;
}

/// <summary>
/// Which arms get a zebra crossing. Every arm of a node is a place for one, as is any point along a road (the
/// Crossings tool splits the road there, so it's an arm of a new node). An arm's <see cref="CrossingMode"/> decides:
/// <list type="bullet">
/// <item><b>Auto</b>: a crossing on every arm of a junction (3+ roads); none anywhere else (a road's own nodes, a
/// change of road, a dead end). Then two rules: an auto crossing closer than <see cref="SectionStyle.CrossingMinGap"/>
/// along the road to another crossing is dropped (the forced one, or the busier junction's, stays), and one that
/// doesn't fit between the junctions isn't drawn.</item>
/// <item><b>Yes</b>: always a crossing, if it fits.</item>
/// <item><b>No</b>: never.</item>
/// </list>
/// Only paved roads with sidewalks get crossings. A junction's arm has a stop line with or without one; a crossing
/// anywhere else brings stop lines for the lanes coming at it from both sides.
/// </summary>
public static class Crossings
{
    public static CrossingMode ModeOf(GraphEdge e, bool atStart) => e.DataAt(atStart) is RoadEnd r ? r.Crossing : CrossingMode.Auto;

    /// <summary>Whether the arm is at a junction (a footprint, not just a change of road width or a corner).</summary>
    public static bool AtJunction(GraphEdge e, bool atStart, IReadOnlyDictionary<int, JunctionFootprint> footprints) =>
        footprints.TryGetValue(atStart ? e.Start : e.End, out var f) && !f.Continuous && !f.Bend;

    /// <summary>Where the road starts at an arm's end (the junction's cut-back, else the node).</summary>
    public static float CutOf(GraphEdge e, bool atStart, IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var (c0, c1) = Junctions.CutBacks(e, footprints);
        return atStart ? c0 : c1;
    }

    /// <summary>Where an arm's zebra starts, from its cut.</summary>
    public static float ZebraFrom(bool junction, SectionStyle st) => junction ? st.CrossingSetback : 0;

    /// <summary>Whether a road can have crossings: paved lanes and sidewalks to cross between.</summary>
    public static bool Crossable(RoadSection sec) =>
        sec.HasSidewalks && sec.Bands.Any(b => b.Lanes && b.Surface == SurfaceKind.Asphalt);

    /// <summary>The markings at every end of every edge that has some, by (edge, at start).</summary>
    public static Dictionary<(int Edge, bool AtStart), EndMarks> Resolve(SplineGraph g,
        IReadOnlyDictionary<int, JunctionFootprint> footprints, Func<GraphEdge, RoadSection> sectionOf, SectionStyle st)
    {
        bool Paved(GraphEdge e) => sectionOf(e).Bands.Any(b => b.Lanes && b.Surface == SurfaceKind.Asphalt);
        float Free(GraphEdge e)
        {
            var (c0, c1) = Junctions.CutBacks(e, footprints);
            return e.Alignment.Length - c0 - c1;
        }

        // Every arm that wants a crossing, strongest claim first: forced ones, then the busier junction's.
        var wants = new List<(GraphEdge Edge, bool AtStart, bool Forced, int Arms, int Node)>();
        foreach (var e in g.Edges)
            foreach (bool atStart in new[] { true, false })
            {
                int node = atStart ? e.Start : e.End;
                int arms = g.Node(node).Edges.Count;
                if (arms < 2 || !Crossable(sectionOf(e))) continue;
                var mode = ModeOf(e, atStart);
                if (mode == CrossingMode.Yes || (mode == CrossingMode.Auto && AtJunction(e, atStart, footprints)))
                    wants.Add((e, atStart, mode == CrossingMode.Yes, arms, node));
            }

        var zebras = new HashSet<(int, bool)>();
        foreach (var w in wants.OrderByDescending(w => w.Forced).ThenByDescending(w => w.Arms).ThenBy(w => w.Node))
        {
            if (Need(w.Edge, w.AtStart, true) + Need(w.Edge, !w.AtStart, zebras.Contains((w.Edge.Id, !w.AtStart))) > Free(w.Edge)) continue;
            if (!w.Forced && NearestAlong(w.Edge, w.AtStart) < st.CrossingMinGap) continue;
            zebras.Add((w.Edge.Id, w.AtStart));
        }

        var marks = new Dictionary<(int, bool), EndMarks>();
        foreach (var e in g.Edges)
        {
            if (!Paved(e)) continue;
            foreach (bool atStart in new[] { true, false })
            {
                if (Marks(e, atStart) is not { } m) continue;
                // A stop line for a crossing on the next arm that doesn't fit this one's road is left out.
                if (m.LinesFrom(st) + Need(e, !atStart, zebras.Contains((e.Id, !atStart))) > Free(e) && !m.Zebra && !m.Junction) continue;
                marks[(e.Id, atStart)] = m;
            }
        }
        return marks;

        EndMarks? Marks(GraphEdge e, bool atStart)
        {
            bool junction = AtJunction(e, atStart, footprints);
            bool zebra = zebras.Contains((e.Id, atStart));
            float from = ZebraFrom(junction, st);
            if (zebra) return new EndMarks(true, from, from + st.CrossingWidth + st.StopLineGap, junction);
            if (junction) return new EndMarks(false, from, st.CrossingSetback, true);
            // Off a junction: stop for a crossing on the other arm across the node.
            int node = atStart ? e.Start : e.End;
            bool across = g.Arms(node).Any(a => !(a.EdgeId == e.Id && a.AtStart == atStart) && zebras.Contains((a.EdgeId, a.AtStart)));
            return across ? new EndMarks(false, 0, st.StopLineGap, false) : null;
        }

        // Metres an end's markings take from its edge's free length.
        float Need(GraphEdge e, bool atStart, bool zebra)
        {
            bool junction = AtJunction(e, atStart, footprints);
            if (zebra) return ZebraFrom(junction, st) + st.CrossingWidth + st.StopLineGap + st.StopLineWidth;
            return junction ? st.CrossingSetback + st.StopLineWidth : 0;
        }

        // Metres along the road from this arm's zebra to the nearest zebra ahead of it: on to the edge's other end, and
        // on through nodes that aren't junctions (a road's own node, a change of road). Not across a junction, whose
        // other arms are other roads.
        float NearestAlong(GraphEdge e, bool atStart)
        {
            float centre = st.CrossingSetback + st.CrossingWidth / 2; // an auto crossing is always at a junction
            float run = e.Alignment.Length - CutOf(e, atStart, footprints) - centre;
            var (edge, far) = (e, !atStart);
            var seen = new HashSet<int> { e.Id };
            while (true)
            {
                float cut = CutOf(edge, far, footprints);
                if (zebras.Contains((edge.Id, far))) return run - cut - ZebraFrom(AtJunction(edge, far, footprints), st) - st.CrossingWidth / 2;
                int node = far ? edge.Start : edge.End;
                if (AtJunction(edge, far, footprints) || run >= st.CrossingMinGap) break;
                var next = g.Arms(node).Where(a => a.EdgeId != edge.Id || a.AtStart != far).ToList();
                if (next.Count != 1 || !seen.Add(next[0].EdgeId)) break;
                // A crossing on the next arm, at this node.
                if (zebras.Contains((next[0].EdgeId, next[0].AtStart))) return run + st.CrossingWidth / 2;
                edge = g.Edge(next[0].EdgeId);
                far = !next[0].AtStart;
                run += edge.Alignment.Length;
            }
            return float.PositiveInfinity;
        }
    }
}
