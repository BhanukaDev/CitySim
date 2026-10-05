using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CitySim.Roads;
using CitySim.Splines;
using CitySim.Splines.Godot;
using CitySim.Zoning;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

/// <summary>
/// <c>--demo-grid</c>: builds a straight road, a curve, a tight bend and a four-lane road, checks the zoning grid on each
/// (rows, even columns, no gaps or overlaps, rebuilt on change and undo, the same grid from the same roads) and prints
/// "Demo grid: all ok" (or the problems). Run headless with <c>--quit-after</c>.
/// <c>--storyboard=&lt;frame&gt;</c>: rebuilds a frame of <c>docs/zoning-grid.html</c> (<c>straight</c>, <c>curve</c>;
/// <c>--radius=</c> as the curve frame's slider) with the frame's origin at <see cref="Origin"/>, for a screenshot
/// to compare with the HTML: <c>--cam=560,500,260,90,0</c> frames its 320 × 200 m view.
/// </summary>
public partial class ZoningDemo : Node
{
    [Export] public RoadToolHost? Host { get; set; }
    [Export] public SplineNetwork? Network { get; set; }
    [Export] public ZoneGridView? Grid { get; set; }

    /// <summary>Where the storyboard's (0, 0) goes on the map.</summary>
    public static readonly NumVector2 Origin = new(400, 400);

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        float Arg(string name, float fallback) =>
            args.FirstOrDefault(a => a.StartsWith(name + "=")) is { } a ? float.Parse(a[(name.Length + 1)..], CultureInfo.InvariantCulture) : fallback;
        foreach (string arg in args)
            if (arg == "--demo-grid") Callable.From(RunGrid).CallDeferred();
            else if (arg.StartsWith("--storyboard="))
            {
                string frame = arg["--storyboard=".Length..];
                float radius = Arg("--radius", 130);
                Callable.From(() => Storyboard(frame, radius)).CallDeferred();
            }
    }

    private Action<Pi[]>? RoadOf(string roadId)
    {
        if (Host?.ProfileFor(roadId) is not { } profile || Network is null) return null;
        var rules = profile.ToRules();
        return pis => Network.Apply(g => g.AddSpline(new Alignment(pis), rules));
    }

    /// <summary>An arc round <paramref name="centre"/> from angle <paramref name="a0"/> to <paramref name="a1"/> (radians,
    /// atan2 of z, x), as corners of that radius between tangent points, a few pieces so no corner turns too far.</summary>
    private static Pi[] Arc(NumVector2 centre, float radius, float a0, float a1, int pieces = 3)
    {
        NumVector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));
        float step = (a1 - a0) / pieces;
        var pis = new List<Pi> { new(centre + Dir(a0) * radius) };
        for (int k = 0; k < pieces; k++)
            pis.Add(new Pi(centre + Dir(a0 + (k + 0.5f) * step) * (radius / MathF.Cos(step / 2)), radius));
        pis.Add(new Pi(centre + Dir(a1) * radius));
        return pis.ToArray();
    }

    // ------------------------------------------------------------------------------------------------- storyboard

    private void Storyboard(string frame, float radius)
    {
        var road = RoadOf("two_lane");
        if (road is null || Grid is null) { GD.PrintErr("Storyboard: no two_lane road, network or grid"); return; }
        Grid.AlwaysShow = true;
        NumVector2 P(float x, float y) => Origin + new NumVector2(x, y);
        switch (frame)
        {
            case "straight":
                // The frame's road runs past both edges of the view (x −10 .. 330 at y 100); ours runs on further so its
                // dead ends are out of shot.
                road([new Pi(P(-80, 100)), new Pi(P(400, 100))]);
                break;
            case "curve":
            {
                // The frame's arc: centre (160, 70 + R), ±am about straight up, am = min(asin(190 / R), 1.45).
                float am = MathF.Min(MathF.Asin(MathF.Min(1, 190 / radius)), 1.45f);
                road(Arc(P(160, 70 + radius), radius, -MathF.PI / 2 - am, -MathF.PI / 2 + am));
                break;
            }
            default:
                GD.PrintErr($"Storyboard: no frame \"{frame}\" yet (straight, curve)");
                return;
        }
        var g = Grid.Grid;
        GD.Print($"Storyboard {frame}: {g.Cells.Count} cells, " +
                 string.Join(", ", g.Strips.Select(s => $"{s.Run.Key} {s.Columns}×{s.Rows}")));
    }

    // ------------------------------------------------------------------------------------------------- checks

    private void RunGrid()
    {
        var problems = new List<string>();
        var road = RoadOf("two_lane");
        var wide = RoadOf("four_lane");
        if (road is null || wide is null || Network is null || Grid is null)
        {
            GD.PrintErr("Demo grid: no two_lane / four_lane road, network or grid");
            return;
        }
        int rebuilt = 0;
        Grid.Rebuilt += _ => rebuilt++;
        float w = Grid.Settings.Cell;
        static NumVector2 V(float x, float z) => new(x, z);
        IEnumerable<ZoneStrip> StripsOf(int edgeId) => Grid.Grid.Strips.Where(s => s.Run.Key.StartsWith($"e{edgeId}"));
        IEnumerable<ZoneCell> CellsOf(ZoneStrip s) => Grid.Grid.Cells.Where(c => c.Run == s.Run.Key);
        int NewestEdge() => Network.Graph.Edges.Max(e => e.Id);

        // A straight road: plain rectangles, 8 m back from the centre line (two-lane: 16 m wide).
        road([new Pi(V(100, 200)), new Pi(V(500, 200))]);
        int straight = NewestEdge();
        foreach (var s in StripsOf(straight))
        {
            if (s.Rows != 5) problems.Add($"straight {s.Run.Key}: {s.Rows} rows, want 5");
            float len = Length(s.Run.Points);
            if (s.Columns != (int)MathF.Round(len / w)) problems.Add($"straight {s.Run.Key}: {s.Columns} columns for {len:0.#} m");
            float colW = len / s.Columns;
            foreach (var c in CellsOf(s))
            {
                if (MathF.Abs(Poly.Area(c.Polygon) - colW * w) > 0.01f * colW * w)
                    problems.Add($"straight cell {c.Key}: area {Poly.Area(c.Polygon):0.#}, want {colW * w:0.#}");
                float near = c.Polygon.Min(p => MathF.Abs(p.Y - 200));
                if (MathF.Abs(near - (8 + c.Row * w)) > 0.01f) problems.Add($"straight cell {c.Key}: front {near:0.##} m from the centre line");
            }
        }
        if (StripsOf(straight).Count() != 2) problems.Add($"straight: {StripsOf(straight).Count()} strips, want 2");

        // A curve, R 130: both sides keep 5 rows; outside the back cells are wider than the front ones, inside narrower;
        // the cells fill the band exactly (no gaps or overlaps).
        CheckCurve(new NumVector2(700, 600), 130, 5, 5);
        // A tight bend, R 40: the inside frontage is 32 m from the centre, so it keeps floor((32 − 6) / 8) = 3 rows.
        CheckCurve(new NumVector2(300, 700), 40, 5, 3);

        void CheckCurve(NumVector2 centre, float radius, int wantOut, int wantIn)
        {
            road(Arc(centre, radius, -MathF.PI / 2 - 1.2f, -MathF.PI / 2 + 1.2f));
            int id = NewestEdge();
            foreach (var s in StripsOf(id))
            {
                var cells = CellsOf(s).ToList();
                float front = (s.Run.Points[0] - centre).Length();
                bool outside = front > radius;
                int want = outside ? wantOut : wantIn;
                string name = $"R{radius} {(outside ? "outside" : "inside")}";
                if (s.Rows != want) problems.Add($"{name}: {s.Rows} rows, want {want}");
                float back = outside ? front + s.Depth : front - s.Depth;
                float sweep = MathF.Abs(Angle(s.Run.Points[0] - centre, s.Run.Points[^1] - centre));
                float band = sweep * MathF.Abs(back * back - front * front) / 2;
                float sum = cells.Sum(c => Poly.Area(c.Polygon));
                if (MathF.Abs(sum - band) > 0.005f * band) problems.Add($"{name}: cells cover {sum:0} m², the band is {band:0} m²");
                if (cells.Select(c => MathF.Sign(Poly.SignedArea(c.Polygon))).Distinct().Count() != 1) problems.Add($"{name}: folded cells");
                float a0 = cells.Where(c => c.Row == 0).Average(c => Poly.Area(c.Polygon));
                float aN = cells.Where(c => c.Row == s.Rows - 1).Average(c => Poly.Area(c.Polygon));
                if (outside ? aN <= a0 : aN >= a0) problems.Add($"{name}: back cells {aN:0.#} m², front {a0:0.#} m²");
                float mid = sweep * (front + (outside ? 1 : -1) * s.Depth / 2);
                if (s.Columns != (int)MathF.Round(mid / w)) problems.Add($"{name}: {s.Columns} columns for {mid:0.#} m at mid-depth");
                GD.Print($"  {name}: {s.Columns}×{s.Rows}, front cells {a0:0.#} m², back {aN:0.#} m²");
            }
        }

        // A road type with no zone rows (highways) gets no cells.
        if (RoadFrontage.Runs(Network.Graph, Network.Footprints, _ => 0).Count > 0) problems.Add("0 zone rows: still has runs");

        // A wider road: its frontage is further out (four-lane: half width from its profile).
        var before = Snapshot(Grid.Grid);
        wide([new Pi(V(100, 900)), new Pi(V(600, 900))]);
        float half = Network.Graph.Edge(NewestEdge()).Rules.Width / 2;
        if (StripsOf(NewestEdge()).Count() != 2) problems.Add("four-lane: no strips");
        foreach (var c in StripsOf(NewestEdge()).SelectMany(CellsOf).Where(c => c.Row == 0))
            if (MathF.Abs(c.Polygon.Min(p => MathF.Abs(p.Y - 900)) - half) > 0.01f) { problems.Add($"four-lane: front not {half} m out"); break; }

        // Rebuilt on every change and on undo; the same roads give the same grid.
        int changes = rebuilt;
        Network.Undo();
        if (rebuilt != changes + 1) problems.Add("undo: grid not rebuilt");
        if (Snapshot(Grid.Grid) != before) problems.Add("undo: grid differs from before the four-lane road");
        var again = ZoneGrid.Build(RoadFrontage.Runs(Network.Graph, Network.Footprints, Grid.RowsOf), Grid.Settings);
        if (Snapshot(again) != Snapshot(Grid.Grid)) problems.Add("determinism: a second build differs");
        // Shown only while the Roads (or Zones) tray is open.
        if (Grid.Visible) problems.Add("shown with no tray open");
        Host!.Hud!.OpenById("roads");
        if (!Grid.Visible) problems.Add("hidden with the Roads tray open");
        Host.Hud.Close();
        if (Grid.Visible) problems.Add("still shown after closing the Roads tray");

        if (rebuilt < 4) problems.Add($"grid rebuilt {rebuilt} times, want at least 4");

        GD.Print($"Demo grid: {Network.Graph.EdgeCount} edges, {Grid.Grid.Strips.Count} strips, {Grid.Grid.Cells.Count} cells");
        GD.Print(problems.Count == 0 ? "Demo grid: all ok" : "Demo grid: PROBLEMS\n  " + string.Join("\n  ", problems));
    }

    private static string Snapshot(ZoneGrid g) => string.Join("|", g.Cells.Select(c =>
        c.Key + ":" + string.Join(";", c.Polygon.Select(p => $"{p.X:0.###},{p.Y:0.###}"))));

    private static float Length(IReadOnlyList<NumVector2> pts)
    {
        float l = 0;
        for (int i = 1; i < pts.Count; i++) l += NumVector2.Distance(pts[i - 1], pts[i]);
        return l;
    }

    private static float Angle(NumVector2 a, NumVector2 b) => MathF.Atan2(a.X * b.Y - a.Y * b.X, NumVector2.Dot(a, b));
}
