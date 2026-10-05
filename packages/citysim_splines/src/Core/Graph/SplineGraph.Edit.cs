using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>What <see cref="SplineGraph.Reconnect"/> made: the edited edges as they are now (new ids, in the order
/// given, split wherever they joined something), and every node they touch.</summary>
public sealed record EditResult(IReadOnlyList<int> Edges, IReadOnlyList<int> Nodes);

/// <summary>A stretch of an edge, between two of its road points (<see cref="Alignment.StretchAt"/>): what a click on a
/// road with corners selects in the Edit tool.</summary>
public readonly record struct Stretch(int EdgeId, int Index);

/// <summary>
/// The Edit tool's operations (DESIGN.md → Edit tool). Moving a node, moving a corner point and setting a radius only
/// change geometry: the edge keeps its id and nodes, and every other corner keeps its radius. <see cref="Reconnect"/>
/// then joins the changed edges to the network the way a draw would, so an edit is "move, then reconnect".
/// </summary>
public sealed partial class SplineGraph
{
    /// <summary>Moves a node and the end of every edge at it (both ends of a loop). Returns those edges.</summary>
    public List<int> MoveNode(int nodeId, Vector2 to)
    {
        var node = _nodes[nodeId];
        node.Position = to;
        var edges = node.Edges.Distinct().ToList();
        foreach (int id in edges)
        {
            var e = _edges[id];
            var pis = e.Alignment.Pis.ToList();
            if (e.Start == nodeId) pis[0] = pis[0] with { Position = to };
            if (e.End == nodeId) pis[^1] = pis[^1] with { Position = to };
            _edges[id] = e with { Alignment = new Alignment(pis) };
        }
        return edges;
    }

    /// <summary>Sets the kerb radii at one end of an edge (default = both back to the profile's).</summary>
    public void SetKerb(int edgeId, bool atStart, KerbEnds kerbs)
    {
        var e = _edges[edgeId];
        _edges[edgeId] = atStart ? e with { KerbStart = kerbs } : e with { KerbEnd = kerbs };
    }

    /// <summary>Sets this end's radius of the kerb on one side of an edge's end (+1 right, −1 left; null = the profile's).</summary>
    public void SetKerb(int edgeId, bool atStart, int side, float? radius) =>
        SetKerb(edgeId, atStart, _edges[edgeId].KerbAt(atStart).With(side, radius));

    /// <summary>Puts every kerb at a node back to the profile's kerb radius. Returns whether any was set.</summary>
    public bool ResetKerbs(int nodeId)
    {
        bool any = false;
        foreach (var arm in Arms(nodeId))
        {
            if (!_edges[arm.EdgeId].KerbAt(arm.AtStart).IsSet) continue;
            SetKerb(arm.EdgeId, arm.AtStart, default(KerbEnds));
            any = true;
        }
        return any;
    }

    /// <summary>Sets the consumer's data at one end of an edge (<see cref="GraphEdge.DataStart"/>; null clears it).</summary>
    public void SetEndData(int edgeId, bool atStart, object? data)
    {
        var e = _edges[edgeId];
        _edges[edgeId] = atStart ? e with { DataStart = data } : e with { DataEnd = data };
    }

    /// <summary>Moves interior PI <paramref name="i"/> of an edge (its ends are nodes: <see cref="MoveNode"/>).</summary>
    public void MovePi(int edgeId, int i, Vector2 to) =>
        ReplacePi(edgeId, i, pi => pi with { Position = to });

    /// <summary>Sets the radius of interior PI <paramref name="i"/> of an edge (a hard corner becomes a rounded one).</summary>
    public void SetRadius(int edgeId, int i, float radius) =>
        ReplacePi(edgeId, i, pi => pi with { Radius = radius, Hard = false });

    /// <summary>Makes interior PI <paramref name="i"/> of an edge a hard corner.</summary>
    public void SetHard(int edgeId, int i) =>
        ReplacePi(edgeId, i, pi => pi with { Hard = true });

    /// <summary>Takes interior PI <paramref name="i"/> out of an edge: its two legs become one straight leg, and the
    /// other corners keep their radii.</summary>
    public void RemovePi(int edgeId, int i)
    {
        var e = _edges[edgeId];
        if (i <= 0 || i >= e.Alignment.Pis.Count - 1) throw new ArgumentOutOfRangeException(nameof(i), "not an interior PI");
        var pis = e.Alignment.Pis.ToList();
        pis.RemoveAt(i);
        _edges[edgeId] = e with { Alignment = new Alignment(pis) };
    }

    /// <summary>
    /// Moves a group (the Edit tool's selection): the given nodes and both ends of the given edges move by
    /// <paramref name="delta"/>. An edge with both ends moving moves rigidly, every PI with it; an edge with one end
    /// moving stretches (only that end moves). A <see cref="Stretch"/> moves its two PIs (its leg), so the legs either
    /// side stretch to follow; an end PI moves its node, but that alone doesn't make its own edge rigid. Returns every
    /// edge that changed, for <see cref="Reconnect"/>.
    /// </summary>
    public List<int> MoveGroup(IEnumerable<int> nodeIds, IEnumerable<int> edgeIds, Vector2 delta, IEnumerable<Stretch>? stretches = null)
    {
        var edgeList = edgeIds.Where(_edges.ContainsKey).ToList();
        var rigidBy = nodeIds.Where(_nodes.ContainsKey).Concat(edgeList.SelectMany(id => new[] { _edges[id].Start, _edges[id].End })).ToHashSet();
        var legs = (stretches ?? Array.Empty<Stretch>()).Where(st => _edges.ContainsKey(st.EdgeId)).ToList();
        var piMoves = legs.GroupBy(st => st.EdgeId)
            .ToDictionary(gr => gr.Key, gr => gr.SelectMany(st => new[] { st.Index, st.Index + 1 }).ToHashSet());
        var moved = rigidBy.ToHashSet();
        foreach (var (id, ks) in piMoves)
        {
            var e = _edges[id];
            if (ks.Contains(0)) moved.Add(e.Start);
            if (ks.Contains(e.Alignment.Pis.Count - 1)) moved.Add(e.End);
        }
        var changed = moved.SelectMany(n => _nodes[n].Edges).Concat(piMoves.Keys).Distinct().ToList();
        foreach (int n in moved) _nodes[n].Position += delta;
        foreach (int id in changed)
        {
            var e = _edges[id];
            bool rigid = edgeList.Contains(id) || (rigidBy.Contains(e.Start) && rigidBy.Contains(e.End));
            bool start = moved.Contains(e.Start), end = moved.Contains(e.End);
            var own = piMoves.GetValueOrDefault(id);
            var pis = e.Alignment.Pis.ToList();
            for (int k = 0; k < pis.Count; k++)
                if (rigid || (k == 0 && start) || (k == pis.Count - 1 && end) || (own?.Contains(k) == true && k > 0 && k < pis.Count - 1))
                    pis[k] = pis[k] with { Position = pis[k].Position + delta };
            _edges[id] = e with { Alignment = new Alignment(pis) };
        }
        return changed;
    }

    /// <summary>
    /// Takes stretches out of an edge (Delete on a selected stretch), keeping the rest of it as built: each run of
    /// kept stretches becomes an edge, cut at the road points, with a new dead end at each cut. Where what's left
    /// meets itself again (a loop with nothing else at its node) it joins back into one edge. Returns the edges left.
    /// </summary>
    public List<int> RemoveStretches(int edgeId, IReadOnlyCollection<int> stretches)
    {
        var e = _edges[edgeId];
        var a = e.Alignment;
        var keep = new List<(float S0, float S1)>();
        for (int k = 0; k < a.StretchCount; k++)
        {
            if (stretches.Contains(k)) continue;
            float s0 = a.RoadStation(k), s1 = a.RoadStation(k + 1);
            if (keep.Count > 0 && MathF.Abs(keep[^1].S1 - s0) < 1e-3f) keep[^1] = (keep[^1].S0, s1);
            else keep.Add((s0, s1));
        }
        if (keep.Count == 0) return RemoveEdge(edgeId);
        DetachEdge(edgeId);
        var made = new List<int>();
        foreach (var (s0, s1) in keep)
        {
            var piece = AlignmentOps.Between(a, s0, s1);
            int from = s0 < 1e-3f ? e.Start : NewNode(piece.Pis[0].Position);
            int to = s1 > a.Length - 1e-3f ? e.End : NewNode(piece.Pis[^1].Position);
            made.Add(NewEdge(e.Rules, piece, from, to, e.CustomData, from == e.Start ? e.KerbStart : default, to == e.End ? e.KerbEnd : default,
                from == e.Start ? e.DataStart : null, to == e.End ? e.DataEnd : null));
        }
        foreach (int n in new[] { e.Start, e.End }.Distinct())
        {
            if (!_nodes.TryGetValue(n, out var node)) continue;
            if (node.Edges.Count == 0) _nodes.Remove(n);
            else if (TryMerge(n) is { } m) made.Add(m);
        }
        return made.Where(_edges.ContainsKey).ToList();
    }

    /// <summary>
    /// Rounds the joint at a node where two edges meet (the radial menu's Smooth on a node): one of them is taken out
    /// and added back continuing the other, as a draw onto a dead end does, with the joint's corner at the largest
    /// radius that fits. Same profiles become one edge; another profile is split back off where the corner starts.
    /// Null when the node isn't a joint of two edges.
    /// </summary>
    public EditResult? SmoothNode(int nodeId)
    {
        if (!IsJoint(nodeId)) return null;
        var arm = Arms(nodeId)[0];
        var e = _edges[arm.EdgeId];
        var pis = e.Alignment.Pis.ToList();
        int at = arm.AtStart ? 0 : pis.Count - 1;
        // Asks for more than can fit: the corner is clamped to the fit, then stored as what was built.
        pis[at] = pis[at] with { Radius = Huge, Hard = false };
        DetachEdge(e.Id);
        int far = arm.AtStart ? e.End : e.Start;
        if (_nodes[far].Edges.Count == 0) _nodes.Remove(far);
        var r = AddSpline(new Alignment(pis), e.Rules, arm.AtStart ? Ends.Start : Ends.End, e.CustomData, (e.KerbStart, e.KerbEnd),
            (e.DataStart, e.DataEnd));
        var edges = r.Edges.Concat(r.Kept).Where(_edges.ContainsKey).ToList();
        foreach (int id in edges)
        {
            var a = _edges[id].Alignment;
            if (a.Pis.All(p => p.Radius < Huge)) continue;
            var fixedPis = a.Pis.Select((p, i) => p.Radius < Huge ? p
                : p with { Radius = i == 0 || i == a.Pis.Count - 1 ? 0 : a.EffectiveRadius(i) });
            _edges[id] = _edges[id] with { Alignment = new Alignment(fixedPis) };
        }
        return new EditResult(edges, r.Nodes.Where(_nodes.ContainsKey).ToList());
    }

    /// <summary>Where a node would move to straighten its joint: onto the line through the next PI along each of
    /// its two edges. Null when it isn't a joint of two edges.</summary>
    public Vector2? StraightenedNode(int nodeId)
    {
        if (!IsJoint(nodeId)) return null;
        var arms = Arms(nodeId);
        return OntoLine(Next(arms[0]), Next(arms[1]), _nodes[nodeId].Position);

        Vector2 Next(Arm arm)
        {
            var pis = _edges[arm.EdgeId].Alignment.Pis;
            return arm.AtStart ? pis[1].Position : pis[^2].Position;
        }
    }

    /// <summary>The turn at a joint of two edges (degrees, 0 = straight through), or null at any other node.</summary>
    public float? JointTurn(int nodeId)
    {
        if (!IsJoint(nodeId)) return null;
        var arms = Arms(nodeId);
        float dot = Math.Clamp(-Vector2.Dot(arms[0].Direction, arms[1].Direction), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    /// <summary>Removes every edge at a node (the radial menu's Delete on a node), merging what's left as
    /// <see cref="RemoveEdge"/> does.</summary>
    public void RemoveNode(int nodeId)
    {
        while (_nodes.TryGetValue(nodeId, out var node) && node.Edges.Count > 0) RemoveEdge(node.Edges[0]);
        _nodes.Remove(nodeId);
    }

    private const float Huge = 1e6f;

    /// <summary>Two different edges meet here (not a loop's own ends).</summary>
    private bool IsJoint(int nodeId) =>
        _nodes.TryGetValue(nodeId, out var n) && n.Edges.Count == 2 && n.Edges[0] != n.Edges[1];

    /// <summary>
    /// Joins edited edges to the network as a draw would (DESIGN.md → Edit tool): each is taken out and added again,
    /// so a crossing with a profile it connects to becomes a junction, and an end on a node or on a road joins it.
    /// Their nodes stay where they are, so the junctions they had are kept. <paramref name="dropped"/> is a node the
    /// edit moved: if it now sits on another node, its edges join that one instead, and when both were dead ends that
    /// connect, the dragged one continues the other like a draw onto a dead end does. Nothing else continues: re-adding
    /// a junction's arms one by one would otherwise merge them.
    /// </summary>
    public EditResult Reconnect(IReadOnlyCollection<int> edgeIds, int? dropped = null)
    {
        var changed = edgeIds.Distinct().Where(_edges.ContainsKey).Select(id => _edges[id]).ToList();
        int? onto = null;
        bool deadEnd = false;
        if (dropped is { } d && _nodes.ContainsKey(d) && NodeAt(_nodes[d].Position, except: d) is { } t)
        {
            onto = t;
            deadEnd = _nodes[d].Edges.Count == 1;
            var to = _nodes[t].Position;
            for (int k = 0; k < changed.Count; k++)
            {
                var e = changed[k];
                if (e.Start != d && e.End != d) continue;
                var pis = e.Alignment.Pis.ToList();
                if (e.Start == d) pis[0] = pis[0] with { Position = to };
                if (e.End == d) pis[^1] = pis[^1] with { Position = to };
                changed[k] = e with { Alignment = new Alignment(pis) };
            }
        }

        foreach (var e in changed) DetachEdge(e.Id);
        // Their nodes left empty go, so an end dropped on a road splits it rather than finding its own old node.
        foreach (int n in changed.SelectMany(e => new[] { e.Start, e.End }).Distinct())
            if (_nodes.TryGetValue(n, out var node) && node.Edges.Count == 0) _nodes.Remove(n);

        var edges = new List<int>();
        var nodes = new List<int>();
        foreach (var e in changed)
        {
            var continueAt = Ends.None;
            if (onto is not null && deadEnd && e.Start == dropped) continueAt |= Ends.Start;
            if (onto is not null && deadEnd && e.End == dropped) continueAt |= Ends.End;
            var r = AddSpline(e.Alignment, e.Rules, continueAt, e.CustomData, (e.KerbStart, e.KerbEnd), (e.DataStart, e.DataEnd));
            // A later re-add may split an earlier one's edge where they cross: keep only the ids still there.
            edges.RemoveAll(id => !_edges.ContainsKey(id));
            edges.AddRange(r.Edges.Concat(r.Kept));
            nodes.AddRange(r.Nodes);
        }
        // Pieces of earlier re-adds split by later ones are edited edges too.
        var all = edges.ToHashSet();
        foreach (int n in nodes.Where(_nodes.ContainsKey).ToList())
            foreach (int id in _nodes[n].Edges)
                if (!all.Contains(id) && changed.Any(c => c.Rules.Id == _edges[id].Rules.Id && Covers(c.Alignment, _edges[id].Alignment)))
                {
                    all.Add(id);
                    edges.Add(id);
                }
        return new EditResult(edges.Distinct().ToList(), nodes.Where(_nodes.ContainsKey).Distinct().ToList());
    }

    /// <summary>The radius whose arc's middle passes through <paramref name="cursor"/> projected on the bisector of
    /// interior corner <paramref name="i"/> (the radius knob): the middle sits R (sec(Δ/2) − 1) in from the PI.
    /// Null for a corner that doesn't turn.</summary>
    public static float? KnobRadius(Alignment a, int i, Vector2 cursor)
    {
        var p = a.Pis[i].Position;
        var legIn = p - a.Pis[i - 1].Position;
        var legOut = a.Pis[i + 1].Position - p;
        if (legIn.Length() < SplineMath.Epsilon || legOut.Length() < SplineMath.Epsilon) return null;
        var dirIn = Vector2.Normalize(legIn);
        var dirOut = Vector2.Normalize(legOut);
        float delta = MathF.Abs(SplineMath.Turn(dirIn, dirOut));
        if (delta < 1e-3f || delta > MathF.PI - 1e-3f) return null;
        var inward = Vector2.Normalize(dirOut - dirIn);
        float d = MathF.Max(0, Vector2.Dot(cursor - p, inward));
        return d / (1f / MathF.Cos(delta / 2) - 1f);
    }

    /// <summary>The point on the line through <paramref name="a"/> and <paramref name="b"/> nearest to
    /// <paramref name="p"/> (Alt-drag straightens a PI onto its neighbours' line).</summary>
    public static Vector2 OntoLine(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        return len2 < SplineMath.Epsilon ? a : a + ab * (Vector2.Dot(p - a, ab) / len2);
    }

    /// <summary>The node within <see cref="NodeTolerance"/> of a point, other than <paramref name="except"/>.</summary>
    public int? NodeAt(Vector2 p, int except)
    {
        int? best = null;
        float bestDist = NodeTolerance;
        foreach (var n in _nodes.Values)
        {
            if (n.Id == except) continue;
            float d = Vector2.Distance(n.Position, p);
            if (d > bestDist) continue;
            bestDist = d;
            best = n.Id;
        }
        return best;
    }

    private void ReplacePi(int edgeId, int i, Func<Pi, Pi> change)
    {
        var e = _edges[edgeId];
        if (i <= 0 || i >= e.Alignment.Pis.Count - 1) throw new ArgumentOutOfRangeException(nameof(i), "not an interior PI");
        var pis = e.Alignment.Pis.ToList();
        pis[i] = change(pis[i]);
        _edges[edgeId] = e with { Alignment = new Alignment(pis) };
    }

    /// <summary>Whether <paramref name="part"/> lies along <paramref name="whole"/> (a piece split off it).</summary>
    private static bool Covers(Alignment whole, Alignment part)
    {
        var c = whole.Curve;
        var mid = part.Curve.Sample(part.Length / 2).Position;
        return Vector2.Distance(c.ClosestPoint(mid).Position, mid) < EdgeTolerance;
    }
}
