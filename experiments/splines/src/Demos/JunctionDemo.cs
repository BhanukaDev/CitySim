using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-junctions</c> (S4): checks the graph, junctions and validation in Core on synthetic profiles: a T and a
/// 4-way (splits, labels, footprint cut-backs and curbs), a crossing on a curve, a too-sharp angle (Warn), a square rail branch (Invalid) and
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
        OnCurve();
        TooSharp();
        RailBranch();
        ThroughJunction();
        NotConnected();
        OverlapAndClamp();
        SplitMerge();
        Continue();
        Squeezed();
        Transition();
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

    /// <summary>A street crossing an avenue on its curve: the curbs touch each arm's real (curved) side, and the
    /// footprint's arm ends sit exactly where the ribbons are cut back.</summary>
    private void OnCurve()
    {
        var g = new SplineGraph();
        g.AddSpline(new Alignment(new[] { new Pi(V(0, 200)), new Pi(V(150, 0), 120), new Pi(V(300, 200)) }), Avenue);
        var r = g.AddSpline(Line(V(125, -100), V(125, 300)), Street);
        int centre = r.Nodes.Single(n => g.Arms(n).Count == 4);
        var f = Junctions.Footprint(g, centre)!;
        Check("Curve: four curbs", f.Curbs.Count, 4);
        foreach (var c in f.Curbs)
            foreach (var p in new[] { c.From, c.To })
            {
                // Each touch point is half a width off some arm's centre line (on its side, not its tangent line).
                bool onSide = f.Cuts.Any(k =>
                {
                    var e = g.Edge(k.EdgeId);
                    return MathF.Abs(MathF.Abs(e.Alignment.Curve.ClosestPoint(p).Offset) - e.Rules.Width / 2) < 0.05f;
                });
                Check($"Curve: curb touches a side at {p}", onSide);
            }
        foreach (var k in f.Cuts)
        {
            var e = g.Edge(k.EdgeId);
            var at = e.Alignment.Curve.Sample(k.AtStart ? k.CutBack : e.Alignment.Length - k.CutBack).Position;
            var l = at + SplineMath.Left(e.Alignment.Curve.Sample(k.AtStart ? k.CutBack : e.Alignment.Length - k.CutBack).Tangent) * (e.Rules.Width / 2);
            Check($"Curve: outline has edge {k.EdgeId}'s cut corner", f.Outline.Any(o => Vector2.Distance(o, l) < 0.05f));
        }
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

        // Clamped below the minimum is red, the same as asking for it; clamped above it is no issue.
        var c = new SplineGraph();
        c.AddSpline(new Alignment(new[] { new Pi(V(20, 178)), new Pi(V(160, 48), 500), new Pi(V(300, 178)) }), Rail);
        var issue = Validation.Check(c).FirstOrDefault();
        Check("clamped below min: red", issue?.Code == "radius-min" && issue.Severity == Severity.Invalid);
        var roomy = new SplineGraph();
        roomy.AddSpline(new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 60), new Pi(V(100, 30)) }), Street);
        Check("clamped above min: fine", roomy.Edges.Single().Alignment.IsClamped(1) && Validation.Check(roomy).Count == 0);
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

        // Not straight through: a corner node stays when the branch goes, and its bend is filled.
        var k = new SplineGraph();
        k.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var stem = k.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        k.RemoveEdge(k.Edges.First(e => Vector2.Distance(e.Alignment.Pis[^1].Position, V(200, 0)) < 0.01f).Id);
        Check("corner node stays", k.EdgeCount, 2);
        Check("corner node: bend fill", Junctions.BendFill(k, stem.Nodes[0]) is not null);
    }

    /// <summary>Drawing on from a dead end of the same profile makes one road, the joint a corner with every drawn
    /// corner's rules; a different profile runs on round the same corner, split off where it starts.</summary>
    private void Continue()
    {
        // An L from a dead end: one edge, the joint rounded at the drawn radius.
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        var r = g.AddSpline(new Alignment(new[] { new Pi(V(100, 0), 16), new Pi(V(100, 100)) }), Street);
        Check("continue: one edge", g.EdgeCount, 1);
        Check("continue: two nodes", g.NodeCount, 2);
        Check("continue: took the old edge", r.Continued.Count, 1);
        var a = g.Edges.Single().Alignment;
        Check("continue: corner at the old end", a.Pis.Count == 3 && Vector2.Distance(a.Pis[1].Position, V(100, 0)) < 0.01f);
        Check("continue: radius", a.EffectiveRadius(1), 16f);
        Check("continue: no issues", Validation.Check(g).Count, 0);

        // A tighter radius than the profile allows is red, as a drawn corner is.
        var tight = new SplineGraph();
        tight.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        tight.AddSpline(new Alignment(new[] { new Pi(V(100, 0), 5), new Pi(V(100, 100)) }), Street);
        Check("continue: below MinRadius is Invalid", Validation.Worst(Validation.Check(tight)) == Severity.Invalid);

        // Legs too short for the radius clamp (amber), as a drawn corner does.
        var clamp = new SplineGraph();
        clamp.AddSpline(Line(V(0, 0), V(10, 0)), Street);
        clamp.AddSpline(new Alignment(new[] { new Pi(V(10, 0), 60), new Pi(V(10, 10)) }), Street);
        Check("continue: clamped", clamp.Edges.Single().Alignment.IsClamped(1));

        // The old road's own corner keeps its built radius; Alt makes a hard joint.
        var bent = new SplineGraph();
        bent.AddSpline(new Alignment(new[] { new Pi(V(0, 100)), new Pi(V(0, 0), 20), new Pi(V(100, 0)) }), Street);
        bent.AddSpline(new Alignment(new[] { new Pi(V(100, 0), Hard: true), new Pi(V(100, 100)) }), Street);
        var ba = bent.Edges.Single().Alignment;
        Check("continue: old corner kept", ba.EffectiveRadius(1), 20f);
        Check("continue: hard joint", ba.Pis.Count == 4 && ba.Pis[2].Hard);

        // Both ends on dead ends: three roads become one.
        var both = new SplineGraph();
        both.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        both.AddSpline(Line(V(200, 100), V(200, 200)), Street);
        both.AddSpline(new Alignment(new[] { new Pi(V(100, 0), 16), new Pi(V(200, 0), 16), new Pi(V(200, 100), 16) }), Street);
        Check("continue both ends: one edge", both.EdgeCount, 1);
        Check("continue both ends: two nodes", both.NodeCount, 2);

        // Back onto the other end of the same road: a loop on one node.
        var loop = new SplineGraph();
        loop.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        loop.AddSpline(new Alignment(new[] { new Pi(V(100, 0), 16), new Pi(V(50, 80), 16), new Pi(V(0, 0), 16) }), Street);
        Check("continue loop: one edge", loop.EdgeCount, 1);
        Check("continue loop: one node", loop.NodeCount, 1);

        // Continuing a T's stem from its dead end: the stem grows, the T stays.
        var t = new SplineGraph();
        t.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        t.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        t.AddSpline(new Alignment(new[] { new Pi(V(100, 100), 16), new Pi(V(200, 100)) }), Street);
        Check("continue stem: T kept", t.EdgeCount, 3);
        Check("continue stem: label", Junctions.Label(t, t.NodeAt(V(100, 0))!.Value) ?? "", "T-junction · 90°");

        // The preview's solid part: the old road up to where the joint's corner starts (100 − 16 m).
        Check("continue: solid until the corner", r.SolidUntil, 84f);

        // A continued road crossing its own built part makes a 4-way there, like crossing any road.
        var own = new SplineGraph();
        own.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var lp = own.AddSpline(new Alignment(new[] { new Pi(V(200, 0), 16), new Pi(V(200, -100), 16), new Pi(V(100, -100), 16), new Pi(V(100, 50)) }), Street);
        int? x = own.NodeAt(V(100, 0));
        Check("loop over own road: junction", x is not null);
        Check("loop over own road: edges", own.EdgeCount, 3);
        Check("loop over own road: label", x is { } xn ? Junctions.Label(own, xn) ?? "" : "", "4-way · 90°");
        Check("loop over own road: no issues", Validation.Check(own).Count, 0);

        // A leg ending on its own road makes a T there.
        var tee = new SplineGraph();
        tee.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        tee.AddSpline(new Alignment(new[] { new Pi(V(200, 0), 16), new Pi(V(200, -100), 16), new Pi(V(100, -100), 16), new Pi(V(100, 0)) }), Street);
        Check("ends on own road: T", tee.NodeAt(V(100, 0)) is { } tn ? Junctions.Label(tee, tn) ?? "" : "", "T-junction · 90°");

        // One spline drawn across itself: the same junction.
        var fig = new SplineGraph();
        fig.AddSpline(new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(200, 0), 16), new Pi(V(200, -100), 16), new Pi(V(100, -100), 16), new Pi(V(100, 50)) }), Street);
        Check("crosses itself: 4-way", fig.NodeAt(V(100, 0)) is { } fn ? Junctions.Label(fig, fn) ?? "" : "", "4-way · 90°");
        Check("crosses itself: no issues", Validation.Check(fig).Count, 0);

        // Another profile: the joint is rounded like one road, and the avenue keeps its edge up to where the corner
        // starts, so the street carries the whole corner and they meet straight on (no bend, no label, a taper).
        var mix = new SplineGraph();
        mix.AddSpline(Line(V(0, 0), V(100, 0)), Avenue);
        var m = mix.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        float tl = Street.DefaultRadius; // a 90° corner's tangent length
        Check("continue other profile: two edges", mix.EdgeCount, 2);
        Check("continue other profile: kept the avenue", m.Kept.Count == 1 && mix.Edge(m.Kept[0]).Rules.Id == "avenue");
        Check("continue other profile: avenue ends where the corner starts", mix.Edge(m.Kept[0]).Alignment.Length, 100 - tl);
        int joint = m.Nodes[0];
        Check("continue other profile: node at the corner start", Vector2.Distance(mix.Node(joint).Position, V(100 - tl, 0)) < 0.01f);
        Check("continue other profile: street curves at its radius", mix.Edge(m.Edges[0]).Alignment.EffectiveRadius(1), Street.DefaultRadius);
        Check("continue other profile: straight on, no bend fill", Junctions.BendFill(mix, joint) is null);
        Check("continue other profile: no label, no issues", Junctions.Label(mix, joint) is null && Validation.Check(mix).Count == 0);
        Check("continue other profile: taper, centre line runs on", Junctions.Footprint(mix, joint) is { Continuous: true });
        Check("continue other profile: old node gone", mix.NodeCount, 3);

        // The same drawn the other way: a street ending on the avenue's dead end.
        var mixEnd = new SplineGraph();
        mixEnd.AddSpline(Line(V(0, 0), V(100, 0)), Avenue);
        var me = mixEnd.AddSpline(new Alignment(new[] { new Pi(V(100, 100)), new Pi(V(100, 0), 16) }), Street);
        Check("continue other profile at the end: kept", me.Kept.Count == 1 && mixEnd.Edge(me.Kept[0]).Alignment.Length is var l && MathF.Abs(l - 84) < 0.01f);
        Check("continue other profile at the end: avenue's direction kept", Vector2.Distance(mixEnd.Edge(me.Kept[0]).Alignment.Pis[0].Position, V(0, 0)) < 0.01f);
        Check("continue other profile at the end: no issues", Validation.Check(mixEnd).Count, 0);
    }

    /// <summary>A short street at 30° off an avenue: not even a sharp corner fits within its cap. The street is cut back
    /// (it used to stay uncut and run across the avenue), the avenue on the sharp side to where the sides cross, and on
    /// the wide side the outline runs on past the node to where the street's side leaves the avenue.</summary>
    private void Squeezed()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Avenue);
        var d = SplineMath.Direction(-30f * MathF.PI / 180f) * 30f;
        var r = g.AddSpline(Line(V(100, 0), V(100, 0) + d), Street);
        int node = r.Nodes[0];
        var f = Junctions.Footprint(g, node)!;
        var street = r.Edges[0];
        var avenue = g.Arms(node).Where(a => a.Rules.Id == "avenue").ToList();
        float CutOf(Arm a) => f.CutBack(a.EdgeId, a.AtStart);
        Check("squeezed: street cut to its cap", f.CutBack(street, true), 27f);
        Check("squeezed: avenue cut on the sharp side", CutOf(avenue.Single(a => a.Direction.X > 0)), 32.785f);
        Check("squeezed: avenue uncut on the wide side", CutOf(avenue.Single(a => a.Direction.X < 0)), 0f);
        Check("squeezed: outline corner past the node", f.Outline.Any(p => Vector2.Distance(p, V(108.8f, -12)) < 0.6f));
    }

    /// <summary>An avenue running on into a street tapers down to it instead of ending in a step.</summary>
    private void Transition()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(100, 0)), Avenue);
        var r = g.AddSpline(Line(V(100, 0), V(200, 0)), Street);
        int node = r.Nodes[0];
        var f = Junctions.Footprint(g, node);
        Check("transition: footprint", f is not null);
        if (f is null) return;
        var avenue = g.Arms(node).Single(a => a.Rules.Id == "avenue");
        Check("transition: avenue cut by the taper", f.CutBack(avenue.EdgeId, avenue.AtStart), 30f);
        Check("transition: street uncut", f.CutBack(r.Edges[0], true), 0f);
        bool Has(float x, float y) => f.Outline.Any(p => Vector2.Distance(p, V(x, y)) < 0.05f);
        Check("transition: street width at the node", Has(100, 6) && Has(100, -6));
        Check("transition: avenue width at the cut", Has(70, 12) && Has(70, -12));
        Check("transition: no label, no issues", Junctions.Label(g, node) is null && Validation.Check(g).Count == 0);
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
