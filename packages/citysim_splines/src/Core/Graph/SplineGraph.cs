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
    public Vector2 Position { get; internal set; }
    public List<int> Edges { get; } = new();
}

/// <summary>
/// An edge between two nodes: its alignment (treated as immutable once in the graph: operations replace it), the
/// profile it was drawn with, and the consumer's <see cref="CustomData"/>, which the graph copies across split and
/// merge but never reads. <see cref="KerbStart"/> / <see cref="KerbEnd"/>: the kerb radii set at each end by the Edit
/// tool's kerb knobs and road handles (DESIGN.md → Junctions → Kerb handles). They stay with the end while it's on the
/// same road.
/// </summary>
public sealed record GraphEdge(int Id, ProfileRules Rules, Alignment Alignment, int Start, int End)
{
    public object? CustomData { get; init; }
    public KerbEnds KerbStart { get; init; }
    public KerbEnds KerbEnd { get; init; }

    public KerbEnds KerbAt(bool atStart) => atStart ? KerbStart : KerbEnd;
}

/// <summary>
/// The kerb radii set at one end of an edge: this end's radius of the kerb on each side of the road, as seen from the
/// node looking along it (<see cref="Right"/> toward the next arm clockwise, <see cref="Left"/> the previous), or null
/// for the profile's <see cref="ProfileRules.KerbRadius"/>. Relative to the end, so reversing the edge keeps them.
/// </summary>
public readonly record struct KerbEnds(float? Left, float? Right)
{
    public bool IsSet => Left is not null || Right is not null;

    /// <summary>The side toward the next arm (+1, right) or the previous (−1, left).</summary>
    public float? Side(int side) => side > 0 ? Right : Left;

    public KerbEnds With(int side, float? radius) => side > 0 ? this with { Right = radius } : this with { Left = radius };
}

/// <summary>One edge leaving a node: which end of the edge is at the node, and the unit tangent pointing away.</summary>
public readonly record struct Arm(int EdgeId, bool AtStart, Vector2 Direction, ProfileRules Rules);

