using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>A junction or an end: a position and the edges meeting there (a loop edge is listed twice).</summary>
public sealed class GraphNode
{
    public GraphNode(int id, Vector2 position) { Id = id; Position = position; }

    public int Id { get; }
    public Vector2 Position { get; }
    public List<int> Edges { get; } = new();
}

/// <summary>
/// An edge between two nodes: its alignment (treated as immutable once in the graph: operations replace it), the
/// profile it was drawn with, and the consumer's <see cref="CustomData"/>, which the graph copies across split and
/// merge but never reads.
/// </summary>
public sealed record GraphEdge(int Id, ProfileRules Rules, Alignment Alignment, int Start, int End)
{
    public object? CustomData { get; init; }
}

/// <summary>One edge leaving a node: which end of the edge is at the node, and the unit tangent pointing away.</summary>
public readonly record struct Arm(int EdgeId, bool AtStart, Vector2 Direction, ProfileRules Rules);

/// <summary>What <see cref="SplineGraph.AddSpline"/> made: the new edges in draw order, and every node the new
/// spline touches (its ends and its junctions).</summary>
public sealed record AddResult(IReadOnlyList<int> Edges, IReadOnlyList<int> Nodes);

/// <summary>
/// The spline network (DESIGN.md → Graph): nodes and edges, each edge owning one alignment. Adding a spline splits it
/// and the edges it ends on or crosses into junctions, where the profiles connect (<see cref="Connects"/>); removing
/// an edge merges the two edges left at a node when they run straight through with the same profile. Plain lists and
/// linear searches (S11 adds a spatial index). <see cref="Clone"/> is cheap enough for the draw preview and the undo
/// history, since alignments are shared, never changed in place.
/// </summary>
public sealed class SplineGraph
{
    /// <summary>Points closer than this are the same node.</summary>
    public const float NodeTolerance = 0.5f;
    /// <summary>A spline end this close to an edge's centre line ends on that edge.</summary>
    public const float EdgeTolerance = 0.25f;
    /// <summary>Two edges meet straight through (and may merge) when their arms are within this of opposite.</summary>
    public const float StraightDegrees = 1f;

    private readonly Dictionary<int, GraphNode> _nodes = new();
    private readonly Dictionary<int, GraphEdge> _edges = new();
    private int _nextNode = 1, _nextEdge = 1;

    public IEnumerable<GraphNode> Nodes => _nodes.Values;
    public IEnumerable<GraphEdge> Edges => _edges.Values;
    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;
    public GraphNode Node(int id) => _nodes[id];
    public GraphEdge Edge(int id) => _edges[id];
    public bool HasEdge(int id) => _edges.ContainsKey(id);
    public bool HasNode(int id) => _nodes.ContainsKey(id);

    public SplineGraph Clone()
    {
        var g = new SplineGraph { _nextNode = _nextNode, _nextEdge = _nextEdge };
        foreach (var (id, n) in _nodes)
        {
            var copy = new GraphNode(id, n.Position);
            copy.Edges.AddRange(n.Edges);
            g._nodes[id] = copy;
        }
        foreach (var (id, e) in _edges) g._edges[id] = e;
        return g;
    }

    /// <summary>
    /// Whether two profiles make junctions together: each has to accept the other (its own id, or one listed in its
    /// <see cref="ProfileRules.ConnectsTo"/>), and neither may be <see cref="JunctionKind.None"/>.
    /// </summary>
    public static bool Connects(ProfileRules a, ProfileRules b)
    {
        if (a.JunctionKind == JunctionKind.None || b.JunctionKind == JunctionKind.None) return false;
        return Accepts(a, b) && Accepts(b, a);

        static bool Accepts(ProfileRules x, ProfileRules y) => x.Id == y.Id || x.ConnectsTo.Contains(y.Id);
    }

    /// <summary>The edges leaving a node, each with its tangent pointing away from the node.</summary>
    public List<Arm> Arms(int nodeId)
    {
        var arms = new List<Arm>();
        var seen = new HashSet<int>();
        foreach (int id in _nodes[nodeId].Edges)
        {
            var e = _edges[id];
            // A loop edge is listed twice: its start the first time, its end the second.
            bool atStart = e.Start == nodeId && seen.Add(id);
            var c = e.Alignment.Curve;
            var dir = atStart ? c.Sample(0).Tangent : -c.Sample(c.Length).Tangent;
            arms.Add(new Arm(id, atStart, dir, e.Rules));
        }
        return arms;
    }

