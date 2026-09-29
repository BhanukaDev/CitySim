using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>Small plan-view (x, z) helpers for the alignment geometry.</summary>
public static class SplineMath
{
    public const float Epsilon = 1e-4f;

    /// <summary>z of the 3D cross product: + when <paramref name="b"/> turns right of <paramref name="a"/> in the
    /// plan (x east, z south, seen from above).</summary>
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Left of travel for a tangent, seen from above: heading east (+x) gives north (−z).</summary>
    public static Vector2 Left(Vector2 tangent) => new(tangent.Y, -tangent.X);

    public static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    public static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

    /// <summary>Wraps an angle into (−π, π].</summary>
    public static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a <= -MathF.PI) a += MathF.Tau;
        else if (a > MathF.PI) a -= MathF.Tau;
        return a;
    }

    /// <summary>Signed turn from one direction to the next, in (−π, π].</summary>
    public static float Turn(Vector2 from, Vector2 to) => MathF.Atan2(Cross(from, to), Vector2.Dot(from, to));

    /// <summary>Where the infinite line <c>p + t·d</c> meets a circle, as <c>t</c> values (0, 1 or 2).</summary>
    public static IEnumerable<float> LineCircle(Vector2 p, Vector2 d, Vector2 centre, float radius)
    {
        var f = p - centre;
        float a = Vector2.Dot(d, d), b = 2 * Vector2.Dot(f, d), c = Vector2.Dot(f, f) - radius * radius;
        float disc = b * b - 4 * a * c;
        if (disc < 0 || a < Epsilon * Epsilon) yield break;
        float sq = MathF.Sqrt(disc);
        yield return (-b - sq) / (2 * a);
        if (sq > Epsilon) yield return (-b + sq) / (2 * a);
    }

    /// <summary>Where two circles meet (0, 1 or 2 points).</summary>
    public static IEnumerable<Vector2> CircleCircle(Vector2 c0, float r0, Vector2 c1, float r1)
    {
        var d = c1 - c0;
        float dist = d.Length();
        if (dist < Epsilon || dist > r0 + r1 + Epsilon || dist < MathF.Abs(r0 - r1) - Epsilon) yield break;
        float a = (r0 * r0 - r1 * r1 + dist * dist) / (2 * dist);
        float h = MathF.Sqrt(MathF.Max(0, r0 * r0 - a * a));
        var mid = c0 + d * (a / dist);
        var perp = new Vector2(-d.Y, d.X) / dist;
        yield return mid + perp * h;
        if (h > Epsilon) yield return mid - perp * h;
    }
}
