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
/// whole leg where the neighbour is an end. <see cref="Rebuild"/> after changing <see cref="Pis"/>.
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
            float maxIn = i - 1 == 0 ? lenIn : lenIn / 2;
            float maxOut = i + 1 == n - 1 ? lenOut : lenOut / 2;
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
}