    /// <summary>The node within <see cref="NodeTolerance"/> of a point, if any.</summary>
    public int? NodeAt(Vector2 p)
    {
        int? best = null;
        float bestDist = NodeTolerance;
        foreach (var n in _nodes.Values)
        {
            float d = Vector2.Distance(n.Position, p);
            if (d > bestDist) continue;
            bestDist = d;
            best = n.Id;
        }
        return best;
    }

    // --- Adding ---

    private readonly record struct Cut(float S, Vector2 Point, int? Node, int? Edge, float EdgeS);

    /// <summary>
    /// Adds a drawn spline. Its ends join a node or split an edge they land on, and every crossing with an edge splits
    /// both, wherever the two profiles <see cref="Connects"/>. Where they don't, nothing joins (validation reports the
    /// crossing or overlap).
    /// </summary>
    public AddResult AddSpline(Alignment alignment, ProfileRules rules)
    {
        var curve = alignment.Curve;
        float length = curve.Length;
        var cuts = new List<Cut>();

        foreach (float s in new[] { 0f, length })
        {
            var p = curve.Sample(s).Position;
            if (NodeAt(p) is { } node)
            {
                if (_nodes[node].Edges.All(id => Connects(rules, _edges[id].Rules))) cuts.Add(new Cut(s, p, node, null, 0));
                continue;
            }
            if (ClosestEdge(p, EdgeTolerance) is { } hit && Connects(rules, hit.Edge.Rules))
                cuts.Add(OnEdge(s, p, hit.Edge, hit.S));
        }

        foreach (var e in _edges.Values.ToList())
        {
            if (!Connects(rules, e.Rules)) continue;
            foreach (var hit in curve.Intersect(e.Alignment.Curve))
            {
                if (hit.SA < NodeTolerance || hit.SA > length - NodeTolerance) continue; // the ends are handled above
                cuts.Add(NodeAt(hit.Point) is { } node ? new Cut(hit.SA, hit.Point, node, null, 0) : OnEdge(hit.SA, hit.Point, e, hit.SB));
            }
        }

        // One cut per place along the new spline (a crossing through a junction hits each of its edges): nodes first.
        cuts = cuts.OrderBy(c => c.S).ThenBy(c => c.Node is null).ToList();
        for (int i = cuts.Count - 1; i > 0; i--)
            if (cuts[i].S - cuts[i - 1].S < NodeTolerance) cuts.RemoveAt(i);

        // Split the existing edges, highest station first so the lower ones stay on the first piece.
        var nodeOfCut = new Dictionary<int, int>();
        foreach (var group in cuts.Select((c, i) => (c, i)).Where(x => x.c.Edge is not null).GroupBy(x => x.c.Edge!.Value))
        {
            int edgeId = group.Key;
            foreach (var (c, i) in group.OrderByDescending(x => x.c.EdgeS))
            {
                var (node, left, _) = SplitEdge(edgeId, c.EdgeS);
                nodeOfCut[i] = node;
                edgeId = left ?? edgeId;
            }
        }
        for (int i = 0; i < cuts.Count; i++)
            if (cuts[i].Node is { } n) nodeOfCut[i] = n;

        // Split the new spline at its interior cuts.
        var nodes = new List<int>();
        var edges = new List<int>();
        int startNode = cuts.Count > 0 && cuts[0].S < NodeTolerance ? nodeOfCut[0] : NewNode(curve.Sample(0).Position);
        int endNode = cuts.Count > 0 && cuts[^1].S > length - NodeTolerance ? nodeOfCut[cuts.Count - 1] : NewNode(curve.Sample(length).Position);
        nodes.Add(startNode);
        var rest = alignment;
        float offset = 0;
        int from = startNode;
        for (int i = 0; i < cuts.Count; i++)
        {
            if (cuts[i].S < NodeTolerance || cuts[i].S > length - NodeTolerance) continue;
            var (left, right) = AlignmentOps.SplitAt(rest, cuts[i].S - offset);
            edges.Add(NewEdge(rules, left, from, nodeOfCut[i]));
            from = nodeOfCut[i];
            nodes.Add(from);
            rest = right;
            offset = cuts[i].S;
        }
        edges.Add(NewEdge(rules, rest, from, endNode));
        nodes.Add(endNode);
        return new AddResult(edges, nodes.Distinct().ToList());

        static Cut OnEdge(float s, Vector2 p, GraphEdge e, float edgeS) => new(s, p, null, e.Id, edgeS);
    }