/// <summary>What <see cref="SplineGraph.AddSpline"/> made: the new edges in draw order, every node the new spline
/// touches (its ends and its junctions), the whole alignment it added (the drawn one, grown by any dead ends of its
/// own profile it continued), the edges the continued dead ends were, and the stations of <see cref="Alignment"/>
/// before <see cref="SolidUntil"/> and after <see cref="SolidFrom"/> that are those old roads unchanged (up to where the
/// corner at the joint starts), so a preview can draw them as built. A dead end of another profile keeps its own edge,
/// shortened to where the joint's corner starts: <see cref="Kept"/> are those edges (new ids), also built as they are.</summary>
public sealed record AddResult(IReadOnlyList<int> Edges, IReadOnlyList<int> Nodes, Alignment Alignment, IReadOnlyList<int> Continued,
    float SolidUntil = 0, float SolidFrom = float.PositiveInfinity)
{
    public IReadOnlyList<int> Kept { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The spline network (DESIGN.md → Graph): nodes and edges, each edge owning one alignment. Adding a spline splits it
/// and the edges it ends on or crosses into junctions, where the profiles connect (<see cref="Connects"/>); removing
/// an edge merges the two edges left at a node when they run straight through with the same profile. Plain lists and
/// linear searches (S11 adds a spatial index). <see cref="Clone"/> is cheap enough for the draw preview and the undo
/// history, since alignments are shared, never changed in place.
/// </summary>
public sealed partial class SplineGraph
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
    /// The edge a spline of <paramref name="rules"/>' profile would continue from a point: a node there with only that
    /// edge (not a loop), of a profile it <see cref="Connects"/> to, so the two run on as one road (DESIGN.md →
    /// Junctions → Continuing a dead end). Null anywhere else.
    /// </summary>
    public (int EdgeId, bool AtStart)? DeadEndAt(Vector2 p, ProfileRules rules)
    {
        if (rules.JunctionKind == JunctionKind.None || NodeAt(p) is not { } n || _nodes[n].Edges.Count != 1) return null;
        var e = _edges[_nodes[n].Edges[0]];
        if (e.Start == e.End || !Connects(rules, e.Rules)) return null;
        return (e.Id, e.Start == n);
    }

    /// <summary>
    /// Adds a drawn spline. An end on a dead end (<see cref="DeadEndAt"/>) continues that edge: the edge is taken into
    /// the spline and its end node becomes a corner, with the drawn end's radius (or hard corner), so every corner rule
    /// applies to it as if it had been drawn in one go. Its other corners keep their built radius. A dead end of
    /// another profile is then split off again where the joint's corner starts, so it keeps its profile and the drawn
    /// one carries the corner on (an avenue running on round a bend into a street). Otherwise ends join a node or split an edge they land on, and every crossing with an edge splits
    /// both, wherever the two profiles <see cref="Connects"/>. Where they don't, nothing joins (validation reports the
    /// crossing or overlap). <paramref name="continueAt"/> limits which ends may continue a dead end (an edit re-adding
    /// a junction's arms one by one mustn't merge them), and the new edges carry <paramref name="data"/>. An edit
    /// re-adding an edge passes its <paramref name="kerbs"/>: they go to the new spline's ends, unless an end continued
    /// a dead end (it's a corner now).
    /// </summary>
    public AddResult AddSpline(Alignment alignment, ProfileRules rules, Ends continueAt = Ends.Both, object? data = null,
        (KerbEnds Start, KerbEnds End) kerbs = default)
    {
        var (drawnStart, drawnEnd) = (alignment.Pis[0].Position, alignment.Pis[^1].Position);
        var continued = new List<int>();
        var emptied = new List<int>();
        var kept = new List<int>();
        (alignment, int startJoint, int endJoint) = Continue(alignment, rules, continueAt, continued, emptied, kept);
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

        // Crossing itself (a loop, or a continued road crossing its own built part) is a junction like any crossing,
        // for a profile that joins itself: both places along the spline get the same node. A hit at the spline's end
        // (a leg ending on its own road) is that end's cut.
        if (Connects(rules, rules))
            foreach (var hit in curve.SelfIntersect())
            {
                if (hit.SA < NodeTolerance && hit.SB > length - NodeTolerance) continue;
                int node = NodeAt(hit.Point) ?? NewNode(hit.Point);
                emptied.Add(node); // dropped again if dedup leaves it unused
                cuts.Add(new Cut(hit.SA, hit.Point, node, null, 0));
                cuts.Add(new Cut(hit.SB, hit.Point, node, null, 0));
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
        var endAt = curve.Sample(length).Position;
        // A closed spline (a loop re-added by an edit) ends on its own start node, not a second node at the same spot.
        int endNode = cuts.Count > 0 && cuts[^1].S > length - NodeTolerance ? nodeOfCut[cuts.Count - 1]
            : Vector2.Distance(endAt, _nodes[startNode].Position) < NodeTolerance ? startNode : NewNode(endAt);
        nodes.Add(startNode);
        var rest = alignment;
        float offset = 0;
        int from = startNode;
        for (int i = 0; i < cuts.Count; i++)
        {
            if (cuts[i].S < NodeTolerance || cuts[i].S > length - NodeTolerance) continue;
            var (left, right) = AlignmentOps.SplitAt(rest, cuts[i].S - offset);
            edges.Add(NewEdge(rules, left, from, nodeOfCut[i], data));
            from = nodeOfCut[i];
            nodes.Add(from);
            rest = right;
            offset = cuts[i].S;
        }
        edges.Add(NewEdge(rules, rest, from, endNode, data));
        nodes.Add(endNode);
        if (kerbs.Start.IsSet && Vector2.Distance(alignment.Pis[0].Position, drawnStart) < NodeTolerance)
            _edges[edges[0]] = _edges[edges[0]] with { KerbStart = kerbs.Start };
        if (kerbs.End.IsSet && Vector2.Distance(alignment.Pis[^1].Position, drawnEnd) < NodeTolerance)
            _edges[edges[^1]] = _edges[edges[^1]] with { KerbEnd = kerbs.End };
        foreach (int n in emptied)
            if (_nodes.TryGetValue(n, out var left) && left.Edges.Count == 0) _nodes.Remove(n);
        if (continued.Count > 0)
            foreach (int id in edges) RoundLoopJoint(id);
        float solidUntil = startJoint > 0 ? alignment.CornerStations(startJoint).Start : 0;
        float solidFrom = endJoint > 0 ? alignment.CornerStations(endJoint).End : float.PositiveInfinity;
        return new AddResult(edges, nodes.Distinct().ToList(), alignment, continued, solidUntil, solidFrom) { Kept = kept };

        static Cut OnEdge(float s, Vector2 p, GraphEdge e, float edgeS) => new(s, p, null, e.Id, edgeS);
    }

    /// <summary>
    /// A draw that continues its road back onto that road's own start closes a loop on one node, and that joint is a
    /// corner like any other (DESIGN.md → Continuing a dead end): it takes the drawn end's radius, unless it's hard. The
    /// node moves to the middle of the first leg, where the loop runs straight through it. Only for a loop alone on
    /// its node; a straight-through joint (a Curve-mode circle) is left as it is.
    /// </summary>
    private void RoundLoopJoint(int edgeId)
    {
        var e = _edges[edgeId];
        var pis = e.Alignment.Pis;
        if (e.Start != e.End || _nodes[e.Start].Edges.Count != 2 || pis.Count < 3 || pis[^1].Hard) return;
        var c = e.Alignment.Curve;
        if (Vector2.Dot(c.Sample(0).Tangent, c.Sample(c.Length).Tangent) > MathF.Cos(MathF.PI / 180)) return;
        var mid = (pis[0].Position + pis[1].Position) / 2;
        var joint = pis[0] with { Radius = pis[^1].Radius > 0 ? pis[^1].Radius : e.Rules.DefaultRadius, Hard = false };
        var loop = pis.Skip(1).Take(pis.Count - 2).Prepend(new Pi(mid)).Append(joint).Append(new Pi(mid));
        _edges[edgeId] = e with { Alignment = new Alignment(loop) };
        _nodes[e.Start].Position = mid;
    }

    /// <summary>
    /// Grows a drawn alignment by the dead ends its ends continue, taking those edges out of the graph. Their far
    /// nodes are left (maybe empty) for the spline to join, and listed in <paramref name="emptied"/> for clean-up. A
    /// dead end of another profile is split off again where the joint's corner starts (at most half of it goes to the
    /// corner) and put back as its own edge (<paramref name="kept"/>); the alignment returned starts or ends there.
    /// </summary>
    private (Alignment Alignment, int StartJoint, int EndJoint) Continue(Alignment drawn, ProfileRules rules, Ends continueAt,
        List<int> continued, List<int> emptied, List<int> kept)
    {
        var pis = drawn.Pis.ToList();
        int startJoint = -1, endJoint = -1;
        (GraphEdge Edge, bool AtStart)? startOld = null, endOld = null;
        if (continueAt.HasFlag(Ends.Start) && DeadEndAt(pis[0].Position, rules) is { } s)
        {
            var old = _edges[s.EdgeId];
            var lead = AlignmentOps.Pinned(s.AtStart ? AlignmentOps.Reversed(old.Alignment) : old.Alignment);
            pis = lead.Pis.Take(lead.Pis.Count - 1).Append(Joint(pis[0], rules)).Concat(pis.Skip(1)).ToList();
            startJoint = lead.Pis.Count - 1;
            Take(old);
            if (old.Rules.Id != rules.Id) startOld = (old, s.AtStart);
        }
        // After the start, so a draw back onto the other end of the same edge closes a loop instead.
        if (continueAt.HasFlag(Ends.End) && DeadEndAt(pis[^1].Position, rules) is { } e)
        {
            var old = _edges[e.EdgeId];
            var tail = AlignmentOps.Pinned(e.AtStart ? old.Alignment : AlignmentOps.Reversed(old.Alignment));
            endJoint = pis.Count - 1;
            pis = pis.Take(pis.Count - 1).Append(Joint(pis[^1], rules)).Concat(tail.Pis.Skip(1)).ToList();
            Take(old);
            if (old.Rules.Id != rules.Id) endOld = (old, e.AtStart);
        }
        if (continued.Count == 0) return (drawn, startJoint, endJoint);
        var grown = new Alignment(pis);
        if (startOld is null && endOld is null) return (grown, startJoint, endJoint);

        // Split the other profiles' roads back off, the end first so the start's station still holds.
        float length = grown.Length;
        float s0 = startOld is null ? 0 : MathF.Max(grown.CornerStations(startJoint).Start, startOld.Value.Edge.Alignment.Length / 2);
        float s1 = endOld is null ? length : MathF.Min(grown.CornerStations(endJoint).End, length - endOld.Value.Edge.Alignment.Length / 2);
        if (s1 - s0 < NodeTolerance) return (grown, startJoint, endJoint); // nothing left between: leave it one road
        var rest = grown;
        if (endOld is not null)
        {
            var (l, r) = AlignmentOps.SplitAt(rest, s1);
            Keep(endOld.Value, r, fromCut: true);
            rest = l;
        }
        if (startOld is not null)
        {
            var (l, r) = AlignmentOps.SplitAt(rest, s0);
            Keep(startOld.Value, l, fromCut: false);
            rest = r;
        }
        return (rest, -1, -1);

        // The old road's part (starting at the cut, or ending there) back as its own edge, in its old direction,
        // between its far node and a new node at the cut. Its old end node is left empty for clean-up.
        void Keep((GraphEdge Edge, bool AtStart) old, Alignment part, bool fromCut)
        {
            var (e, atStart) = old;
            int far = atStart ? e.End : e.Start;
            int cut = NewNode(fromCut ? part.Pis[0].Position : part.Pis[^1].Position);
            var piece = atStart == fromCut ? part : AlignmentOps.Reversed(part);
            kept.Add(atStart ? NewEdge(e.Rules, piece, cut, far, e.CustomData, kerbEnd: e.KerbEnd)
                : NewEdge(e.Rules, piece, far, cut, e.CustomData, kerbStart: e.KerbStart));
            emptied.Add(atStart ? e.Start : e.End);
        }

        void Take(GraphEdge old)
        {
            DetachEdge(old.Id);
            continued.Add(old.Id);
            foreach (int n in new[] { old.Start, old.End })
                if (_nodes[n].Edges.Count == 0) emptied.Add(n);
        }

        // A drawn end carries the pending radius; one without (a scripted alignment) takes the profile's default.
        static Pi Joint(Pi end, ProfileRules rules) =>
            end.Hard || end.Radius > 0 ? end : end with { Radius = rules.DefaultRadius };
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
        int left = NewEdge(e.Rules, l, e.Start, node, e.CustomData, kerbStart: e.KerbStart);
        int right = NewEdge(e.Rules, r, node, e.End, e.CustomData, kerbEnd: e.KerbEnd);
        return (node, left, right);
    }

    /// <summary>
    /// Makes a junction node on a bend (DESIGN.md → Junctions → Corner junctions): interior PI <paramref name="pi"/>
    /// of an edge is rebuilt at <paramref name="radius"/> (0 = a sharp corner at the PI) and the edge is split at that
    /// arc's middle, which is <see cref="Alignment.BendPoint"/>. The edge's other corners keep the radii they were
    /// built with. At the built radius it's the same split as a road ending on the arc's middle. Returns the node.
    /// </summary>
    public int SplitBend(int edgeId, int pi, float radius)
    {
        var reshaped = ReshapeBend(edgeId, pi, radius);
        var (s0, s1) = reshaped.CornerStations(pi);
        return SplitEdge(edgeId, (s0 + s1) / 2).Node;
    }

    /// <summary>The first half of <see cref="SplitBend"/>: the bend rebuilt at <paramref name="radius"/> (0 = sharp),
    /// the other corners kept, nothing split (the Draw tool's hover preview). Returns the new alignment.</summary>
    public Alignment ReshapeBend(int edgeId, int pi, float radius)
    {
        var e = _edges[edgeId];
        var pis = AlignmentOps.Pinned(e.Alignment).Pis.ToList();
        pis[pi] = radius <= 0 ? pis[pi] with { Hard = true } : pis[pi] with { Radius = radius, Hard = false };
        var reshaped = new Alignment(pis);
        _edges[edgeId] = e with { Alignment = reshaped };
        return reshaped;
    }

    /// <summary>
    /// Whether corner <paramref name="i"/> of an edge offers its road point (a dot, a snap): not when it lies within a
    /// road width of a junction (3+ roads) at the edge's end. That's half of a bend a road was joined to, inside the
    /// junction, whose node is the bend's point now. A long arc into a junction (a circle's quarter) keeps its dot.
    /// </summary>
    public bool ShowsRoadPoint(GraphEdge e, int i)
    {
        var p = e.Alignment.RoadPoint(i);
        foreach (int n in new[] { e.Start, e.End })
            if (_nodes[n].Edges.Count >= 3 && Vector2.Distance(_nodes[n].Position, p) < e.Rules.Width) return false;
        return true;
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
        return NewEdge(a.Rules, AlignmentOps.Join(aa, bb), aFar, bFar, a.CustomData,
            arms[0].AtStart ? a.KerbEnd : a.KerbStart, arms[1].AtStart ? b.KerbEnd : b.KerbStart);
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

    private int NewEdge(ProfileRules rules, Alignment alignment, int start, int end, object? data = null,
        KerbEnds kerbStart = default, KerbEnds kerbEnd = default)
    {
        int id = _nextEdge++;
        _edges[id] = new GraphEdge(id, rules, alignment, start, end) { CustomData = data, KerbStart = kerbStart, KerbEnd = kerbEnd };
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
