using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Corner junctions (DESIGN.md → Junctions → Corner junctions; <c>docs/corner-junctions.html</c>): in every mode, a
/// bend's dot is a slider out to its corner point. The cursor held on it reshapes the bend so the road passes through
/// it (previewed in place, the old shape replaced), and a draw starting or ending there makes its junction there:
/// <see cref="SplineGraph.SplitBend"/> runs before the leg is added, in the trial and in the build, as one undo step.
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>The bend the draw started on, until its first leg is built.</summary>
    private BendSlide? _startBend;
    /// <summary>What the network is showing reshaped (null: the built graph), and that graph, whose dots the
    /// overlay shows.</summary>
    private string? _bendShown;
    private SplineGraph? _bentGraph;

    /// <summary>The bends this draw's leg sits on: the start's (first leg only) and the cursor's.</summary>
    private IEnumerable<BendSlide> Bends(Alignment? drawn)
    {
        if (_startBend is { } s && _session.LegsBuilt == 0 && (drawn is null || Near(s.Position, drawn.Pis[0].Position))) yield return s;
        if (_snap?.Bend is { } e && (drawn is null || Near(e.Position, drawn.Pis[^1].Position))) yield return e;
    }

    /// <summary>Makes the junctions on the bends <paramref name="drawn"/>'s ends sit on, in <paramref name="g"/> (a
    /// copy of the built graph, or the graph itself when building).</summary>
    private void ApplyBends(SplineGraph g, Alignment drawn)
    {
        foreach (var b in Bends(drawn).ToList())
            if (g.Edges.FirstOrDefault(x => ReferenceEquals(x.Alignment, b.Alignment)) is { } edge)
                g.SplitBend(edge.Id, b.Pi, b.Radius);
    }

    /// <summary>Draws the network with the bends under the draw reshaped, while the slider is off the built radius;
    /// rebuilt only when that changes.</summary>
    private void ShowBends(Alignment? drawn)
    {
        if (Network is null) return;
        var reshaped = Bends(drawn).Where(b => b.Radius < b.Alignment.EffectiveRadius(b.Pi) - 1e-3f).ToList();
        string? key = reshaped.Count == 0 ? null
            : string.Join(";", reshaped.Select(b => $"{RuntimeHelpers.GetHashCode(b.Alignment)}:{b.Pi}:{b.Radius}"))
              + "|" + string.Join(",", Network.Hidden.OrderBy(id => id));
        if (key == _bendShown) return;
        _bendShown = key;
        _bentGraph = null;
        if (key is null) { Network.ShowTrial(null); return; }
        var g = Network.Graph.Clone();
        // Reshaped only, not split: the road stays one piece with its dot at the ghost until the leg is built.
        foreach (var b in reshaped)
            if (g.Edges.FirstOrDefault(x => ReferenceEquals(x.Alignment, b.Alignment)) is { } edge)
                g.ReshapeBend(edge.Id, b.Pi, b.Radius);
        Network.ShowTrial(g, hidden: true);
        _bentGraph = g;
    }

    /// <summary>Back to the built network, if a bend was showing reshaped.</summary>
    private void ClearBends()
    {
        if (_bendShown is null) return;
        _bendShown = null;
        _bentGraph = null;
        Network?.ShowTrial(null);
    }

    /// <summary>The slider the cursor is on, for the overlay: the track from the dot to the corner point, where the
    /// profile's minimum radius falls on it, and whether the cursor's radius is refused.</summary>
    private BendMark? BendMark()
    {
        if (_snap?.Bend is not { } b || Network is null) return null;
        var edge = Network.Graph.Edges.FirstOrDefault(x => ReferenceEquals(x.Alignment, b.Alignment));
        if (edge is null) return null;
        var a = b.Alignment;
        float built = a.EffectiveRadius(b.Pi), min = edge.Rules.MinRadius;
        bool anarchy = Testbed?.Anarchy == true;
        NumVector2? minAt = min > 0 && min < built ? a.BendPoint(b.Pi, min) : null;
        bool red = !anarchy && b.Radius > 0 && b.Radius < min - 1e-3f;
        return new BendMark(a.RoadPoint(b.Pi), a.Pis[b.Pi].Position, anarchy ? null : minAt ?? (min >= built ? a.RoadPoint(b.Pi) : null), b.Position, red);
    }

    /// <summary>The dots to show: the reshaped graph's while a bend is reshaped, so the old dot doesn't linger.</summary>
    private List<NumVector2> Dots() => SplineToolView.Points(_bentGraph ?? Network!.Graph);

    private static bool Near(NumVector2 a, NumVector2 b) => NumVector2.Distance(a, b) < SplineGraph.NodeTolerance;
}
