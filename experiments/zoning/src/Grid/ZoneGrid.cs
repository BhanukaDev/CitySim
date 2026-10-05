using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Zoning;

/// <summary>
/// One road side's frontage line (the back of the pavement), the line its strip of cells grows from. Plan space (map
/// metres, <c>Vector2(x, z)</c>). <see cref="Side"/>: +1 = the cells lie to the left of the points' direction (left as
/// in <c>SplineMath.Left</c>: heading east, left is north, −z), −1 = to the right. <see cref="Rows"/>: the most rows
/// this side may have (the road type's).
/// </summary>
public sealed record FrontageRun(string Key, IReadOnlyList<Vector2> Points, int Side, int Rows);

/// <summary>The grid's settings: one cell size for the whole game, and how close the back row may come to a bend's
/// centre before the inside of the bend loses rows.</summary>
public sealed record GridSettings
{
    public float Cell { get; init; } = 8f;
    public float FoldMargin { get; init; } = 6f;
}

/// <summary>A zoning cell. Its key is <c>run:column:row</c>; keys change when a road changes, so painted cells move
/// across by <see cref="Centre"/>, not by key. Row 0 is the front row (on the road).</summary>
public sealed record ZoneCell(string Key, string Run, int Column, int Row, IReadOnlyList<Vector2> Polygon, Vector2 Centre);

/// <summary>A run's strip of cells: how many rows it got (fewer than the run's on a tight bend) and its columns.</summary>
public sealed record ZoneStrip(FrontageRun Run, int Rows, int Columns, float Depth);

/// <summary>
/// The zoning grid (PLAN.md §3, storyboard <c>docs/zoning-grid.html</c>, <c>gridChains</c>): each frontage run gets a
/// strip of cells, columns cut square to the road and rows as offset lines parallel to it (mitred vertex normals).
/// Columns are spread evenly at the cell size, measured half-way back, so curves fan out on the outside and narrow on
/// the inside and neither side is left with a sliver. A pure function of the runs and the settings (no Godot types).
/// Corners, junctions and blocks come in Z2.
/// </summary>
public sealed class ZoneGrid
{
    public IReadOnlyList<ZoneCell> Cells { get; }
    public IReadOnlyList<ZoneStrip> Strips { get; }
    public GridSettings Settings { get; }

    private ZoneGrid(List<ZoneCell> cells, List<ZoneStrip> strips, GridSettings settings)
    {
        Cells = cells;
        Strips = strips;
        Settings = settings;
    }

    public static ZoneGrid Build(IEnumerable<FrontageRun> runs, GridSettings? settings = null)
    {
        settings ??= new GridSettings();
        var cells = new List<ZoneCell>();
        var strips = new List<ZoneStrip>();
        foreach (var run in runs)
            if (Strip.From(run, settings) is { } s)
            {
                strips.Add(new ZoneStrip(run, s.Rows, s.Columns, s.Depth));
                s.AddCells(cells, settings.Cell);
            }
        return new ZoneGrid(cells, strips, settings);
    }

    /// <summary>The cell whose polygon holds <paramref name="p"/>, if any.</summary>
    public ZoneCell? CellAt(Vector2 p) => Cells.FirstOrDefault(c => Poly.Contains(c.Polygon, p));

    /// <summary>A run made ready for cutting: its normals, the mid-depth line and the column stations along it.</summary>
    private sealed class Strip
    {
        private FrontageRun _run = null!;
        private Vector2[] _pts = null!;
        private Vector2[] _vn = null!;   // mitred vertex normals, towards the cells
        private float[] _m = null!;      // stations along the mid-depth line
        public int Rows { get; private set; }
        public int Columns { get; private set; }
        public float Depth { get; private set; }

        public static Strip? From(FrontageRun run, GridSettings settings)
        {
            float w = settings.Cell;
            var pts = Dedupe(run.Points);
            if (pts.Length < 2 || run.Rows <= 0) return null;
            int n = pts.Length;
            var t = new Vector2[n - 1];
            var nrm = new Vector2[n - 1];
            for (int j = 0; j < n - 1; j++)
            {
                t[j] = Vector2.Normalize(pts[j + 1] - pts[j]);
                nrm[j] = Left(t[j]) * run.Side;
            }

            // On a bend the cells' side is inside, its rows stop short of the bend's centre instead of folding over.
            int rows = run.Rows;
            for (int i = 1; i < n - 1; i++)
            {
                float turn = MathF.Acos(Math.Clamp(Vector2.Dot(t[i - 1], t[i]), -1f, 1f));
                if (turn < 1e-4f || Vector2.Dot(t[i], nrm[i - 1]) <= 0) continue;
                float radius = 0.5f * (Vector2.Distance(pts[i - 1], pts[i]) + Vector2.Distance(pts[i], pts[i + 1])) / turn;
                rows = Math.Min(rows, Math.Max(0, (int)MathF.Floor((radius - settings.FoldMargin) / w)));
            }
            if (rows <= 0) return null;

            // The ends are square to the road at the end point: a segment's normal turned on by half the next turn
            // (on a curve, the segment's own normal is half a segment's turn off).
            var vn = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                if (i == 0) vn[i] = n > 2 ? Rotate(nrm[0], -Turn(t[0], t[1]) / 2) : nrm[0];
                else if (i == n - 1) vn[i] = n > 2 ? Rotate(nrm[n - 2], Turn(t[n - 3], t[n - 2]) / 2) : nrm[n - 2];
                else
                {
                    var m = Vector2.Normalize(nrm[i - 1] + nrm[i]);
                    vn[i] = m / MathF.Max(Vector2.Dot(m, nrm[i]), 0.35f);
                }
            }

