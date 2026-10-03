using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Grid mode (DESIGN.md → Modes, 4): a rectangle of blocks from a corner, a direction along the first edge and a
/// side. Block sizes are whole lots (<see cref="ProfileRules.SnapLength"/>) of clear space between the roads' edges,
/// so zoning fills a block with no part cells; the centre lines are a lot count plus one road width apart. The
/// geometry is exact (the cross direction is the along one turned 90°), so a grid never drifts off square.
/// Core-only.
/// </summary>
public sealed record GridLayout(Vector2 Corner, Vector2 Along, Vector2 Across, int Cols, int Rows, float PitchAlong, float PitchAcross)
{
    /// <summary>Default block: 8 × 8 lots, which is zoning 4 deep from each side.</summary>
    public const int DefaultLots = 8;
    public const int MinLots = 1, MaxLots = 64;
    /// <summary>The most blocks along either side, so a stray click can't build hundreds of roads.</summary>
    public const int MaxBlocks = 40;

    /// <summary>Centre-line spacing for a block of <paramref name="lots"/>: the lots plus one road width.</summary>
    public static float Pitch(int lots, ProfileRules rules) => lots * rules.SnapLength + rules.Width;

    public float Width => Cols * PitchAlong;
    public float Depth => Rows * PitchAcross;

    /// <summary>
    /// The grid from a corner to <paramref name="alongEnd"/> (the second click: direction, and the width rounded to
    /// whole blocks, at least one) and <paramref name="depthAt"/> (the third: the side, and the depth rounded the same
    /// way; null while the width is still being picked, which gives no rows: just the first edge). Null when the
    /// second click is on the corner.
    /// </summary>
    public static GridLayout? From(Vector2 corner, Vector2 alongEnd, Vector2? depthAt, int lotsAlong, int lotsAcross, ProfileRules rules)
    {
        var d = alongEnd - corner;
        if (d.Length() < SplineMath.Epsilon) return null;
        var along = Vector2.Normalize(d);
        float pa = Pitch(lotsAlong, rules), pc = Pitch(lotsAcross, rules);
        int cols = Math.Clamp((int)MathF.Round(d.Length() / pa), 1, MaxBlocks);
        var across = SplineMath.Left(along);
        int rows = 0;
        if (depthAt is { } q)
        {
            float depth = Vector2.Dot(q - corner, across);
            if (depth < 0) { across = -across; depth = -depth; }
            rows = Math.Clamp((int)MathF.Round(depth / pc), 1, MaxBlocks);
        }
        return new GridLayout(corner, along, across, cols, rows, pa, pc);
    }

    public Vector2 Point(int col, int row) => Corner + Along * (col * PitchAlong) + Across * (row * PitchAcross);

    /// <summary>The far end of the first edge (the second click, rounded).</summary>
    public Vector2 AlongEnd => Point(Cols, 0);

    /// <summary>The roads: every row line full width and every column line full depth, straight. Added to a graph
    /// one by one, their crossings become the junctions. With no rows it's just the first edge.</summary>
    public List<Alignment> Lines()
    {
        var lines = new List<Alignment>();
        for (int r = 0; r <= Rows; r++) lines.Add(Line(Point(0, r), Point(Cols, r)));
        if (Rows > 0)
            for (int c = 0; c <= Cols; c++) lines.Add(Line(Point(c, 0), Point(c, Rows)));
        return lines;

        static Alignment Line(Vector2 a, Vector2 b) => new(new[] { new Pi(a), new Pi(b) });
    }

    /// <summary>The tag: <c>3 × 2 blocks · 8 × 8 lots · 240 × 160 m</c>.</summary>
    public string Label(int lotsAlong, int lotsAcross, string unit) =>
        $"{Cols} × {Rows} blocks · {lotsAlong} × {lotsAcross} {unit}s · {Width:0} × {Depth:0} m";
}
