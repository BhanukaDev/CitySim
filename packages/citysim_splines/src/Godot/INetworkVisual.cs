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
    /// <paramref name="hidden"/> edges (the ones a draw in progress is continuing) are drawn only over their
    /// <see cref="EdgeSpan"/>, the old road the draw keeps unchanged; the junctions at their ends are drawn as usual.</summary>
    void SetNetwork(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IReadOnlyList<Issue> issues, IReadOnlyDictionary<int, EdgeSpan>? hidden);
}

/// <summary>The stations of a hidden edge still drawn (<see cref="INetworkVisual.SetNetwork"/>); empty leaves it out.</summary>
public readonly record struct EdgeSpan(float From, float To)
{
    public static readonly EdgeSpan None = new(0, 0);
    public bool IsEmpty => To - From <= 1e-3f;
}