            float depth = rows * w;
            var ms = new float[n];
            for (int i = 1; i < n; i++)
                ms[i] = ms[i - 1] + Vector2.Distance(pts[i] + vn[i] * depth / 2, pts[i - 1] + vn[i - 1] * depth / 2);
            if (ms[^1] < w * 0.4f) return null;

            return new Strip
            {
                _run = run, _pts = pts, _vn = vn, _m = ms,
                Rows = rows, Depth = depth,
                Columns = Math.Max(1, (int)MathF.Round(ms[^1] / w)),
            };
        }

        public void AddCells(List<ZoneCell> cells, float w)
        {
            float length = _m[^1];
            for (int c = 0; c < Columns; c++)
            {
                float ma = length * c / Columns, mb = length * (c + 1) / Columns;
                for (int r = 0; r < Rows; r++)
                {
                    var poly = CellPoly(ma, mb, r * w, (r + 1) * w);
                    cells.Add(new ZoneCell($"{_run.Key}:{c}:{r}", _run.Key, c, r, poly, Poly.Centroid(poly)));
                }
            }
        }

        /// <summary>The quad (with the run's bends between) from station <paramref name="ma"/> to <paramref name="mb"/>
        /// on the mid line, <paramref name="y0"/> to <paramref name="y1"/> metres back from the frontage.</summary>
        private Vector2[] CellPoly(float ma, float mb, float y0, float y1)
        {
            var (ia, fa) = Locate(_m, ma);
            var (ib, fb) = Locate(_m, mb);
            Vector2 At(int i, float f, float y) => Vector2.Lerp(_pts[i] + _vn[i] * y, _pts[i + 1] + _vn[i + 1] * y, f);
            var inner = new List<int>();
            for (int i = ia + 1; i <= ib; i++)
                if (_m[i] > ma + 1e-4f && _m[i] < mb - 1e-4f) inner.Add(i);
            var poly = new List<Vector2> { At(ia, fa, y0) };
            poly.AddRange(inner.Select(i => _pts[i] + _vn[i] * y0));
            poly.Add(At(ib, fb, y0));
            poly.Add(At(ib, fb, y1));
            poly.AddRange(Enumerable.Reverse(inner).Select(i => _pts[i] + _vn[i] * y1));
            poly.Add(At(ia, fa, y1));
            return poly.ToArray();
        }

        private static (int Index, float Fraction) Locate(float[] arr, float v)
        {
            int n = arr.Length;
            if (v <= arr[0]) return (0, 0);
            if (v >= arr[n - 1]) return (n - 2, 1);
            int i = 0;
            while (i < n - 2 && arr[i + 1] < v) i++;
            float s = arr[i + 1] - arr[i];
            return (i, s > 1e-6f ? (v - arr[i]) / s : 0);
        }

        private static Vector2[] Dedupe(IReadOnlyList<Vector2> pts)
        {
            var list = new List<Vector2>();
            foreach (var p in pts)
                if (list.Count == 0 || Vector2.DistanceSquared(list[^1], p) > 1e-4f) list.Add(p);
            return list.ToArray();
        }

        private static Vector2 Left(Vector2 t) => new(t.Y, -t.X);

        /// <summary>Signed angle from <paramref name="a"/> to <paramref name="b"/>.</summary>
        private static float Turn(Vector2 a, Vector2 b) => MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b));

        private static Vector2 Rotate(Vector2 v, float angle)
        {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }
    }
}

/// <summary>Polygon helpers for the grid (plan space).</summary>
public static class Poly
{
    public static float SignedArea(IReadOnlyList<Vector2> p)
    {
        float s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static float Area(IReadOnlyList<Vector2> p) => MathF.Abs(SignedArea(p));

    public static Vector2 Centroid(IReadOnlyList<Vector2> p)
    {
        float s = 0, x = 0, y = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            float c = a.X * b.Y - b.X * a.Y;
            s += c;
            x += (a.X + b.X) * c;
            y += (a.Y + b.Y) * c;
        }
        if (MathF.Abs(s) < 1e-6f)
            return p.Aggregate(Vector2.Zero, (acc, v) => acc + v) / p.Count;
        return new Vector2(x / (3 * s), y / (3 * s));
    }

    public static bool Contains(IReadOnlyList<Vector2> poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
