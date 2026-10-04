using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Splines;

/// <summary>
/// An edge's height line (DESIGN.md → Vertical profile): world heights at even stations from start to end, level across
/// the edge (no roll). Made for one alignment, rules, pair of node heights and pair of junction cut-backs; a change to
/// any of them makes it stale (<see cref="Fits"/>) and <see cref="Vertical.Conform"/> makes a new one. Once made it
/// stays, whatever the ground does later: the ground is shaped to the line, never the line to the ground.
/// </summary>
public sealed record EdgeHeights(Alignment Alignment, ProfileRules Rules, float Start, float End, float CutStart, float CutEnd,
    float[] Heights)
{
    public float Spacing => Heights.Length > 1 ? Alignment.Length / (Heights.Length - 1) : 1f;

    /// <summary>The deepest cut (natural ground above the line) when the line was made, in metres, and where it is.</summary>
    public (float Depth, float S) DeepestCut { get; init; }
    /// <summary>The highest fill (natural ground below the line) when the line was made, in metres, and where it is.</summary>
    public (float Depth, float S) HighestFill { get; init; }

    /// <summary>The height at a distance along the edge.</summary>
    public float At(float s)
    {
        if (Heights.Length == 1) return Heights[0];
        float f = Math.Clamp(s / Spacing, 0, Heights.Length - 1);
        int i = Math.Min((int)f, Heights.Length - 2);
        return Heights[i] + (Heights[i + 1] - Heights[i]) * (f - i);
    }

    /// <summary>The steepest grade (a fraction) between two stations, and where along the edge it is.</summary>
    public (float Grade, float S) Steepest()
    {
        (float g, float s) best = (0, 0);
        for (int i = 0; i + 1 < Heights.Length; i++)
        {
            float g = MathF.Abs(Heights[i + 1] - Heights[i]) / Spacing;
            if (g > best.g) best = (g, (i + 0.5f) * Spacing);
        }
        return best;
    }

    public bool Fits(GraphEdge e, float start, float end, float cutStart, float cutEnd) =>
        ReferenceEquals(Alignment, e.Alignment) && ReferenceEquals(Rules, e.Rules) && Near(Start, start) && Near(End, end)
        && Near(CutStart, cutStart) && Near(CutEnd, cutEnd);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
}

/// <summary>
/// Gives every node a height and every edge a height line, keeping the ones still valid (DESIGN.md → Vertical profile,
/// <c>Ground</c> stations). A new or moved node takes the ground under it, exactly where it was put: the line never moves
/// a node to make a grade fit, validation marks it red instead. An edge's line is the ground along its centre averaged
/// over <see cref="ProfileRules.GroundSmoothing"/> (to take out bumps), bent to meet its nodes' heights, held level over
/// each junction's cut-back (junctions are flat plates), then limited to the max grade and rounded at crests and sags.
/// Ground under existing roads has been shaped to them, so a road split or met by a new one keeps its height.
/// </summary>
public static class Vertical
{
    /// <summary>Metres between stations of a height line.</summary>
    public const float Spacing = 2f;

    /// <summary>Fills in missing and stale heights. Returns the edges given a new line and the nodes given a new height or
    /// at the end of such an edge (what the ground shaping has to redo).</summary>
    public static (List<int> Edges, List<int> Nodes) Conform(SplineGraph g, IReadOnlyDictionary<int, JunctionFootprint> footprints, IGround ground)
    {
        var nodes = new HashSet<int>();
        foreach (var node in g.Nodes)
        {
            if (node.Height is not null) continue;
            node.Level = new NodeLevel(node.Position, ground.GetHeight(node.Position));
            nodes.Add(node.Id);
        }

        var edges = new List<int>();
        foreach (var e in g.Edges.ToList())
        {
            float h0 = g.Node(e.Start).Height!.Value, h1 = g.Node(e.End).Height!.Value;
            var (c0, c1) = Junctions.CutBacks(e, footprints);
            if (e.Heights?.Fits(e, h0, h1, c0, c1) == true) continue;
            g.SetHeights(e.Id, Line(e, h0, h1, c0, c1, ground));
            edges.Add(e.Id);
            nodes.Add(e.Start);
            nodes.Add(e.End);
        }
        return (edges, nodes.ToList());
    }

