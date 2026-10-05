using System;
using System.Collections.Generic;
using System.Numerics;
using CitySim.Splines;

namespace CitySim.Zoning;

/// <summary>
/// The frontage runs of a road network: each edge's two sides, at the back of the corridor (half the profile's width
/// from the centre line, after the edge's offset), between its junction cut-backs. Reads the splines graph only, so
/// it stays Godot-free. In Z2 runs meet at block corners; for now each edge side is its own run.
/// </summary>
public static class RoadFrontage
{
    /// <summary>Spacing of the points along a run, in metres. Curves are arcs, so this only sets how smooth they are.</summary>
    public const float Spacing = 2f;

    /// <param name="rowsOf">The most rows a profile's sides may have (a road type's <c>ZoneRows</c>; 0 = none).</param>
    public static List<FrontageRun> Runs(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        Func<string, int> rowsOf)
    {
        var runs = new List<FrontageRun>();
        foreach (var e in graph.Edges)
        {
            int rows = rowsOf(e.Rules.Id);
            if (rows <= 0) continue;
            var curve = e.Alignment.Curve;
            var (cutStart, cutEnd) = Junctions.CutBacks(e, footprints);
            float s0 = cutStart, s1 = curve.Length - cutEnd;
            if (s1 - s0 < 1f) continue;

            int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / Spacing));
            var left = new Vector2[n + 1];
            var right = new Vector2[n + 1];
            float half = e.Rules.Width / 2;
            for (int k = 0; k <= n; k++)
            {
                var sample = curve.Sample(s0 + (s1 - s0) * k / n);
                var l = SplineMath.Left(sample.Tangent);
                left[k] = sample.Position + l * (e.Offset + half);
                right[k] = sample.Position + l * (e.Offset - half);
            }
            runs.Add(new FrontageRun($"e{e.Id}L", left, +1, rows));
            runs.Add(new FrontageRun($"e{e.Id}R", right, -1, rows));
        }
        return runs;
    }
}
