using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-junctions</c> (S4): checks the graph, junctions and validation in Core on synthetic profiles: a T and a
/// 4-way (splits, labels, footprint cut-backs and curbs), a too-sharp angle (Warn), a square rail branch (Invalid) and
/// its legal turnout ghost, a crossing through an existing junction, ConnectsTo refusing a canal, an overlap, a
/// clamped radius, and split → merge round trips on a straight and on an arc. Prints each case, then
/// <c>Demo junctions: all ok</c> or <c>FAILED</c>. Pure Core, no scene needed.
/// </summary>
public partial class JunctionDemo : Node
{
    private readonly List<string> _failures = new();

    private static readonly ProfileRules Street = new()
    {
        Id = "street", Width = 12, DefaultRadius = 16, MinRadius = 10, JunctionKind = JunctionKind.Node,
        MinJunctionAngle = 30, ConnectsTo = new[] { "avenue" },
    };
    private static readonly ProfileRules Avenue = Street with { Id = "avenue", Width = 24, DefaultRadius = 60, MinRadius = 40, MinJunctionAngle = 45, ConnectsTo = new[] { "street" } };
    private static readonly ProfileRules Rail = new()
    {
        Id = "rail", Width = 5, DefaultRadius = 500, MinRadius = 300, JunctionKind = JunctionKind.Turnout, TurnoutMaxAngle = 6.3f,
    };
    private static readonly ProfileRules Canal = new() { Id = "canal", Width = 14, DefaultRadius = 40, MinRadius = 25, MinJunctionAngle = 45 };

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-junctions")) Run();
    }

    private void Run()
    {
        TJunction();
        FourWay();
        TooSharp();
        RailBranch();
        ThroughJunction();
        NotConnected();
        OverlapAndClamp();
        SplitMerge();
        foreach (var f in _failures) GD.PrintErr($"Demo junctions: FAILED {f}");
        GD.Print($"Demo junctions: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private static Vector2 V(float x, float z) => new(x, z);
    private static Alignment Line(Vector2 a, Vector2 b) => new(new[] { new Pi(a), new Pi(b) });

    private void TJunction()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var r = g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        Check("T: edges", g.EdgeCount, 3);
        Check("T: nodes", g.NodeCount, 4);
        int node = r.Nodes[0];
        Check("T: node on the main road", g.Node(node).Position, V(100, 0));
        Check("T: arms", g.Arms(node).Count, 3);
        Check("T: label", Junctions.Label(g, node) ?? "", "T-junction · 90°");
        Check("T: no issues", Validation.Check(g).Count, 0);
        var f = Junctions.Footprint(g, node);
        Check("T: footprint", f is not null);
        // Each arm is cut back by the other road's half width plus the 16 m curb.
        foreach (var c in f?.Cuts ?? Array.Empty<ArmCut>()) Check($"T: cut-back edge {c.EdgeId}", c.CutBack, 22f);
        Check("T: two curbs", f?.Curbs.Count ?? 0, 2);
        Check("T: curb centre", f?.Curbs.Any(c => Vector2.Distance(c.Centre, V(78, 22)) < 0.01f) == true);
    }

    private void FourWay()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(300, 0)), Avenue);
        var r = g.AddSpline(Line(V(150, -100), V(150, 100)), Street);
        Check("X: edges", g.EdgeCount, 4);
        Check("X: new spline split in two", r.Edges.Count, 2);
        int centre = r.Nodes.Single(n => g.Arms(n).Count == 4);
        Check("X: label", Junctions.Label(g, centre) ?? "", "4-way · 90°");
        Check("X: no issues", Validation.Check(g).Count, 0);
        // Street arms cut back past the avenue's half width (12) + street curb 16; avenue arms by 6 + 16.
        var f = Junctions.Footprint(g, centre)!;
        Check("X: street cut-back", f.Cuts.Where(c => g.Edge(c.EdgeId).Rules.Id == "street").Max(c => c.CutBack), 28f);
        Check("X: avenue cut-back", f.Cuts.Where(c => g.Edge(c.EdgeId).Rules.Id == "avenue").Max(c => c.CutBack), 22f);
        Check("X: four curbs", f.Curbs.Count, 4);
    }

    private void TooSharp()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(300, 0)), Street);
        var dir = SplineMath.Direction(-22f * MathF.PI / 180f);
        g.AddSpline(Line(V(120, 0), V(120, 0) + dir * 170), Street);
        var issues = Validation.Check(g);
        Check("sharp: one issue", issues.Count, 1);
        Check("sharp: amber", issues.FirstOrDefault()?.Severity == Severity.Warn);
        Check("sharp: message", issues.FirstOrDefault()?.Message ?? "", "22°, min 30° · Ctrl+A allows");
    }

    private void RailBranch()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(600, 0)), Rail);
        var clone = g.Clone();
        var r = clone.AddSpline(Line(V(140, 0), V(140, -110)), Rail);
        var issues = Validation.Check(clone);
        Check("rail square: refused (Invalid)", issues.Any(i => i is { Severity: Severity.Invalid, Code: "turnout" }));
        Check("rail square: message", issues.FirstOrDefault(i => i.Code == "turnout")?.Message ?? "", "90° not allowed");
        Check("rail square: clone left the graph alone", g.EdgeCount, 1);

        var ghost = Junctions.TurnoutGhost(V(140, 0), V(1, 0), V(460, -110), Rail.MinRadius);
        Check("turnout ghost", ghost is not null);
        if (ghost is null) return;
        Check("ghost leaves along the line", Vector2.Dot(ghost.Curve.Sample(0).Tangent, V(1, 0)) > 0.9999f);
        Check("ghost radius", ghost.EffectiveRadius(1), 300f);
        var t = g.Clone();
        var tr = t.AddSpline(ghost, Rail);
        Check("turnout: legal", Validation.Check(t).Count(i => i.Severity == Severity.Invalid), 0);
        Check("turnout: label", Junctions.Label(t, tr.Nodes[0]) ?? "", "turnout · R 300 m");
        Check("turnout: ratio", Junctions.TurnoutRatio(6.3f), "1:9");
    }

    private void ThroughJunction()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        g.AddSpline(Line(V(50, -50), V(150, 50)), Street); // a diagonal through the T's node
        int node = g.Nodes.Single(n => n.Position == V(100, 0)).Id;
        Check("through: joins the T's node", g.Arms(node).Count, 5);
        Check("through: no new node there", g.NodeCount, 6);
        Check("through: label", Junctions.Label(g, node) ?? "", "5-way · 45°");

        var d = new SplineGraph();
        d.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        d.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        d.AddSpline(Line(V(100, -100), V(100, 50)), Street); // through the node and back over the branch
        Check("over a road: red", Validation.Check(d).Any(i => i is { Code: "overlap", Severity: Severity.Invalid }));
    }

    private void NotConnected()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        g.AddSpline(Line(V(100, -80), V(100, 80)), Canal);
        Check("canal: no split", g.EdgeCount, 2);
        var canal = g.Edges.Single(e => e.Rules.Id == "canal").Id;
        var issue = Validation.Check(g).FirstOrDefault(i => i.Code == "crossing" && i.EdgeId == canal);
        Check("canal: red crossing", issue?.Severity == Severity.Invalid);
        Check("canal: message", issue?.Message ?? "", "crosses street · not connected");
    }

    private void OverlapAndClamp()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        g.AddSpline(Line(V(0, 5), V(200, 5)), Street);
        Check("overlap: red", Validation.Check(g).Any(i => i is { Code: "overlap", Severity: Severity.Invalid }));

        var ok = new SplineGraph();
        ok.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        ok.AddSpline(Line(V(0, 12), V(200, 12)), Street); // touching at a 0 m gap
        Check("0 m gap: fine", Validation.Check(ok).Count, 0);

        var c = new SplineGraph();
        c.AddSpline(new Alignment(new[] { new Pi(V(20, 178)), new Pi(V(160, 48), 500), new Pi(V(300, 178)) }), Rail);
        var issue = Validation.Check(c).FirstOrDefault();
        Check("clamped: amber", issue?.Code == "radius-clamped" && issue.Severity == Severity.Warn);
    }

    private void SplitMerge()
    {
        // Straight: a T, then delete the branch — the main road merges back into one edge.
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var r = g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        g.RemoveEdge(r.Edges[0]);
        Check("merge: one edge", g.EdgeCount, 1);
        Check("merge: two nodes", g.NodeCount, 2);
        var e = g.Edges.Single();
        Check("merge: length", e.Alignment.Length, 200f);
        Check("merge: runs 0 → 200", e.Alignment.Curve.Sample(0).Position, V(0, 0));

        // An arc: cross the corner's arc, then delete the crossing road — the corner folds back into one PI.
        var corner = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 40), new Pi(V(100, 100)) });
        var h = new SplineGraph();
        h.AddSpline(corner, Street);
        var mid = corner.Curve.Sample(60 + 10 * MathF.PI).Position; // the middle of the arc
        var across = h.AddSpline(Line(mid - V(-1, 1) * 50, mid + V(-1, 1) * 50), Street);
        Check("arc split: corner in two", h.EdgeCount, 4);
        float total = h.Edges.Where(x => !across.Edges.Contains(x.Id)).Sum(x => x.Alignment.Length);
        Check("arc split: same length", total, corner.Length);
        foreach (int id in across.Edges) h.RemoveEdge(id);
        var back = h.Edges.Single().Alignment;
        Check("arc merge: three PIs again", back.Pis.Count, 3);
        Check("arc merge: corner PI", back.Pis[1].Position, V(100, 0));
        Check("arc merge: radius", back.EffectiveRadius(1), 40f);

        // Not straight through: a corner node stays when the branch goes.
        var k = new SplineGraph();
        k.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        k.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        var third = k.AddSpline(Line(V(100, 0), V(200, 0)), Street);
        k.RemoveEdge(third.Edges[0]);
        Check("corner node stays", k.EdgeCount, 2);
    }

    private void Check(string name, bool ok)
    {
        GD.Print($"  {name}: {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, int got, int want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: {got} (want {want}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, float got, float want)
    {
        bool ok = MathF.Abs(got - want) < 0.01f;
        GD.Print($"  {name}: {got:0.###} (want {want:0.###}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, Vector2 got, Vector2 want)
    {
        bool ok = Vector2.Distance(got, want) < 0.01f;
        GD.Print($"  {name}: {got} (want {want}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, string got, string want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: \"{got}\" (want \"{want}\") {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }
}
