using System.Collections.Generic;
using System.Linq;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Stretches (DESIGN.md → Edit tool): on a road with corners a click selects only the stretch between two of its road
/// points (the dots), not the whole edge. A selected stretch is outlined with its corners' handles; Delete takes it
/// out (the rest of the road stays, cut at its dots), a drag moves its leg (the road either side stretches to follow),
/// and RMB on it opens the radial menu for its corners. A road with no corners is one stretch: a click selects the
/// edge as before. A box takes whole edges wholly inside it, and on a road that isn't, the stretches that are.
/// </summary>
public partial class SplineEditTool
{
    private readonly HashSet<Stretch> _stretches = new();
    private Stretch? _hoverStretch;
    private Stretch? _pressStretch;

    public IReadOnlyCollection<Stretch> SelectedStretches => _stretches;

    /// <summary>The stretch of edge <paramref name="id"/> nearest a point, or null when the edge is one stretch.</summary>
    private static Stretch? StretchOf(SplineGraph g, int id, NumVector2 p)
    {
        var a = g.Edge(id).Alignment;
        return a.StretchCount < 2 ? null : new Stretch(id, a.StretchAt(a.Curve.ClosestPoint(p).S));
    }

    private static bool Valid(SplineGraph g, Stretch st) =>
        g.HasEdge(st.EdgeId) && st.Index < g.Edge(st.EdgeId).Alignment.StretchCount;

    /// <summary>The middle of a stretch on the road: where it's found again after an edit renumbers its edge, and
    /// where its radial menu opens.</summary>
    private static NumVector2 MidOf(SplineGraph g, Stretch st)
    {
        var a = g.Edge(st.EdgeId).Alignment;
        return a.Curve.Sample((a.RoadStation(st.Index) + a.RoadStation(st.Index + 1)) / 2).Position;
    }

    /// <summary>Selects again what's at each point after an edit: the stretch there, or the edge when it's one stretch.</summary>
    private void Reselect(SplineGraph g, IEnumerable<NumVector2> points)
    {
        foreach (var p in points)
        {
            var hit = g.Edges.Select(e => (e, d: NumVector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p)))
                .Where(x => x.d <= x.e.Rules.Width / 2).OrderBy(x => x.d).Select(x => x.e).FirstOrDefault();
            if (hit is null) continue;
            if (StretchOf(g, hit.Id, p) is { } st) _stretches.Add(st);
            else _selected.Add(hit.Id);
        }
    }

    /// <summary>A stretch as its own piece of road, for its outline.</summary>
    private static EditEdge StretchEdge(SplineGraph g, Stretch st)
    {
        var e = g.Edge(st.EdgeId);
        var a = e.Alignment;
        return new EditEdge(AlignmentOps.Between(a, a.RoadStation(st.Index), a.RoadStation(st.Index + 1)), e.Rules.Width);
    }

    /// <summary>The interior corners at a stretch's two ends (a stretch ending on a node has one, or none).</summary>
    private static IEnumerable<int> CornersOf(Alignment a, int k) =>
        new[] { k, k + 1 }.Where(i => i > 0 && i < a.Pis.Count - 1);

    /// <summary>Every corner whose handles show: all of a selected edge's, and a selected stretch's two.</summary>
    private IEnumerable<(int Edge, int Index)> SelectedCorners(SplineGraph g) =>
        _selected.Where(g.HasEdge).SelectMany(id => Enumerable.Range(1, g.Edge(id).Alignment.Pis.Count - 2).Select(i => (id, i)))
            .Concat(_stretches.Where(st => Valid(g, st)).SelectMany(st => CornersOf(g.Edge(st.EdgeId).Alignment, st.Index).Select(i => (st.EdgeId, i))))
            .Distinct();

    /// <summary>The nodes a stretch's leg moves: its edge's start or end when the stretch reaches it.</summary>
    private static IEnumerable<int> EndNodes(SplineGraph g, Stretch st)
    {
        var e = g.Edge(st.EdgeId);
        if (st.Index == 0) yield return e.Start;
        if (st.Index + 1 == e.Alignment.Pis.Count - 1) yield return e.End;
    }

    // --- Direct-API test hooks ---

    /// <summary>A click at a plan point, as the mouse's: a node, else the stretch (or one-stretch edge) under it
    /// (Shift adds or removes).</summary>
    public void ClickForTest(NumVector2 at, bool shift = false)
    {
        if (Network is null) return;
        ForcedPlanCursor = at;
        _view.UpdateCursor();
        _pressHandle = Pick(at);
        _hoverEdge = _pressHandle is null ? EdgeUnder(at) : null;
        _hoverStretch = _hoverEdge is { } id ? StretchOf(Network.Graph, id, at) : null;
        Click(shift);
        _pressHandle = null;
    }

    /// <summary>Delete with the selection as it is.</summary>
    public void DeleteForTest() => DeleteSelection();

    /// <summary>RMB on the road at <paramref name="at"/>: opens the radial menu on the stretch there, with action
    /// <paramref name="item"/> hovered; <paramref name="choose"/> picks it.</summary>
    public bool RoadMenuForTest(NumVector2 at, int item, bool choose)
    {
        if (Network is null || EdgeUnder(at) is not { } id) return false;
        var st = StretchOf(Network.Graph, id, at) ?? new Stretch(id, 0);
        OpenMenu(new Handle(HandleKind.Stretch, st.EdgeId, st.Index, MidOf(Network.Graph, st)));
        _forcedMenuItem = item;
        UpdateMenu(_menu!);
        if (choose) Choose(_menu!, item);
        _forcedMenuItem = null;
        return true;
    }
}
