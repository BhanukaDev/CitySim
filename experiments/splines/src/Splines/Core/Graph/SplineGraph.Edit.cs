using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>What <see cref="SplineGraph.Reconnect"/> made: the edited edges as they are now (new ids, in the order
/// given, split wherever they joined something), and every node they touch.</summary>
public sealed record EditResult(IReadOnlyList<int> Edges, IReadOnlyList<int> Nodes);

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

    /// <summary>Moves interior PI <paramref name="i"/> of an edge (its ends are nodes: <see cref="MoveNode"/>).</summary>
    public void MovePi(int edgeId, int i, Vector2 to) =>
        ReplacePi(edgeId, i, pi => pi with { Position = to });

    /// <summary>Sets the radius of interior PI <paramref name="i"/> of an edge (a hard corner becomes a rounded one).</summary>
    public void SetRadius(int edgeId, int i, float radius) =>
        ReplacePi(edgeId, i, pi => pi with { Radius = radius, Hard = false });

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
            var r = AddSpline(e.Alignment, e.Rules, continueAt, e.CustomData);
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