    /// <summary>
    /// Splits an edge at station <paramref name="s"/> with a new node. Within <see cref="NodeTolerance"/> of an end it
    /// returns that end's node instead and splits nothing (<c>Left</c>/<c>Right</c> null).
    /// </summary>
    public (int Node, int? Left, int? Right) SplitEdge(int edgeId, float s)
    {
        var e = _edges[edgeId];
        if (s < NodeTolerance) return (e.Start, null, null);
        if (s > e.Alignment.Length - NodeTolerance) return (e.End, null, null);
        var (l, r) = AlignmentOps.SplitAt(e.Alignment, s);
        int node = NewNode(l.Pis[^1].Position);
        DetachEdge(edgeId);
        int left = NewEdge(e.Rules, l, e.Start, node, e.CustomData);
        int right = NewEdge(e.Rules, r, node, e.End, e.CustomData);
        return (node, left, right);
    }

    // --- Removing ---

    /// <summary>
    /// Removes an edge. A node left with no edges goes; a node left with exactly two edges of the same profile running
    /// straight through merges them back into one (DESIGN.md → Graph). Returns the merged edges' ids.
    /// </summary>
    public List<int> RemoveEdge(int edgeId)
    {
        var e = _edges[edgeId];
        DetachEdge(edgeId);
        var merged = new List<int>();
        foreach (int n in new[] { e.Start, e.End }.Distinct())
        {
            if (!_nodes.TryGetValue(n, out var node)) continue;
            if (node.Edges.Count == 0) _nodes.Remove(n);
            else if (TryMerge(n) is { } m) merged.Add(m);
        }
        return merged;
    }

    /// <summary>Merges the two edges at a node into one, if they share a profile and run straight through.</summary>
    public int? TryMerge(int nodeId)
    {
        var node = _nodes[nodeId];
        if (node.Edges.Count != 2 || node.Edges[0] == node.Edges[1]) return null;
        var arms = Arms(nodeId);
        var a = _edges[arms[0].EdgeId];
        var b = _edges[arms[1].EdgeId];
        if (a.Rules.Id != b.Rules.Id) return null;
        if (Vector2.Dot(arms[0].Direction, arms[1].Direction) > -MathF.Cos(StraightDegrees * MathF.PI / 180f)) return null;

        // Orient a to end at the node and b to start there.
        var aa = arms[0].AtStart ? AlignmentOps.Reversed(a.Alignment) : a.Alignment;
        int aFar = arms[0].AtStart ? a.End : a.Start;
        var bb = arms[1].AtStart ? b.Alignment : AlignmentOps.Reversed(b.Alignment);
        int bFar = arms[1].AtStart ? b.End : b.Start;
        DetachEdge(a.Id);
        DetachEdge(b.Id);
        _nodes.Remove(nodeId);
        return NewEdge(a.Rules, AlignmentOps.Join(aa, bb), aFar, bFar, a.CustomData);
    }

    // --- Helpers ---

    /// <summary>The edge whose centre line passes within <paramref name="within"/> of a point, and the station.</summary>
    public (GraphEdge Edge, float S)? ClosestEdge(Vector2 p, float within)
    {
        (GraphEdge, float)? best = null;
        float bestDist = within;
        foreach (var e in _edges.Values)
        {
            var cp = e.Alignment.Curve.ClosestPoint(p);
            float d = Vector2.Distance(cp.Position, p);
            if (d > bestDist) continue;
            bestDist = d;
            best = (e, cp.S);
        }
        return best;
    }

    private int NewNode(Vector2 p)
    {
        int id = _nextNode++;
        _nodes[id] = new GraphNode(id, p);
        return id;
    }

    private int NewEdge(ProfileRules rules, Alignment alignment, int start, int end, object? data = null)
    {
        int id = _nextEdge++;
        _edges[id] = new GraphEdge(id, rules, alignment, start, end) { CustomData = data };
        _nodes[start].Edges.Add(id);
        _nodes[end].Edges.Add(id);
        return id;
    }

    /// <summary>Takes an edge out of the graph and off its nodes (the nodes stay).</summary>
    private void DetachEdge(int id)
    {
        var e = _edges[id];
        _nodes[e.Start].Edges.RemoveAll(x => x == id);
        if (_nodes.TryGetValue(e.End, out var end)) end.Edges.RemoveAll(x => x == id);
        _edges.Remove(id);
    }
}