    private static EdgeHeights Line(GraphEdge e, float h0, float h1, float cutStart, float cutEnd, IGround ground)
    {
        var curve = e.Alignment.Curve;
        float len = curve.Length;
        int n = Math.Max(1, (int)MathF.Ceiling(len / Spacing));
        float ds = len / n;
        var raw = new float[n + 1];
        for (int i = 0; i <= n; i++) raw[i] = ground.GetHeight(curve.Sample(i * ds).Position);

        // The ground averaged over the smoothing distance (infinite = one level for the whole edge).
        var sum = new float[n + 2];
        for (int i = 0; i <= n; i++) sum[i + 1] = sum[i] + raw[i];
        float window = e.Rules.GroundSmoothing;
        int k = float.IsPositiveInfinity(window) ? n : (int)(window / 2 / ds);
        var smooth = new float[n + 1];
        for (int i = 0; i <= n; i++)
        {
            int a = Math.Max(0, i - k), b = Math.Min(n, i + k);
            smooth[i] = (sum[b + 1] - sum[a]) / (b - a + 1);
        }

        // Level at the nodes' heights over the cut-backs, the smoothed ground bent to meet them in between.
        float flatTo = Math.Min(cutStart, len / 2), flatFrom = Math.Max(len - cutEnd, len / 2);
        // Whole stations past each cut-back, so the line is exactly level where the road meets its junction.
        int ia = Math.Clamp((int)MathF.Ceiling(flatTo / ds - 1e-4f), 0, n), ib = Math.Clamp((int)MathF.Floor(flatFrom / ds + 1e-4f), ia, n);
        float d0 = h0 - smooth[ia], d1 = h1 - smooth[ib];
        var h = new float[n + 1];
        var pinned = new bool[n + 1];
        for (int i = 0; i <= n; i++)
        {
            if (i <= ia) (h[i], pinned[i]) = (h0, true);
            else if (i >= ib) (h[i], pinned[i]) = (h1, true);
            else h[i] = smooth[i] + d0 + (d1 - d0) * (i - ia) / (float)(ib - ia);
        }

        // Ends too far apart in height for the max grade: one even ramp between them, so the line shows the grade they
        // need (and a road built anyway with Anarchy has no step in it).
        if (e.Rules.MaxGrade is { } maxGrade && MathF.Abs(h1 - h0) > maxGrade * (ib - ia) * ds + 1e-3f)
        {
            for (int i = ia + 1; i < ib; i++) h[i] = h0 + (h1 - h0) * (i - ia) / (ib - ia);
            return Measured(e, h0, h1, cutStart, cutEnd, h, raw, ds);
        }
        LimitGrade(e.Rules.MaxGrade, h, pinned, ds);
        // The limit leaves sharp crests and sags where it meets the ground; averaging rounds them into vertical curves
        // (an average of a line within the grade stays within it), then the limit again for the joins to the pins.
        if (k > 0)
        {
            var round = new float[n + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i <= n; i++)
                {
                    if (pinned[i]) { round[i] = h[i]; continue; }
                    int a = Math.Max(0, i - k), b = Math.Min(n, i + k);
                    float total = 0;
                    for (int j = a; j <= b; j++) total += h[j];
                    round[i] = total / (b - a + 1);
                }
                Array.Copy(round, h, n + 1);
            }
            LimitGrade(e.Rules.MaxGrade, h, pinned, ds);
        }
        return Measured(e, h0, h1, cutStart, cutEnd, h, raw, ds);
    }

    /// <summary>The line, with its deepest cut and highest fill against the natural ground.</summary>
    private static EdgeHeights Measured(GraphEdge e, float h0, float h1, float cutStart, float cutEnd, float[] h, float[] raw, float ds)
    {
        int n = h.Length - 1;
        (float, float) cut = (0, 0), fill = (0, 0);
        for (int i = 0; i <= n; i++)
        {
            float d = raw[i] - h[i];
            if (d > cut.Item1) cut = (d, i * ds);
            if (-d > fill.Item1) fill = (-d, i * ds);
        }
        return new EdgeHeights(e.Alignment, e.Rules, h0, h1, cutStart, cutEnd, h) { DeepestCut = cut, HighestFill = fill };
    }

    /// <summary>No steeper than the max grade, leaving the pinned stations (close enough in height for it; ends too far
    /// apart get an even ramp instead).</summary>
    private static void LimitGrade(float? maxGrade, float[] h, bool[] pinned, float ds)
    {
        if (maxGrade is not { } grade) return;
        float step = grade * ds;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < h.Length; i++)
                if (!pinned[i]) h[i] = Math.Clamp(h[i], h[i - 1] - step, h[i - 1] + step);
            for (int i = h.Length - 2; i >= 0; i--)
                if (!pinned[i]) h[i] = Math.Clamp(h[i], h[i + 1] - step, h[i + 1] + step);
        }
    }
}
