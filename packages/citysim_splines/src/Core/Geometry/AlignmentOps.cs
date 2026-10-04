using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Splitting, reversing and joining alignments for the graph (DESIGN.md → Graph: crossing or ending on an edge splits
/// it; deleting an edge can merge two back into one). Each returns new alignments and keeps the built shape: the
/// corners next to a cut keep the radius they were built with, so a clamped corner doesn't spring back to its wanted
/// radius once the leg it was sharing becomes an end leg.
/// </summary>
public static class AlignmentOps
{
    /// <summary>The same curve run the other way.</summary>
    public static Alignment Reversed(Alignment a) => new(Enumerable.Reverse(a.Pis));

    /// <summary>
    /// Cuts at station <paramref name="s"/> into two alignments that meet there. A cut on a straight adds an end PI at
    /// the point. A cut inside a corner's arc replaces that PI with two, where the tangent at the cut meets each leg,
    /// both at the built radius, so the two halves are the two parts of the same arc.
    /// Known limit: with the half-leg clamp rule, the corner before (or after) such an arc may lose room and clamp
    /// tighter if it was already using half its leg.
    /// </summary>
    public static (Alignment Left, Alignment Right) SplitAt(Alignment a, float s)
    {
        var pis = a.Pis;
        var p = a.Curve.Sample(s).Position;
        for (int i = 1; i < pis.Count - 1; i++)
        {
            var (s0, s1) = a.CornerStations(i);
            if (s <= s0 + 1e-3f || s >= s1 - 1e-3f) continue;
            var c = a.Corner(i);
            var tangent = a.Curve.Sample(s).Tangent;
            float r = c.Radius;
            float swept = (s - s0) / r, rest = (s1 - s) / r;
            var inPi = p - tangent * (r * MathF.Tan(swept / 2));
            var outPi = p + tangent * (r * MathF.Tan(rest / 2));
            var left = pis.Take(i).Select((q, k) => k == i - 1 ? Built(a, k, q) : q).Append(new Pi(inPi, r, pis[i].Spiral)).Append(new Pi(p));
            var right = new[] { new Pi(p), new Pi(outPi, r, pis[i].Spiral) }.Concat(pis.Skip(i + 1).Select((q, k) => k == 0 ? Built(a, i + 1, q) : q));
            return (new Alignment(left), new Alignment(right));
        }

        // On a straight (or exactly at a sharp corner): find the leg by the stations of the corners around it.
        int leg = 0;
        for (int i = 1; i < pis.Count - 1; i++)
            if (s >= a.CornerStations(i).End - 1e-3f) leg = i;
        var l = pis.Take(leg + 1).Select((q, k) => k == leg ? Built(a, k, q) : q).ToList();
        var rt = pis.Skip(leg + 1).Select((q, k) => k == 0 ? Built(a, leg + 1, q) : q).ToList();
        if (Vector2.Distance(l[^1].Position, p) > 1e-3f) l.Add(new Pi(p));
        else l[^1] = new Pi(p);
        if (rt.Count == 0 || Vector2.Distance(rt[0].Position, p) > 1e-3f) rt.Insert(0, new Pi(p));
        else rt[0] = new Pi(p);
        return (new Alignment(l), new Alignment(rt));
    }

    /// <summary>The part of <paramref name="a"/> between stations <paramref name="s0"/> and <paramref name="s1"/>.</summary>
    public static Alignment Between(Alignment a, float s0, float s1)
    {
        var right = s0 > 1e-3f ? SplitAt(a, s0).Right : a;
        float length = s1 - MathF.Max(s0, 0);
        return length < right.Length - 1e-3f ? SplitAt(right, length).Left : right;
    }

    /// <summary>
    /// Joins <paramref name="a"/> (ending where <paramref name="b"/> starts) into one alignment. When the two PIs
    /// either side of the joint are the halves of one arc (as <see cref="SplitAt"/> made them), they fold back into
    /// the original corner, so a split then a merge gives the same PIs.
    /// </summary>
    public static Alignment Join(Alignment a, Alignment b)
    {
        var joint = a.Pis[^1].Position;
        var pis = a.Pis.Take(a.Pis.Count - 1).Append(new Pi(joint)).Concat(b.Pis.Skip(1)).ToList();
        var merged = new Alignment(pis);
        int j = a.Pis.Count - 1; // the joint's index
        if (j < 2 || j > pis.Count - 3) return merged;
        Pi before = pis[j - 1], after = pis[j + 1];
        if (before.Hard || after.Hard || before.Radius <= 0 || MathF.Abs(before.Radius - after.Radius) > 1e-2f) return merged;
        if (Cross(pis[j - 2].Position, before.Position, after.Position, pis[j + 2].Position) is not { } corner) return merged;
        var folded = pis.Take(j - 1).Append(new Pi(corner, before.Radius, before.Spiral)).Concat(pis.Skip(j + 2)).ToList();
        var candidate = new Alignment(folded);
        // Compare with the two halves as built (the unfolded join may clamp its arc halves against the joint PI).
        bool same = MathF.Abs(candidate.Length - (a.Length + b.Length)) < 1e-2f &&
                    Vector2.Distance(candidate.Curve.ClosestPoint(joint).Position, joint) < 1e-2f;
        return same ? candidate : merged;
    }

    /// <summary>The same alignment with every corner pinned to the radius it was built with (for an edge that
    /// becomes part of a longer one, so a clamped corner doesn't spring back when its legs change).</summary>
    public static Alignment Pinned(Alignment a) => new(a.Pis.Select((q, k) => Built(a, k, q)));

    /// <summary>PI <paramref name="k"/> with its radius pinned to what was built (ends and sharp corners unchanged): for
    /// the corners next to a cut, whose leg changes.</summary>
    private static Pi Built(Alignment a, int k, Pi pi) =>
        k == 0 || k == a.Pis.Count - 1 || pi.Hard || a.EffectiveRadius(k) <= 0 ? pi : pi with { Radius = a.EffectiveRadius(k) };

    /// <summary>Where line p0→p1 meets line q0→q1 (null if parallel).</summary>
    private static Vector2? Cross(Vector2 p0, Vector2 p1, Vector2 q0, Vector2 q1)
    {
        var dp = p1 - p0;
        var dq = q1 - q0;
        float den = SplineMath.Cross(dp, dq);
        if (MathF.Abs(den) < 1e-6f) return null;
        return p0 + dp * (SplineMath.Cross(q0 - p0, dq) / den);
    }
}
