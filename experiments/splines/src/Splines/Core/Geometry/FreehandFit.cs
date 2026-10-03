using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Freehand mode (DESIGN.md → Modes, 3): a dragged stroke turned into a few PIs with fitted radii, so the result is an
/// ordinary alignment the Edit tool can reshape. The stroke is smoothed (hand wobble out), then simplified
/// (Ramer–Douglas–Peucker) to the points where it bends; each bend gets the radius of a circle fitted to the stroke
/// around it, and its PI is pushed outward so the arc passes through the stroke rather than cutting inside it. A bend
/// with no room for the profile's minimum radius is dropped: at that scale it's wobble, not a corner. Core-only.
/// </summary>
public static class FreehandFit
{
    /// <summary>Least distance between two recorded stroke samples (metres).</summary>
    public const float SampleSpacing = 2f;

    /// <summary>How far the simplified line may stray from the stroke: half the corridor, at least 2 m.</summary>
    public static float Tolerance(ProfileRules rules) => MathF.Max(2f, rules.Width / 2f);

    /// <summary>
    /// The alignment for a stroke. Every bend's radius is at least <paramref name="minRadius"/> (the profile's, or ~0
    /// with Anarchy) and at most what fits; the ends carry <paramref name="endRadius"/> (used when an end continues a
    /// dead end, as a Draw click's would).
    /// </summary>
    public static Alignment Fit(IReadOnlyList<Vector2> stroke, ProfileRules rules, float minRadius, float endRadius)
    {
        if (stroke.Count < 2) return new Alignment(stroke.Select(p => new Pi(p)));
        var samples = Smooth(stroke, Tolerance(rules));
        float tolerance = Tolerance(rules);
        var keep = Simplify(samples, tolerance);
        // A flick at either end (a leg of a few metres off a bend) isn't meant: the end leg takes it over.
        while (keep.Count > 2 && Vector2.Distance(samples[keep[^1]], samples[keep[^2]]) < 2 * tolerance) keep.RemoveAt(keep.Count - 2);
        while (keep.Count > 2 && Vector2.Distance(samples[keep[0]], samples[keep[1]]) < 2 * tolerance) keep.RemoveAt(1);
        var v = keep.Select(k => samples[k]).ToList();
        var pis = new List<Pi> { new(v[0], endRadius) };
        for (int i = 1; i < v.Count - 1; i++)
        {
            var dirIn = v[i] - v[i - 1];
            var dirOut = v[i + 1] - v[i];
            if (dirIn.Length() < SplineMath.Epsilon || dirOut.Length() < SplineMath.Epsilon) continue;
            dirIn = Vector2.Normalize(dirIn);
            dirOut = Vector2.Normalize(dirOut);
            float delta = MathF.Abs(SplineMath.Turn(dirIn, dirOut));
            // The stroke around this bend: from halfway along the leg before to halfway along the leg after.
            int from = (keep[i - 1] + keep[i]) / 2, to = (keep[i] + keep[i + 1]) / 2;
            float r = MathF.Max(CircleRadius(samples, from, to) ?? float.PositiveInfinity, minRadius);
            // Push the PI out along the bisector by the arc's external distance, so the arc's middle lands on the
            // stroke. A near-reversal would push it miles away: leave that one on the stroke.
            var at = v[i];
            if (float.IsFinite(r) && delta > 1e-3f && delta < MathF.PI * 2 / 3)
            {
                var outward = dirIn - dirOut;
                if (outward.Length() > SplineMath.Epsilon) at += Vector2.Normalize(outward) * (r * (1f / MathF.Cos(delta / 2) - 1f));
            }
            pis.Add(new Pi(at, float.IsFinite(r) ? r : 1e5f));
        }
        pis.Add(new Pi(v[^1], endRadius));

        // Bends with no room for the minimum radius go, tightest first (each one dropped gives its neighbours room).
        var a = new Alignment(pis);
        while (pis.Count > 2)
        {
            int worst = -1;
            for (int i = 1; i < pis.Count - 1; i++)
                if (a.MaxRadius(i) < minRadius - 1e-2f && (worst < 0 || a.MaxRadius(i) < a.MaxRadius(worst))) worst = i;
            if (worst < 0) break;
            pis.RemoveAt(worst);
            a = new Alignment(pis);
        }

        // A radius that doesn't fit is built at the most that fits; store that, so it isn't flagged as clamped.
        bool changed = false;
        for (int i = 1; i < pis.Count - 1; i++)
        {
            if (!a.IsClamped(i)) continue;
            pis[i] = pis[i] with { Radius = a.EffectiveRadius(i) };
            changed = true;
        }
        return changed ? new Alignment(pis) : a;
    }

