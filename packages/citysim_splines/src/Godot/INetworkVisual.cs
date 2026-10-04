using System.Collections.Generic;

namespace CitySim.Splines.Godot;

/// <summary>
/// The consumer's visuals for the built network (DESIGN.md → Hooks, the first slice of <c>ISplineVisual</c>): set on
/// <see cref="SplineNetwork.Visual"/> and it's called in place of the placeholder flat ribbons after every change, undo,
/// redo and Edit trial. Draw previews stay flat ribbons. Simple full rebuilds, like the ribbons (S11 makes them
/// incremental).
/// </summary>
public interface INetworkVisual
{
    /// <summary>Rebuilds everything from <paramref name="graph"/>: each edge between its cut-backs
    /// (<see cref="Junctions.CutBacks"/>), each junction footprint, and a highlight for each issue.
    /// <paramref name="hidden"/> edges are left out (the ones a draw in progress is continuing).</summary>
    void SetNetwork(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IReadOnlyList<Issue> issues, IReadOnlySet<int>? hidden);
}
