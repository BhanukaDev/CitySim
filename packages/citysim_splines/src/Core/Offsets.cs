using System;
using System.Collections.Generic;
using System.Linq;

namespace CitySim.Splines;

/// <summary>
/// An offset picked with the mouse (<see cref="Offsets.Pick"/>): the offset, the range it was held to
/// (<see cref="Min"/>..<see cref="Max"/>), what it snapped to (empty for none) and the offsets it would snap to, for the
/// overlay's bar. All across the same heading, metres to the left (<see cref="GraphEdge.Offset"/>).
/// </summary>
public readonly record struct OffsetPick(float Offset, float Min, float Max, string Snap, IReadOnlyList<float> Marks);

/// <summary>
/// Moving a piece of road sideways (DESIGN.md → Offsets, the CS2 way): the mouse's position across the road is where the
/// new piece's centre goes, and the piece stays parallel to its nodes' line. Its centre line has to stay on the wider of
/// it and each road it runs on into (half that one's width either side of its centre), so two roads of one width can
/// still slide against each other. It snaps to centres and to lined-up sides, else to whole half metres.
/// </summary>
public static class Offsets
{
    /// <summary>Plain steps.</summary>
    public const float Step = 0.5f;
    /// <summary>How near a snap catches.</summary>
    public const float Catch = 0.75f;

    /// <summary>
    /// The offset for a cursor <paramref name="raw"/> metres left of the alignment, for a piece
    /// <paramref name="half"/> wide either side, next to the roads it runs on into (<paramref name="neighbours"/>: each
    /// one's offset across the same heading and half width). With none, within its own half width of the alignment.
    /// </summary>
    public static OffsetPick Pick(float raw, float half, IReadOnlyList<(float Offset, float Half)> neighbours)
    {
        float lo = -half, hi = half;
        if (neighbours.Count > 0)
        {
            lo = float.NegativeInfinity;
            hi = float.PositiveInfinity;
            foreach (var (o, h) in neighbours)
            {
                float m = MathF.Max(half, h);
                lo = MathF.Max(lo, o - m);
                hi = MathF.Min(hi, o + m);
            }
            if (lo > hi) lo = hi = (lo + hi) / 2; // two neighbours too far apart: halfway
        }
        float Clamp(float v) => Math.Clamp(v, lo, hi);
        var marks = new List<(float V, string Name)> { (0, "centred") };
        foreach (var (o, h) in neighbours)
        {
            marks.Add((o, "centred"));
            marks.Add((o + h - half, "sides lined up"));
            marks.Add((o - h + half, "sides lined up"));
        }
        var inRange = marks.Where(m => m.V >= lo - 1e-3f && m.V <= hi + 1e-3f).ToList();
        float off = Clamp(raw);
        var best = inRange.Where(m => MathF.Abs(m.V - off) < Catch).OrderBy(m => MathF.Abs(m.V - off)).Cast<(float V, string Name)?>().FirstOrDefault();
        string snap = "";
        if (best is { } b) (off, snap) = (b.V, b.Name);
        else off = Clamp(MathF.Round(off / Step) * Step);
        return new OffsetPick(off, lo, hi, snap, inRange.Select(m => m.V).Distinct().ToList());
    }

    /// <summary>A tag for an offset across a heading: <c>centre</c>, <c>L 3.0 m</c>, <c>R 1.5 m</c>.</summary>
    public static string Label(float offset) =>
        MathF.Abs(offset) < 0.01f ? "centre" : $"{(offset > 0 ? "L" : "R")} {MathF.Abs(offset):0.0} m";
}