    /// <summary>The stroke with each point averaged with those within <paramref name="reach"/> metres along it either
    /// side (hand wobble out); the ends stay put, since they're where the stroke snapped.</summary>
    public static List<Vector2> Smooth(IReadOnlyList<Vector2> pts, float reach)
    {
        var station = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++) station[i] = station[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
        var result = new List<Vector2>(pts.Count) { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            // Never reach past either end, so the smoothing doesn't drag the line's ends inward.
            float r = MathF.Min(reach, MathF.Min(station[i], station[^1] - station[i]));
            var sum = Vector2.Zero;
            int n = 0;
            for (int k = i; k >= 0 && station[i] - station[k] <= r; k--) { sum += pts[k]; n++; }
            for (int k = i + 1; k < pts.Count && station[k] - station[i] <= r; k++) { sum += pts[k]; n++; }
            result.Add(sum / n);
        }
        result.Add(pts[^1]);
        return result;
    }

    /// <summary>The indices of the stroke points kept by Ramer–Douglas–Peucker at <paramref name="tolerance"/>,
    /// first and last always.</summary>
    public static List<int> Simplify(IReadOnlyList<Vector2> pts, float tolerance)
    {
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            float worst = -1;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > worst) { worst = d; at = i; }
            }
            if (worst <= tolerance) continue;
            keep[at] = true;
            stack.Push((a, at));
            stack.Push((at, b));
        }
        return Enumerable.Range(0, pts.Count).Where(i => keep[i]).ToList();
    }

    /// <summary>The radius of the least-squares circle (Kåsa) through stroke points <paramref name="from"/> …
    /// <paramref name="to"/>, or null when they're too few or in a line.</summary>
    public static float? CircleRadius(IReadOnlyList<Vector2> pts, int from, int to)
    {
        int n = to - from + 1;
        if (n < 3) return null;
        var mean = Vector2.Zero;
        for (int i = from; i <= to; i++) mean += pts[i];
        mean /= n;
        // Solve for x² + y² + D·x + E·y + F = 0 in the least-squares sense (normal equations, about the mean).
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        for (int i = from; i <= to; i++)
        {
            double x = pts[i].X - mean.X, y = pts[i].Y - mean.Y, z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            sxz += x * z; syz += y * z; sz += z;
        }
        // | sxx sxy sx | |D|   |-sxz|
        // | sxy syy sy | |E| = |-syz|   (Cramer's rule)
        // | sx  sy  n  | |F|   |-sz |
        double det = Det(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
        if (Math.Abs(det) < 1e-9 * Math.Max(1, sxx * syy * n)) return null;
        double d = Det(-sxz, sxy, sx, -syz, syy, sy, -sz, sy, n) / det;
        double e = Det(sxx, -sxz, sx, sxy, -syz, sy, sx, -sz, n) / det;
        double f = Det(sxx, sxy, -sxz, sxy, syy, -syz, sx, sy, -sz) / det;
        double r2 = d * d / 4 + e * e / 4 - f;
        return r2 > 0 ? (float)Math.Sqrt(r2) : null;

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
