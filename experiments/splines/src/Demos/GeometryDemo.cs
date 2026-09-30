using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-geometry</c> (S1): checks the Core alignment geometry on known cases (a 90° corner at R 20, clamped
/// corners, a hard corner, offsets of an arc, a line/arc intersection, closest point). Prints each case, then
/// <c>Demo geometry: all ok</c> or <c>FAILED</c>. Pure Core, no scene needed.
/// </summary>
public partial class GeometryDemo : Node
{
    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-geometry")) Run();
    }

    private void Run()
    {
        Corner90();
        Clamped();
        HardCorner();
        Offsets();
        IntersectionAndClosest();
        foreach (var f in _failures) GD.PrintErr($"Demo geometry: FAILED {f}");
        GD.Print($"Demo geometry: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private static Vector2 V(float x, float z) => new(x, z);

    /// <summary>(0,0) → (100,0) → (100,100) with R 20: a right turn around (80, 20).</summary>
    private static Alignment Corner() => new(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 20), new Pi(V(100, 100)) });

    private void Corner90()
    {
        var a = Corner();
        var c = a.Curve;
        float arcMid = 80 + 5 * MathF.PI;
        var mid = c.Sample(arcMid);
        bool continuous = true;
        for (int i = 0; i + 1 < c.Segments.Count; i++)
        {
            var end = c.Segments[i].Sample(c.Segments[i].Length);
            var next = c.Segments[i + 1].Sample(0);
            continuous &= Vector2.Distance(end.Position, next.Position) < 1e-3f && Vector2.Dot(end.Tangent, next.Tangent) > 0.9999f;
        }
        Check("corner length", c.Length, 160 + 10 * MathF.PI);
        Check("corner segments", c.Segments.Count, 3);
        Check("corner arc mid radius", Vector2.Distance(mid.Position, V(80, 20)), 20);
        Check("corner curvature (right turn)", mid.Curvature, -1f / 20);
        Check("corner tangent-continuous", continuous);
        Check("corner min radius", c.MinRadius(), 20);
        Check("corner min radius on first leg", float.IsPositiveInfinity(c.MinRadius(0, 70)));
        Check("corner effective radius", a.EffectiveRadius(1), 20);
        Check("corner not clamped", !a.IsClamped(1));
        Check("corner end tangent south", c.Sample(c.Length).Tangent, V(0, 1));
        var samples = c.SampleEvery(10);
        Check("sample-every ends", samples[0].S == 0 && MathF.Abs(samples[^1].S - c.Length) < 1e-3f);
    }

    private void Clamped()
    {
        // Middle leg of 20 m: each corner may use half of it, so T ≤ 10 and R = 10 at 90°.
        var a = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 40), new Pi(V(100, 20), 40), new Pi(V(200, 20)) });
        Check("clamped radius (shared leg)", a.EffectiveRadius(1), 10);
        Check("clamped flag", a.IsClamped(1) && a.IsClamped(2));
        Check("max radius (shared leg)", a.MaxRadius(1), 10);
        Check("clamped arcs meet", a.Curve.Segments.Count, 4); // line, arc, arc, line: no gap line between the arcs

        // An end leg can be used whole: 30 m legs cap R at 30.
        var b = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(30, 0), 100), new Pi(V(30, 30)) });
        Check("clamped radius (end legs)", b.EffectiveRadius(1), 30);
        Check("clamped end legs: pure arc", b.Curve.Segments.Count, 1);
        Check("max radius (end legs)", b.MaxRadius(1), 30);
    }

    private void HardCorner()
    {
        var a = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 20, Hard: true), new Pi(V(100, 100)) });
        var c = a.Curve;
        Check("hard length", c.Length, 200);
        Check("hard no arc", c.Segments.All(s => s is LineSegment) && a.EffectiveRadius(1) == 0);
        Check("hard tangent jumps", c.Sample(99.9f).Tangent, V(1, 0));
        Check("hard tangent after", c.Sample(100.1f).Tangent, V(0, 1));
        Check("hard min radius", float.IsPositiveInfinity(c.MinRadius()));

        var straight = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(50, 0), 20), new Pi(V(100, 0)) });
        Check("straight-through PI", straight.Curve.Length, 100);
    }

    private void Offsets()
    {
        var c = Corner().Curve;
        // The corner turns right, so its centre is on the right: −5 (right) is inside.
        var inside = c.Offset(-5);
        var outside = c.Offset(5);
        Check("offset inside radius", inside.MinRadius(), 15);
        Check("offset inside length", inside.Length, 160 + 7.5f * MathF.PI);
        Check("offset outside radius", outside.MinRadius(), 25);
        Check("offset outside length", outside.Length, 160 + 12.5f * MathF.PI);
        Check("offset start (right of east = south)", inside.Sample(0).Position, V(0, 5));
        Check("offset start (left of east = north)", outside.Sample(0).Position, V(0, -5));

        var line = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0)) }).Curve.Offset(3);
        Check("offset straight", line.Segments.Count == 1 && line.Segments[0] is LineSegment);
        Check("offset straight position", line.Sample(50).Position, V(50, -3));

        // Offsetting past the centre collapses the arc; the straights then meet at a sharp corner.
        var collapsed = c.Offset(-25);
        Check("offset collapsed arc dropped", collapsed.Segments.All(s => s is LineSegment) && collapsed.Segments.Count == 2);
        Check("offset collapsed corner", collapsed.Segments[0].End, V(75, 25));

        // A hard corner's offset: the straights are trimmed / extended to meet.
        var hard = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 0), new Pi(V(100, 100)) }).Curve;
        Check("offset hard corner outside", hard.Offset(5).Segments[0].End, V(105, -5));
        Check("offset hard corner inside", hard.Offset(-5).Segments[0].End, V(95, 5));
    }

    private void IntersectionAndClosest()
    {
        var c = Corner().Curve;
        var line = new Alignment(new[] { new Pi(V(90, -50)), new Pi(V(90, 150)) }).Curve;
        var hits = c.Intersect(line);
        Check("line/arc hit count", hits.Count, 1);
        if (hits.Count == 1)
        {
            Check("line/arc hit point", hits[0].Point, V(90, 20 - MathF.Sqrt(300)));
            Check("line/arc hit station on line", hits[0].SB, 50 + 20 - MathF.Sqrt(300));
        }

        var cross = new Alignment(new[] { new Pi(V(50, -10)), new Pi(V(50, 10)) }).Curve;
        var h2 = c.Intersect(cross);
        Check("line/line hit", h2.Count == 1 && Vector2.Distance(h2[0].Point, V(50, 0)) < 1e-3f && MathF.Abs(h2[0].SA - 50) < 1e-3f);

        // A left-turning corner around (100, 0) whose arc crosses ours at both arc ends: arc/arc, one hit per point.
        var other = new Alignment(new[] { new Pi(V(80, -30)), new Pi(V(80, 20), 20), new Pi(V(130, 20)) }).Curve;
        var h3 = c.Intersect(other);
        Check("arc/arc hit count", h3.Count, 2);
        if (h3.Count == 2)
        {
            Check("arc/arc hit 1", h3[0].Point, V(80, 0));
            Check("arc/arc hit 2", h3[1].Point, V(100, 20));
        }

        var p = c.ClosestPoint(V(120, 50));
        Check("closest station", p.S, 80 + 10 * MathF.PI + 30);
        Check("closest offset (left of south = east, +)", p.Offset, 20);
        Check("closest position", p.Position, V(100, 50));
        var q = c.ClosestPoint(V(80 + 10, 20 - 10));
        Check("closest on arc", Vector2.Distance(q.Position, V(80, 20)), 20);
        Check("closest on arc offset (inside = right, −)", q.Offset, -(20 - MathF.Sqrt(200)));
    }

    private void Check(string name, float got, float want, float tol = 1e-3f)
    {
        bool ok = MathF.Abs(got - want) <= tol;
        GD.Print($"  {name}: {got:0.####} (want {want:0.####}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, Vector2 got, Vector2 want, float tol = 1e-3f)
    {
        bool ok = Vector2.Distance(got, want) <= tol;
        GD.Print($"  {name}: ({got.X:0.###}, {got.Y:0.###}) (want ({want.X:0.###}, {want.Y:0.###})) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, bool ok)
    {
        GD.Print($"  {name}: {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }
}
