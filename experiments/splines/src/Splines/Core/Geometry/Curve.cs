using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>The closest point on a curve: station <see cref="S"/>, signed distance (+ = left of travel), position.</summary>
public readonly record struct CurvePoint(float S, float Offset, Vector2 Position);

/// <summary>Where two curves cross: the station on each, and the point.</summary>
public readonly record struct CurveHit(float SA, float SB, Vector2 Point);

/// <summary>
/// A chain of straights and arcs with stations (distance along it). What an <see cref="Alignment"/> derives, and what
/// its offsets are. Simple lists and linear searches; spatial indexes wait for S11.
/// </summary>
public sealed class Curve
{
    private readonly List<Segment> _segments;
    private readonly float[] _starts;

    public Curve(IEnumerable<Segment> segments)
    {
        _segments = new List<Segment>(segments);
        _starts = new float[_segments.Count];
        float s = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            _starts[i] = s;
            s += _segments[i].Length;
        }
        Length = s;
    }

    public IReadOnlyList<Segment> Segments => _segments;
    public float Length { get; }

    /// <summary>Station where segment <paramref name="i"/> starts.</summary>
    public float StartOf(int i) => _starts[i];

    public CurveSample Sample(float s)
    {
        if (_segments.Count == 0) return default;
        int i = IndexAt(s);
        return _segments[i].Sample(s - _starts[i]);
    }

    /// <summary>Samples from 0 to the end every <paramref name="spacing"/> metres, always including both ends and every
    /// segment boundary (so corners and arc ends are exact).</summary>
    public List<(float S, CurveSample Sample)> SampleEvery(float spacing)
    {
        var result = new List<(float, CurveSample)>();
        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            int n = Math.Max(1, (int)MathF.Ceiling(seg.Length / MathF.Max(spacing, 0.01f)));
            for (int k = i == 0 ? 0 : 1; k <= n; k++)
            {
                float local = seg.Length * k / n;
                result.Add((_starts[i] + local, seg.Sample(local)));
            }
        }
        return result;
    }

    public CurvePoint ClosestPoint(Vector2 p)
    {
        var best = new CurvePoint(0, 0, default);
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i < _segments.Count; i++)
        {
            float local = _segments[i].Closest(p);
            var sample = _segments[i].Sample(local);
            float dist = Vector2.DistanceSquared(p, sample.Position);
            if (dist >= bestDist) continue;
            bestDist = dist;
            float side = Vector2.Dot(p - sample.Position, SplineMath.Left(sample.Tangent));
            best = new CurvePoint(_starts[i] + local, MathF.Sign(side) * MathF.Sqrt(dist), sample.Position);
        }
        return best;
    }

    /// <summary>
    /// The parallel curve <paramref name="d"/> metres to the left (right if negative). Straights and arcs offset exactly;
    /// an arc that would collapse is dropped, and wherever two neighbouring straights no longer meet (a hard corner, or a
    /// dropped arc) they're trimmed or extended to where their lines cross.
    /// </summary>
    public Curve Offset(float d)
    {
        var parts = new List<Segment>();
        foreach (var seg in _segments)
            if (seg.Offset(d) is { } o) parts.Add(o);

        for (int i = 0; i + 1 < parts.Count; i++)
        {
            if (Vector2.Distance(parts[i].End, parts[i + 1].Start) < 1e-3f) continue;
            if (parts[i] is LineSegment a && parts[i + 1] is LineSegment b &&
                LineLineInfinite(a.A, a.Direction, b.A, b.Direction) is { } x)
            {
                parts[i] = new LineSegment(a.A, x);
                parts[i + 1] = new LineSegment(x, b.B);
            }
        }
        return new Curve(parts);
    }

    /// <summary>Every point where this curve crosses <paramref name="other"/> (pairwise over segments).</summary>
    public List<CurveHit> Intersect(Curve other)
    {
        var hits = new List<CurveHit>();
        for (int i = 0; i < _segments.Count; i++)
            for (int j = 0; j < other._segments.Count; j++)
                foreach (var p in SegmentHits(_segments[i], other._segments[j]))
                {
                    float sa = _starts[i] + _segments[i].Closest(p), sb = other._starts[j] + other._segments[j].Closest(p);
                    // A hit exactly at a shared segment boundary is found by both segments: keep one.
                    if (hits.Exists(h => Vector2.Distance(h.Point, p) < 1e-3f)) continue;
                    hits.Add(new CurveHit(sa, sb, p));
                }
        hits.Sort((a, b) => a.SA.CompareTo(b.SA));
        return hits;
    }

    /// <summary>Where this curve crosses itself: hits between segments that aren't neighbours.</summary>
    public List<CurveHit> SelfIntersect()
    {
        var hits = new List<CurveHit>();
        for (int i = 0; i < _segments.Count; i++)
            for (int j = i + 2; j < _segments.Count; j++)
                foreach (var p in SegmentHits(_segments[i], _segments[j]))
                    hits.Add(new CurveHit(_starts[i] + _segments[i].Closest(p), _starts[j] + _segments[j].Closest(p), p));
        return hits;
    }

    /// <summary>The tightest radius between stations <paramref name="s0"/> and <paramref name="s1"/> (∞ if straight).</summary>
    public float MinRadius(float s0, float s1)
    {
        if (s1 < s0) (s0, s1) = (s1, s0);
        float min = float.PositiveInfinity;
        for (int i = 0; i < _segments.Count; i++)
        {
            float a = _starts[i], b = a + _segments[i].Length;
            if (b <= s0 || a >= s1) continue;
            min = MathF.Min(min, _segments[i].Radius);
        }
        return min;
    }

    public float MinRadius() => MinRadius(0, Length);

    private int IndexAt(float s)
    {
        for (int i = _segments.Count - 1; i > 0; i--)
            if (s >= _starts[i]) return i;
        return 0;
    }

    private static Vector2? LineLineInfinite(Vector2 p, Vector2 dp, Vector2 q, Vector2 dq)
    {
        float den = SplineMath.Cross(dp, dq);
        if (MathF.Abs(den) < 1e-6f) return null;
        float t = SplineMath.Cross(q - p, dq) / den;
        return p + dp * t;
    }

    private const float Tol = 1e-4f;

    private static IEnumerable<Vector2> SegmentHits(Segment a, Segment b)
    {
        switch (a, b)
        {
            case (LineSegment la, LineSegment lb):
            {
                var da = la.B - la.A; var db = lb.B - lb.A;
                float den = SplineMath.Cross(da, db);
                if (MathF.Abs(den) < 1e-9f) yield break; // parallel or overlapping: not a crossing
                float t = SplineMath.Cross(lb.A - la.A, db) / den, u = SplineMath.Cross(lb.A - la.A, da) / den;
                if (t is >= -Tol and <= 1 + Tol && u is >= -Tol and <= 1 + Tol) yield return la.A + da * t;
                break;
            }
            case (LineSegment l, ArcSegment arc):
                foreach (var p in LineArc(l, arc)) yield return p;
                break;
            case (ArcSegment arc, LineSegment l):
                foreach (var p in LineArc(l, arc)) yield return p;
                break;
            case (ArcSegment aa, ArcSegment ab):
                foreach (var p in SplineMath.CircleCircle(aa.Centre, aa.ArcRadius, ab.Centre, ab.ArcRadius))
                    if (OnArc(aa, p) && OnArc(ab, p)) yield return p;
                break;
        }
    }

    private static IEnumerable<Vector2> LineArc(LineSegment l, ArcSegment arc)
    {
        var d = l.B - l.A;
        foreach (float t in SplineMath.LineCircle(l.A, d, arc.Centre, arc.ArcRadius))
        {
            if (t < -Tol || t > 1 + Tol) continue;
            var p = l.A + d * t;
            if (OnArc(arc, p)) yield return p;
        }
    }

    private static bool OnArc(ArcSegment arc, Vector2 p) => arc.ParamOf(p) is >= -Tol and <= 1 + Tol;
}
