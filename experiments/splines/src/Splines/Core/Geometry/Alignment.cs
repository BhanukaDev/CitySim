using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// A point of intersection: a corner the player placed. <see cref="Radius"/> rounds it (0 = sharp), <see cref="Spiral"/>
/// is the transition length in and out (used from S7), and <see cref="Hard"/> is the player's Alt-click sharp corner.
/// Only interior PIs have a corner; the first and last are the ends.
/// </summary>
public readonly record struct Pi(Vector2 Position, float Radius = 0, float Spiral = 0, bool Hard = false);

/// <summary>
/// One interior PI's built corner, for feedback drawing: the tangent points where the arc meets the legs, the arc's
/// midpoint (where the radius knob sits), the radius built and the one asked for, and the turn (degrees, + = right).
/// A sharp corner has <see cref="Radius"/> 0 and <see cref="In"/> = <see cref="Out"/> = <see cref="Mid"/> = the PI.
/// </summary>
public readonly record struct CornerGeometry(Vector2 In, Vector2 Out, Vector2 Mid, float Radius, float Wanted, bool Clamped, float TurnDegrees);

/// <summary>
/// An edge's plan geometry, stored as PIs (DESIGN.md → Alignment) and derived into straights and arcs. A corner's arc
/// is clamped to the largest radius that fits: its tangent length may use at most half of each neighbouring leg, or the
/// whole leg where the neighbour is an end or runs straight through (it needs no tangent length, so a curve chained
/// on along its tangent gets the whole leg). <see cref="Rebuild"/> after changing <see cref="Pis"/>.
/// </summary>
public sealed class Alignment
{
    private float[] _effective = Array.Empty<float>();
    private bool[] _clamped = Array.Empty<bool>();
    private float[] _fit = Array.Empty<float>();
    private CornerGeometry[] _corners = Array.Empty<CornerGeometry>();
    private float[] _cornerStart = Array.Empty<float>(), _cornerEnd = Array.Empty<float>();

    public Alignment(IEnumerable<Pi> pis)
    {
        Pis = new List<Pi>(pis);
        Rebuild();
    }

    public List<Pi> Pis { get; }
    public Curve Curve { get; private set; } = new(Array.Empty<Segment>());
    public float Length => Curve.Length;

    /// <summary>The radius actually built at PI <paramref name="i"/> (0 for ends, hard or straight-through corners).</summary>
    public float EffectiveRadius(int i) => _effective[i];

    /// <summary>True when PI <paramref name="i"/>'s radius didn't fit and was reduced.</summary>
    public bool IsClamped(int i) => _clamped[i];

    /// <summary>The largest radius that fits at PI <paramref name="i"/> (∞ for ends and straight-through corners): the
    /// most Shift+wheel offers, so a radius never asks for more than it can build.</summary>
    public float MaxRadius(int i) => _fit[i];

    /// <summary>Where interior PI <paramref name="i"/>'s corner is on the road, a point a road can connect to: the PI
    /// itself where the road passes through it (a joint between chained curves, a sharp corner), else its arc's
    /// middle (where the radius knob sits).</summary>
    public Vector2 RoadPoint(int i) => _corners[i].Mid;

    /// <summary>Interior PI <paramref name="i"/> is built as an arc (not sharp, hard or straight through).</summary>
    public bool IsArc(int i) => i > 0 && i < Pis.Count - 1 && _effective[i] > 0;

    /// <summary>
    /// Where the middle of PI <paramref name="i"/>'s arc would be at radius <paramref name="radius"/>: on the corner's
    /// line of symmetry, <c>R · (1 / sin(θ/2) − 1)</c> in from the PI (θ the angle between the legs), so the PI itself at
    /// 0 and <see cref="RoadPoint"/> at the built radius. The bend slider's track (DESIGN.md → Junctions → Corner
    /// junctions) runs between the two.
    /// </summary>
    public Vector2 BendPoint(int i, float radius)
    {
        var (inward, factor) = BendAxis(i);
        return Pis[i].Position + inward * (radius * factor);
    }

    /// <summary>The radius whose arc middle is <paramref name="distance"/> in from PI <paramref name="i"/> (the
    /// inverse of <see cref="BendPoint"/>).</summary>
    public float BendRadiusAt(int i, float distance)
    {
        float factor = BendAxis(i).Factor;
        return factor > SplineMath.Epsilon ? distance / factor : 0;
    }

    /// <summary>PI <paramref name="i"/>'s line of symmetry, pointing into the corner, and how far along it the arc's
    /// middle moves per metre of radius.</summary>
    public (Vector2 Inward, float Factor) BendAxis(int i)
    {
        var p = Pis[i].Position;
        var toPrev = Pis[i - 1].Position - p;
        var toNext = Pis[i + 1].Position - p;
        if (toPrev.Length() < SplineMath.Epsilon || toNext.Length() < SplineMath.Epsilon) return (Vector2.Zero, 0);
        toPrev = Vector2.Normalize(toPrev);
        toNext = Vector2.Normalize(toNext);
        var sum = toPrev + toNext;
        if (sum.Length() < SplineMath.Epsilon) return (Vector2.Zero, 0);
        float sinHalf = MathF.Sqrt(MathF.Max(0, (1 - Vector2.Dot(toPrev, toNext)) / 2));
        return (Vector2.Normalize(sum), sinHalf > SplineMath.Epsilon ? 1 / sinHalf - 1 : 0);
    }

