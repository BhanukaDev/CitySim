using System;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>A point on a curve: plan position, unit tangent, and signed curvature (+ = turning left, 1/R).</summary>
public readonly record struct CurveSample(Vector2 Position, Vector2 Tangent, float Curvature);

/// <summary>One piece of a <see cref="Curve"/>: a straight or a circular arc (clothoids come in S7).</summary>
public abstract class Segment
{
    public abstract float Length { get; }
    public abstract Vector2 Start { get; }
    public abstract Vector2 End { get; }

    /// <summary>The point at local distance <paramref name="s"/> (clamped to the segment).</summary>
    public abstract CurveSample Sample(float s);

    /// <summary>Local distance of the closest point to <paramref name="p"/>.</summary>
    public abstract float Closest(Vector2 p);

    /// <summary>The parallel segment <paramref name="d"/> metres to the left (right if negative); null when an arc
    /// would collapse (radius ≤ 0).</summary>
    public abstract Segment? Offset(float d);

    /// <summary>Radius of curvature (∞ for a straight).</summary>
    public abstract float Radius { get; }
}

public sealed class LineSegment : Segment
{
    public LineSegment(Vector2 a, Vector2 b) { A = a; B = b; }

    public Vector2 A { get; }
    public Vector2 B { get; }

    public override float Length => Vector2.Distance(A, B);
    public override Vector2 Start => A;
    public override Vector2 End => B;
    public override float Radius => float.PositiveInfinity;
    public Vector2 Direction => Length > SplineMath.Epsilon ? Vector2.Normalize(B - A) : Vector2.UnitX;

    public override CurveSample Sample(float s)
    {
        var dir = Direction;
        return new CurveSample(A + dir * Math.Clamp(s, 0, Length), dir, 0);
    }

    public override float Closest(Vector2 p) => Math.Clamp(Vector2.Dot(p - A, Direction), 0, Length);

    public override Segment Offset(float d)
    {
        var shift = SplineMath.Left(Direction) * d;
        return new LineSegment(A + shift, B + shift);
    }
}

/// <summary>A circular arc around <see cref="Centre"/>, starting at <see cref="StartAngle"/> (atan2 of z, x) and sweeping
/// <see cref="Sweep"/> radians. A positive sweep turns right in the plan (x east, z south, seen from above).</summary>
public sealed class ArcSegment : Segment
{
    public ArcSegment(Vector2 centre, float radius, float startAngle, float sweep)
    {
        Centre = centre; ArcRadius = radius; StartAngle = startAngle; Sweep = sweep;
    }

    public Vector2 Centre { get; }
    public float ArcRadius { get; }
    public float StartAngle { get; }
    public float Sweep { get; }

    public override float Length => ArcRadius * MathF.Abs(Sweep);
    public override Vector2 Start => PointAt(StartAngle);
    public override Vector2 End => PointAt(StartAngle + Sweep);
    public override float Radius => ArcRadius;
    /// <summary>Signed curvature: + turning left.</summary>
    public float Curvature => -MathF.Sign(Sweep) / ArcRadius;

    private Vector2 PointAt(float angle) => Centre + SplineMath.Direction(angle) * ArcRadius;

    public override CurveSample Sample(float s)
    {
        float t = Length > 0 ? Math.Clamp(s, 0, Length) / Length : 0;
        float angle = StartAngle + Sweep * t;
        var radial = SplineMath.Direction(angle);
        var tangent = new Vector2(-radial.Y, radial.X) * MathF.Sign(Sweep);
        return new CurveSample(Centre + radial * ArcRadius, tangent, Curvature);
    }

    /// <summary>Where <paramref name="p"/>'s angle falls along the sweep: 0 at the start, 1 at the end, outside
    /// [0, 1] when it's off the arc.</summary>
    public float ParamOf(Vector2 p)
    {
        float rel = SplineMath.Wrap(SplineMath.Angle(p - Centre) - StartAngle - Sweep / 2);
        return (rel + Sweep / 2) / Sweep;
    }

    public override float Closest(Vector2 p) => Math.Clamp(ParamOf(p), 0, 1) * Length;

    public override Segment? Offset(float d)
    {
        // The centre is on the left for a left turn, so offsetting left brings the arc closer to it.
        float r = ArcRadius - d * Curvature * ArcRadius;
        return r > SplineMath.Epsilon ? new ArcSegment(Centre, r, StartAngle, Sweep) : null;
    }
}
