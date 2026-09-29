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
/// An edge's plan geometry, stored as PIs (DESIGN.md → Alignment) and derived into straights and arcs. A corner's arc
/// is clamped to the largest radius that fits: its tangent length may use at most half of each neighbouring leg, or the
/// whole leg where the neighbour is an end. <see cref="Rebuild"/> after changing <see cref="Pis"/>.
/// </summary>
public sealed class Alignment
{
    private float[] _effective = Array.Empty<float>();
    private bool[] _clamped = Array.Empty<bool>();

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

    public void Rebuild()
    {
        int n = Pis.Count;
        _effective = new float[n];
        _clamped = new bool[n];
        var segments = new List<Segment>();
        if (n < 2) { Curve = new Curve(segments); return; }

        var cursor = Pis[0].Position;
        for (int i = 1; i < n - 1; i++)
        {
            var p = Pis[i].Position;
            var legIn = p - Pis[i - 1].Position;
            var legOut = Pis[i + 1].Position - p;
            float lenIn = legIn.Length(), lenOut = legOut.Length();
            if (lenIn < SplineMath.Epsilon || lenOut < SplineMath.Epsilon) { AddLine(p); continue; }
            var dirIn = legIn / lenIn;
            var dirOut = legOut / lenOut;

            float turn = SplineMath.Turn(dirIn, dirOut); // + = right
            float delta = MathF.Abs(turn);
            float radius = Pis[i].Hard ? 0 : Pis[i].Radius;
            // Straight through, sharp, or doubling back: no arc.
            if (radius <= 0 || delta < 1e-4f || delta > MathF.PI - 1e-3f) { AddLine(p); continue; }

            float tanHalf = MathF.Tan(delta / 2);
            float maxIn = i - 1 == 0 ? lenIn : lenIn / 2;
            float maxOut = i + 1 == n - 1 ? lenOut : lenOut / 2;
            float tMax = MathF.Min(maxIn, maxOut);
            float t = radius * tanHalf;
            if (t > tMax)
            {
                t = tMax;
                radius = t / tanHalf;
                _clamped[i] = true;
            }
            _effective[i] = radius;

            var a = p - dirIn * t;
            var left = SplineMath.Left(dirIn);
            var centre = turn < 0 ? a + left * radius : a - left * radius;
            AddLine(a);
            segments.Add(new ArcSegment(centre, radius, SplineMath.Angle(a - centre), turn));
            cursor = p + dirOut * t;
        }
        AddLine(Pis[n - 1].Position);
        Curve = new Curve(segments);

        void AddLine(Vector2 to)
        {
            if (Vector2.Distance(cursor, to) > SplineMath.Epsilon) segments.Add(new LineSegment(cursor, to));
            cursor = to;
        }
    }
}