    /// <summary>The station of road point <paramref name="j"/>: 0 for the start, the length for the end, else
    /// <see cref="RoadPoint"/>'s (the middle of the corner's arc).</summary>
    public float RoadStation(int j) => j <= 0 ? 0 : j >= Pis.Count - 1 ? Length : (_cornerStart[j] + _cornerEnd[j]) / 2;

    /// <summary>The stretches between road points: stretch <c>k</c> runs from road point <c>k</c> to <c>k + 1</c>, one
    /// per leg. A straight edge is one stretch.</summary>
    public int StretchCount => Math.Max(1, Pis.Count - 1);

    /// <summary>The stretch station <paramref name="s"/> is on.</summary>
    public int StretchAt(float s)
    {
        int k = 0;
        for (int j = 1; j < Pis.Count - 1; j++)
            if (s >= RoadStation(j)) k = j;
        return k;
    }

    /// <summary>The built corner at interior PI <paramref name="i"/> (1 … Count − 2).</summary>
    public CornerGeometry Corner(int i) => _corners[i];

    /// <summary>Stations where interior PI <paramref name="i"/>'s arc starts and ends (equal at a sharp corner, where
    /// both are the PI's own station).</summary>
    public (float Start, float End) CornerStations(int i) => (_cornerStart[i], _cornerEnd[i]);

    public void Rebuild()
    {
        int n = Pis.Count;
        _effective = new float[n];
        _clamped = new bool[n];
        _fit = new float[n];
        Array.Fill(_fit, float.PositiveInfinity);
        _corners = new CornerGeometry[n];
        _cornerStart = new float[n];
        _cornerEnd = new float[n];
        var segments = new List<Segment>();
        float station = 0;
        if (n < 2) { Curve = new Curve(segments); return; }

        var cursor = Pis[0].Position;
        for (int i = 1; i < n - 1; i++)
        {
            var p = Pis[i].Position;
            _corners[i] = new CornerGeometry(p, p, p, 0, 0, false, 0);
            var legIn = p - Pis[i - 1].Position;
            var legOut = Pis[i + 1].Position - p;
            float lenIn = legIn.Length(), lenOut = legOut.Length();
            if (lenIn < SplineMath.Epsilon || lenOut < SplineMath.Epsilon) { AddLine(p); MarkSharp(i); continue; }
            var dirIn = legIn / lenIn;
            var dirOut = legOut / lenOut;

            float turn = SplineMath.Turn(dirIn, dirOut); // + = right
            float delta = MathF.Abs(turn);
            float radius = Pis[i].Hard ? 0 : Pis[i].Radius;
            _corners[i] = _corners[i] with { TurnDegrees = turn * 180f / MathF.PI };
            float tanHalf = MathF.Tan(delta / 2);
            float maxIn = i - 1 == 0 || StraightThrough(i - 1) ? lenIn : lenIn / 2;
            float maxOut = i + 1 == n - 1 || StraightThrough(i + 1) ? lenOut : lenOut / 2;
            float tMax = MathF.Min(maxIn, maxOut);
            if (delta >= 1e-4f && delta <= MathF.PI - 1e-3f) _fit[i] = tMax / tanHalf;
            // Straight through, sharp, or doubling back: no arc.
            if (radius <= 0 || delta < 1e-4f || delta > MathF.PI - 1e-3f) { AddLine(p); MarkSharp(i); continue; }

            float t = radius * tanHalf;
            if (t > tMax)
            {
                // A corner that fills its leg exactly (a split inside an arc builds these) lands a hair over in float:
                // that's the radius it asked for, not a clamp.
                _clamped[i] = t - tMax > 1e-3f * MathF.Max(1f, t);
                t = tMax;
                radius = t / tanHalf;
            }
            _effective[i] = radius;

            var a = p - dirIn * t;
            var left = SplineMath.Left(dirIn);
            var centre = turn < 0 ? a + left * radius : a - left * radius;
            AddLine(a);
            var arc = new ArcSegment(centre, radius, SplineMath.Angle(a - centre), turn);
            segments.Add(arc);
            _cornerStart[i] = station;
            station += arc.Length;
            _cornerEnd[i] = station;
            cursor = p + dirOut * t;
            _corners[i] = _corners[i] with
            {
                In = a, Out = cursor, Mid = arc.Sample(arc.Length / 2).Position,
                Radius = radius, Wanted = Pis[i].Radius, Clamped = _clamped[i],
            };
        }
        AddLine(Pis[n - 1].Position);
        Curve = new Curve(segments);

        void AddLine(Vector2 to)
        {
            if (Vector2.Distance(cursor, to) > SplineMath.Epsilon)
            {
                segments.Add(new LineSegment(cursor, to));
                station += Vector2.Distance(cursor, to);
            }
            cursor = to;
        }

        void MarkSharp(int i) => _cornerStart[i] = _cornerEnd[i] = station;
    }

    /// <summary>Interior PI <paramref name="j"/> doesn't turn (its legs run on in one line), so it takes no tangent length.</summary>
    private bool StraightThrough(int j)
    {
        if (j <= 0 || j >= Pis.Count - 1) return false;
        var legIn = Pis[j].Position - Pis[j - 1].Position;
        var legOut = Pis[j + 1].Position - Pis[j].Position;
        float lenIn = legIn.Length(), lenOut = legOut.Length();
        if (lenIn < SplineMath.Epsilon || lenOut < SplineMath.Epsilon) return false;
        return MathF.Abs(SplineMath.Turn(legIn / lenIn, legOut / lenOut)) < 1e-4f;
    }
}
