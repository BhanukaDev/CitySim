using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

public sealed partial class SplineGraph
{
    /// <summary>Sample spacing for <see cref="RunsAlong"/>, metres.</summary>
    private const float AlongStep = 0.5f;
    /// <summary>A run shorter than this is a crossing, not a road lying along the line.</summary>
    private const float MinRun = 2f;
    private static readonly float AlongCos = MathF.Cos(3f * MathF.PI / 180f);

    /// <summary>
    /// Where the straight line <paramref name="a"/>→<paramref name="b"/> lies on built roads that a spline of
    /// <paramref name="rules"/>' profile <see cref="Connects"/> to: centre lines within <see cref="EdgeTolerance"/>
    /// and parallel. Each run is stations along the line (ends moved onto a node of the road where one is close, so a
    /// piece added up to there joins it) and the widest road in it. Grid mode uses it to reuse a road it is placed on.
    /// </summary>
    public List<(float From, float To, float Width)> RunsAlong(Vector2 a, Vector2 b, ProfileRules rules)
    {
        var runs = new List<(float From, float To, float Width)>();
        float length = Vector2.Distance(a, b);
        if (length < MinRun) return runs;
        var dir = (b - a) / length;
        var edges = _edges.Values.Where(e => Connects(rules, e.Rules)).ToList();
        int n = (int)MathF.Ceiling(length / AlongStep);
        float? from = null, width = 0;
        for (int i = 0; i <= n; i++)
        {
            float s = MathF.Min(i * AlongStep, length);
            var p = a + dir * s;
            float? w = null;
            foreach (var e in edges)
            {
                var c = e.Alignment.Curve;
                var cp = c.ClosestPoint(p);
                if (MathF.Abs(cp.Offset) > EdgeTolerance) continue;
                if (MathF.Abs(Vector2.Dot(c.Sample(cp.S).Tangent, dir)) < AlongCos) continue;
                w = MathF.Max(w ?? 0, e.Rules.Width);
            }
            if (w is { } ww) { from ??= s; width = MathF.Max(width ?? 0, ww); }
            if (from is { } f && (w is null || i == n))
            {
                float to = w is null ? s - AlongStep : s;
                if (to - f >= MinRun) runs.Add((OnNode(f), OnNode(to), width ?? 0));
                from = null;
                width = 0;
            }
        }
        return runs;

        // A run's end near a node of the road snaps to it, so the piece of the line beyond it starts on that node.
        float OnNode(float s)
        {
            var p = a + dir * s;
            var near = _nodes.Values.Where(nd => Vector2.Distance(nd.Position, p) < AlongStep + NodeTolerance)
                .OrderBy(nd => Vector2.Distance(nd.Position, p)).FirstOrDefault();
            return near is null ? s : Math.Clamp(Vector2.Dot(near.Position - a, dir), 0, length);
        }
    }
}
