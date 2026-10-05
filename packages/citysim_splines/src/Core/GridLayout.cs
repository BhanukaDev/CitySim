using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>How a grid's blocks share its outline (DESIGN.md → Modes, 4).</summary>
public enum GridFit
{
    /// <summary>Equal blocks and the outline exactly on the clicks; a block may hold a part lot.</summary>
    Even,
    /// <summary>Every block whole lots (they may differ by one); the far roads move in lot steps, never whole blocks.</summary>
    LotSteps,
}

/// <summary>
/// Grid mode (DESIGN.md → Modes, 4): a rectangle from a corner, a direction along the first edge and a side, whose
/// outline is where the clicks are. Inside it the player sets the number of blocks per axis; the blocks share the
/// space by <see cref="GridFit"/>. A block's size is its clear space between the roads' kerbs (zoning fills a block
/// from both sides), in lots of <see cref="ProfileRules.SnapLength"/>. An outline side placed on a built road reuses
/// it: the block next to it is measured from that road's kerb (<c>AlongWidths</c> / <c>AcrossWidths</c> hold each
/// centre line's road width) and <see cref="Lines"/> leaves that stretch out, so the grid's roads meet it in Ts.
/// The geometry is exact (the cross direction is the along one turned 90°), so a grid never drifts off square.
/// Core-only.
/// </summary>
public sealed record GridLayout(Vector2 Corner, Vector2 Along, Vector2 Across, float[] AlongOffsets, float[] AcrossOffsets,
    float[] AlongWidths, float[] AcrossWidths, float Lot)
{
    /// <summary>Default blocks along × across.</summary>
    public const int DefaultCols = 3, DefaultRows = 2;
    /// <summary>The most blocks along either side, so a stray key can't build hundreds of roads.</summary>
    public const int MaxBlocks = 40;
    /// <summary>The smallest block, in lots of clear space: one lot of zoning from each side (from the user).</summary>
    public const int MinLots = 2;

    /// <summary>The width of a built road an outline side lies on (<see cref="SplineGraph.RunsAlong"/> over most of
    /// it), or null. Null for the whole function: no built roads are looked at.</summary>
    public delegate float? RoadAlong(Vector2 a, Vector2 b);

    public int Cols => AlongOffsets.Length - 1;
    /// <summary>0 while the width is still being picked (just the first edge).</summary>
    public int Rows => AcrossOffsets.Length - 1;
    public float Width => AlongOffsets[^1];
    public float Depth => AcrossOffsets[^1];

    /// <summary>Clear space between the outer kerbs of a span split into <paramref name="n"/> blocks.</summary>
    private static float Clear(float span, int n, float first, float last, float road) => span - (first + last) / 2 - (n - 1) * road;

    /// <summary>The most blocks a span takes before one would be under <see cref="MinLots"/> (at least one).</summary>
    public static int MaxFit(float span, float first, float last, ProfileRules rules) =>
        Math.Clamp((int)MathF.Floor((span - (first + last) / 2 + rules.Width) / (MinLots * rules.SnapLength + rules.Width) + 1e-4f), 1, MaxBlocks);

    /// <summary>
    /// Centre-line offsets splitting <paramref name="span"/> into <paramref name="n"/> blocks whose outer lines are
    /// roads <paramref name="first"/> and <paramref name="last"/> wide (the inner ones the profile's). Even: equal clear
    /// space. LotSteps: whole lots, the spare ones spread evenly, so the far line can stop short by under a lot.
    /// </summary>
    public static float[] Split(float span, int n, GridFit fit, float first, float last, ProfileRules rules)
    {
        float lot = rules.SnapLength, clear = Clear(span, n, first, last, rules.Width);
        int total = Math.Max(0, (int)MathF.Floor(clear / lot + 1e-4f));
        int lots = total / n, extra = total - lots * n;
        var offs = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            float block = fit == GridFit.Even ? clear / n : (lots + (i + 1) * extra / n - i * extra / n) * lot;
            float w0 = i == 0 ? first : rules.Width, w1 = i == n - 1 ? last : rules.Width;
            offs[i + 1] = offs[i] + w0 / 2 + block + w1 / 2;
        }
        return offs;
    }

    private static float[] Widths(int n, float first, float last, float road) =>
        Enumerable.Range(0, n + 1).Select(i => i == 0 ? first : i == n ? last : road).ToArray();

    /// <summary>
    /// The grid from a corner to <paramref name="alongEnd"/> (the second click: direction and width) and
    /// <paramref name="depthAt"/> (the third: the side and the depth; null while the width is still being picked,
    /// which gives no rows: just the first edge), with <paramref name="cols"/> × <paramref name="rows"/> blocks,
    /// fewer where a span is too short for that many. A side on a built road (<paramref name="roadAlong"/>) takes that
    /// road's width, and a far side on one stays put (Even on that axis, whatever <paramref name="fit"/>), as does one
    /// whose click snapped onto a built road or node (<paramref name="pinAlong"/>: the second click,
    /// <paramref name="pinAcross"/>: the third), so the grid joins what it was placed on. Null when the second click is
    /// on the corner.
    /// </summary>
    public static GridLayout? From(Vector2 corner, Vector2 alongEnd, Vector2? depthAt, int cols, int rows, GridFit fit,
        ProfileRules rules, RoadAlong? roadAlong = null, bool pinAlong = false, bool pinAcross = false)
    {
        var d = alongEnd - corner;
        if (d.Length() < SplineMath.Epsilon) return null;
        var along = Vector2.Normalize(d);
        float width = d.Length(), depth = 0, w = rules.Width;
        var across = SplineMath.Left(along);
        if (depthAt is { } q)
        {
            depth = Vector2.Dot(q - corner, across);
            if (depth < 0) { across = -across; depth = -depth; }
        }
        bool rows0 = depth <= SplineMath.Epsilon;
        float? On(Vector2 a, Vector2 b) => roadAlong?.Invoke(a, b);
        // The outline's sides: the first edge, the far row, the first column, the far column.
        float rowFirst = On(corner, alongEnd) ?? w;
        float? rowLast = rows0 ? null : On(corner + across * depth, alongEnd + across * depth);
        float? colFirst = rows0 ? null : On(corner, corner + across * depth);
        float? colLast = rows0 ? null : On(alongEnd, alongEnd + across * depth);

        float cf = colFirst ?? w, cl = colLast ?? w;
        int nc = Math.Min(Math.Max(1, cols), MaxFit(width, cf, cl, rules));
        var alongOffsets = Split(width, nc, colLast is null && !pinAlong ? fit : GridFit.Even, cf, cl, rules);
        float[] acrossOffsets = { 0f }, acrossWidths = { rowFirst };
        if (!rows0)
        {
            float rl = rowLast ?? w;
            int nr = Math.Min(Math.Max(1, rows), MaxFit(depth, rowFirst, rl, rules));
            acrossOffsets = Split(depth, nr, rowLast is null && !pinAcross ? fit : GridFit.Even, rowFirst, rl, rules);
            acrossWidths = Widths(nr, rowFirst, rl, w);
        }
        return new GridLayout(corner, along, across, alongOffsets, acrossOffsets, Widths(nc, cf, cl, w), acrossWidths, rules.SnapLength);
    }

    /// <summary><see cref="RoadAlong"/> over a built graph: a road along at least half the side.</summary>
    public static RoadAlong Built(SplineGraph graph, ProfileRules rules) => (a, b) =>
    {
        var runs = graph.RunsAlong(a, b, rules);
        float covered = runs.Sum(r => r.To - r.From);
        return covered >= Vector2.Distance(a, b) / 2 ? runs.Max(r => r.Width) : null;
    };

    public Vector2 Point(int col, int row) => Corner + Along * AlongOffsets[col] + Across * AcrossOffsets[row];

    /// <summary>The far end of the first edge.</summary>
    public Vector2 AlongEnd => Point(Cols, 0);

    /// <summary>A block's clear space between the roads' kerbs, in metres (along, across).</summary>
    public (float Along, float Across) BlockSize(int col, int row) =>
        (AlongOffsets[col + 1] - AlongOffsets[col] - (AlongWidths[col] + AlongWidths[col + 1]) / 2,
         AcrossOffsets[row + 1] - AcrossOffsets[row] - (AcrossWidths[row] + AcrossWidths[row + 1]) / 2);

    /// <summary>A block's clear area (kerb to kerb), its four corners in order.</summary>
    public Vector2[] BlockCorners(int col, int row)
    {
        float a0 = AlongOffsets[col] + AlongWidths[col] / 2, a1 = AlongOffsets[col + 1] - AlongWidths[col + 1] / 2;
        float c0 = AcrossOffsets[row] + AcrossWidths[row] / 2, c1 = AcrossOffsets[row + 1] - AcrossWidths[row + 1] / 2;
        Vector2 P(float a, float c) => Corner + Along * a + Across * c;
        return new[] { P(a0, c0), P(a1, c0), P(a1, c1), P(a0, c1) };
    }

    /// <summary>Whether a size is whole lots.</summary>
    public bool Whole(float metres) => MathF.Abs(metres / Lot - MathF.Round(metres / Lot)) < 0.02f;

    /// <summary>Whether any block is under <see cref="MinLots"/> on either side (refused unless Anarchy).</summary>
    public bool TooSmall => Rows > 0 && Enumerable.Range(0, Cols).Any(c => Enumerable.Range(0, Rows).Any(r =>
        BlockSize(c, r) is var (a, b) && MathF.Min(a, b) < MinLots * Lot - 0.01f));

    /// <summary>
    /// The roads: the outline as roads turning its corners on arcs of the profile's default radius (as the Draw tool
    /// makes a corner, so each has its bend slider, not a node), and every inner row and column line straight across
    /// to it, less any stretch that lies on a built road in <paramref name="built"/> (that road is reused; the grid's
    /// lines end on it and join it, and the outline breaks there). A corner on a built road or node isn't rounded: the
    /// outline breaks there too, so the corner is a junction with that road. Added to a graph one by one, their crossings
    /// become the junctions. With no rows it's just the first edge.
    /// </summary>
    public List<Alignment> Lines(SplineGraph? built = null, ProfileRules? rules = null)
    {
        var lines = new List<Alignment>();
        if (Rows == 0)
        {
            foreach (var (s0, s1) in Kept(Point(0, 0), Point(Cols, 0)))
                lines.Add(Line(Point(0, 0), Point(Cols, 0), s0, s1));
            return lines;
        }

        // The outline, corner by corner round the ring; a run of kept pieces that passes a corner turns it there.
        float radius = rules?.DefaultRadius ?? 0;
        var corners = new[] { Point(0, 0), Point(Cols, 0), Point(Cols, Rows), Point(0, Rows) };
        var runs = new List<List<Pi>>();
        List<Pi>? open = null;
        for (int k = 0; k < 4; k++)
        {
            Vector2 a = corners[k], b = corners[(k + 1) % 4];
            float length = Vector2.Distance(a, b);
            var dir = (b - a) / length;
            var kept = Kept(a, b);
            if (kept.Count == 0 || OnBuilt(a)) open = null;
            foreach (var (s0, s1) in kept)
            {
                if (s0 > 0 || open is null)
                {
                    open = new List<Pi> { new(a + dir * s0) };
                    runs.Add(open);
                }
                else open[^1] = new Pi(a, radius); // on round the corner
                open.Add(new Pi(a + dir * s1));
                if (s1 < length) open = null;
            }
        }
        var first = runs.FirstOrDefault();
        bool closes = open is not null && !OnBuilt(corners[0]) && Vector2.Distance(first![0].Position, corners[0]) < SplineGraph.NodeTolerance;
        if (runs.Count == 1 && closes)
        {
            // The whole ring unbroken: one loop, closed on the first side (where the first column meets it, a T anyway)
            // so every corner is a bend.
            var mid = Cols > 1 ? Point(1, 0) : (corners[0] + corners[1]) / 2;
            var loop = new List<Pi> { new(mid) };
            loop.AddRange(first.Skip(1).Take(first.Count - 2)); // corners 1, 2, 3
            loop.Add(new Pi(corners[0], radius));
            loop.Add(new Pi(mid));
            lines.Add(new Alignment(loop));
        }
        else
        {
            // A run ending on corner 0 goes on into one starting there.
            if (runs.Count > 1 && closes)
            {
                open![^1] = new Pi(corners[0], radius);
                open.AddRange(first!.Skip(1));
                runs.RemoveAt(0);
            }
            lines.AddRange(runs.Select(r => new Alignment(r)));
        }

        for (int r = 1; r < Rows; r++) Add(Point(0, r), Point(Cols, r));
        for (int c = 1; c < Cols; c++) Add(Point(c, 0), Point(c, Rows));
        return lines;

        // A corner on a built road or node (a grid started on one): a junction there, not a bend.
        bool OnBuilt(Vector2 p) => built is not null && (built.NodeAt(p) is not null || built.Edges.Any(e =>
            e.Alignment.Curve.Length > 0 && Vector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p) < SplineGraph.NodeTolerance));

        void Add(Vector2 a, Vector2 b)
        {
            foreach (var (s0, s1) in Kept(a, b)) lines.Add(Line(a, b, s0, s1));
        }

        Alignment Line(Vector2 a, Vector2 b, float s0, float s1)
        {
            var dir = Vector2.Normalize(b - a);
            return new Alignment(new[] { new Pi(a + dir * s0), new Pi(a + dir * s1) });
        }

        // The stretches of a to b not on a built road, as distances from a.
        List<(float, float)> Kept(Vector2 a, Vector2 b)
        {
            var kept = new List<(float, float)>();
            float length = Vector2.Distance(a, b), s = 0;
            if (built is not null && rules is not null)
                foreach (var (from, to, _) in built.RunsAlong(a, b, rules))
                {
                    Piece(s, from);
                    s = to;
                }
            Piece(s, length);
            return kept;

            void Piece(float s0, float s1)
            {
                if (s1 - s0 > SplineGraph.NodeTolerance) kept.Add((s0, s1));
            }
        }
    }

    /// <summary>The tag: <c>3 × 2 blocks · 236 × 152 m</c>.</summary>
    public string Label() => Rows == 0
        ? $"{Cols} blocks · {Width:0} m"
        : $"{Cols} × {Rows} blocks · {Width:0} × {Depth:0} m";

    /// <summary>A block's label: <c>64 × 48 m · 8 × 6 lots</c>, or <c>8 × 6 lots</c> alone when
    /// <paramref name="brief"/> (lots to one decimal when part of one).</summary>
    public string BlockLabel(int col, int row, string unit, bool brief = false)
    {
        var (a, b) = BlockSize(col, row);
        return brief ? $"{a / Lot:0.#} × {b / Lot:0.#} {unit}s" : $"{a:0.#} × {b:0.#} m · {a / Lot:0.#} × {b / Lot:0.#} {unit}s";
    }
}
