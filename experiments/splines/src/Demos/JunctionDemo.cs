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
/// clamped radius, split → merge round trips on a straight and on an arc, and kerb handles. Prints each case, then
/// <c>Demo junctions: all ok</c> or <c>FAILED</c>. Pure Core, no scene needed.
/// </summary>
public partial class JunctionDemo : Node
{
    private readonly List<string> _failures = new();

    private static readonly ProfileRules Street = new()
    {
        Id = "street", Width = 12, DefaultRadius = 16, MinRadius = 10, JunctionKind = JunctionKind.Node,
        MinJunctionAngle = 30, ConnectsTo = new[] { "avenue" }, KerbRadius = 6, MinKerbRadius = 2, MaxKerbRadius = 16,
    };
    private static readonly ProfileRules Avenue = Street with { Id = "avenue", Width = 24, DefaultRadius = 60, MinRadius = 40, MinJunctionAngle = 45, ConnectsTo = new[] { "street" },
        KerbRadius = 10, MinKerbRadius = 4, MaxKerbRadius = 30 };
    private static readonly ProfileRules Rail = new()
    {
        Id = "rail", Width = 5, DefaultRadius = 500, MinRadius = 300, JunctionKind = JunctionKind.Turnout, TurnoutMaxAngle = 6.3f,
    };
    private static readonly ProfileRules Canal = new() { Id = "canal", Width = 14, DefaultRadius = 40, MinRadius = 25, MinJunctionAngle = 45,
        KerbRadius = 8, MinKerbRadius = 4, MaxKerbRadius = 20 };

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-junctions")) Run();
    }

    private void Run()
    {
        TJunction();
        FourWay();
        OnCurve();
        RunsOn();
        TooSharp();
        RailBranch();
        ThroughJunction();
        NotConnected();
        OverlapAndClamp();
        SplitMerge();
        Continue();
        Squeezed();
        Transition();
        TransitionAtT();
        Kerbs();
        KerbShapes();
        foreach (var f in _failures) GD.PrintErr($"Demo junctions: FAILED {f}");
        GD.Print($"Demo junctions: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private static Vector2 V(float x, float z) => new(x, z);
    private static Alignment Line(Vector2 a, Vector2 b) => new(new[] { new Pi(a), new Pi(b) });

    /// <summary>An avenue running on into a street at a T (the avenue turning off): the avenue's side across from the
    /// branch tapers down to the street's, no step at the node.</summary>
    private void TransitionAtT()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(100, 0)), Avenue);
        g.AddSpline(Line(V(100, 0), V(200, 0)), Street);
        g.AddSpline(Line(V(100, 0), V(100, 100)), Avenue);
        int node = g.NodeAt(V(100, 0)) ?? -1;
        Check("T transition: 3 arms", node >= 0 ? g.Arms(node).Count : 0, 3);
        var f = node >= 0 ? Junctions.Footprint(g, node) : null;
        Check("T transition: footprint", f is not null);
        if (f is null) return;
        // The far side (z < 0): 12 m out along the avenue, 6 m at the node and along the street, nothing between.
        var far = f.Outline.Where(p => p.Y < -0.01f).ToList();
        Check("T transition: street width at the node", far.Where(p => p.X >= 100 - 1e-3f).Max(p => -p.Y), 6f);
        Check("T transition: no step (each point narrower nearer the node)",
            far.Where(p => p.X < 100).All(p => -p.Y <= 6 + (100 - p.X) * 0.5f + 1e-3f));
        Check("T transition: avenue cut back to the taper (30 m)", f.CutBack(g.Arms(node).First(a => a.Rules.Id == "avenue" && a.Direction.X < -0.5f).EdgeId,
            g.Arms(node).First(a => a.Rules.Id == "avenue" && a.Direction.X < -0.5f).AtStart), 30f);
    }

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
        // Each arm is cut back by the other road's half width plus the 6 m kerb.
        foreach (var c in f?.Cuts ?? Array.Empty<ArmCut>()) Check($"T: cut-back edge {c.EdgeId}", c.CutBack, 12f);
        Check("T: two curbs", f?.Curbs.Count ?? 0, 2);
        Check("T: curb centre", f?.Curbs.Any(c => Vector2.Distance(c.Centre, V(88, 12)) < 0.01f) == true);
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
        // Street arms cut back past the avenue's half width (12) + the street's 6 m kerb (the narrower arm's); avenue
        // arms by 6 + 6.
        var f = Junctions.Footprint(g, centre)!;
        Check("X: street cut-back", f.Cuts.Where(c => g.Edge(c.EdgeId).Rules.Id == "street").Max(c => c.CutBack), 18f);
        Check("X: avenue cut-back", f.Cuts.Where(c => g.Edge(c.EdgeId).Rules.Id == "avenue").Max(c => c.CutBack), 12f);
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

    /// <summary>Two arms with no curb between them are joined along their own sides round the node, not by a chord
    /// between their cut ends: a T on a circle keeps the circle's far side, and the outside of a gap wider than 180°
    /// (a corner straightened into a T) goes round the node.</summary>
    private void RunsOn()
    {
        var g = new SplineGraph();
        var circle = new Alignment(new[] { new Pi(V(0, -80)), new Pi(V(80, -80), 80), new Pi(V(80, 0)), new Pi(V(80, 80), 80), new Pi(V(0, 80)) });
        g.AddSpline(circle, Street);
        var r = g.AddSpline(Line(V(160, 0), V(80, 0)), Street);
        int node = r.Nodes.Single(n => g.Arms(n).Count == 3);
        var f = Junctions.Footprint(g, node)!;
        Check("Runs on: far side at the node", f.Outline.Any(o => Vector2.Distance(o, V(74, 0)) < 0.05f));
        // Every outline point on the far side sits on the circle's inner side (R 74), none on a chord inside it.
        var far = f.Outline.Where(o => o.X < 77).ToList();
        Check("Runs on: far side follows the curve", far.Count > 2 && far.All(o => MathF.Abs(o.Length() - 74) < 0.05f));

        g = new SplineGraph();
        g.AddSpline(Line(V(0, 100), V(0, -100)), Street);
        g.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        g.RemoveEdge(g.Edges.Single(e => e.Alignment.Pis.Any(p => p.Position.Y < -1)).Id);
        g.AddSpline(Line(V(0, 0), V(70, -70)), Street);
        f = Junctions.Footprint(g, g.Nodes.Single(n => g.Arms(n.Id).Count == 3).Id)!;
        var mid = 6 * SplineMath.Direction((90 + 225 / 2f) * MathF.PI / 180);
        Check("Runs on: round outside of a 225° gap", f.Outline.Any(o => Vector2.Distance(o, mid) < 0.05f));
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

        // Not straight through: a corner node stays when the branch goes, and gets a bend footprint (a kerb inside).
        var k = new SplineGraph();
        k.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var stem = k.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        k.RemoveEdge(k.Edges.First(e => Vector2.Distance(e.Alignment.Pis[^1].Position, V(200, 0)) < 0.01f).Id);
        Check("corner node stays", k.EdgeCount, 2);
        Check("corner node: bend footprint", Junctions.Footprint(k, stem.Nodes[0]) is { Bend: true, Curbs.Count: 1 });
        Check("corner node: no bend fill", Junctions.BendFill(k, stem.Nodes[0]) is null);
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
        // Its closing joint is a rounded corner like the others; the node moves to the first leg's middle.
        var ring = loop.Edges.Single();
        Check("continue loop: node mid-leg", loop.Node(ring.Start).Position, V(50, 0));
        Check("continue loop: joint rounded", ring.Alignment.EffectiveRadius(ring.Alignment.Pis.Count - 2), 16f);
        // Moved and joined again (the Edit tool): still one loop on one node.
        loop.Reconnect(loop.MoveGroup(Array.Empty<int>(), new[] { ring.Id }, V(30, 20)));
        Check("loop moved: one edge", loop.EdgeCount, 1);
        Check("loop moved: one node", loop.NodeCount, 1);
        Check("loop moved: closed", loop.Edges.Single() is { } moved && moved.Start == moved.End);

        // A P's loose end joined just below its own corner (an Edit drop): the corner stays round, the up arm is cut
        // back short of it, not round it onto the top road.
        var pg = new SplineGraph();
        pg.AddSpline(new Alignment(new[] { new Pi(V(0, 160)), new Pi(V(0, 0), 16), new Pi(V(160, 0), 16), new Pi(V(160, 90), 16), new Pi(V(0, 30)) }), Street);
        Check("P: T on its own road", pg.NodeAt(V(0, 30)) is { } pn && Junctions.Label(pg, pn) is not null);
        var pfp = Junctions.Footprint(pg, pg.NodeAt(V(0, 30))!.Value)!;
        // The up arm is the loop's start (it runs from the node up to the corner, 30 m off, whose arc starts at 14 m).
        var up = pfp.Cuts.Single(c => c.AtStart && pg.Edge(c.EdgeId).Start == pg.Edge(c.EdgeId).End);
        Check("P: up arm cut short of its corner", up.CutBack < 14f);
        Check("P: curbs", pfp.Curbs.Count, 2);
        // Left lying beside its own stem, not joined: overlaps itself.
        var pb = new SplineGraph();
        pb.AddSpline(new Alignment(new[] { new Pi(V(0, 160)), new Pi(V(0, 0), 16), new Pi(V(160, 0), 16), new Pi(V(160, 90), 16), new Pi(V(4, 96)) }), Street);
        Check("P beside its stem: overlaps itself", Validation.Check(pb).Any(i => i.Message == "overlaps itself"));
        Check("P joined: no issues", Validation.Check(pg).Count, 0);

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

        // Straight on (no corner): the avenue is kept up to its old end, and that node stays the joint, not a second
        // node beside it (the street used to end on whichever of the two the rounding picked, often the emptied one, so
        // the two didn't connect). Several lengths, as it came down to rounding.
        foreach (float len in new[] { 100f, 480f, 333.3f, 77.7f, 123.45f, 251.9f })
        {
            var run = new SplineGraph();
            run.AddSpline(Line(V(300, 500), V(450, 500)), Street);           // a street first, the avenue drawn on from it
            run.AddSpline(Line(V(450, 500), V(450 + len, 500)), Avenue);
            run.AddSpline(Line(V(450 + len, 500), V(600 + len, 500)), Street);
            Check($"continue other profile straight on ({len} m): joined at one node",
                run.NodeCount == 4 && run.NodeAt(V(450 + len, 500)) is { } rn && run.Arms(rn).Count == 2 && Validation.Check(run).Count == 0);
        }
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

    /// <summary>Kerb knobs and road handles on a street T (DESIGN.md → Junctions → Kerb handles): a road handle per arm at
    /// its cut-back and a knob per kerb, their ranges, a road handle making its kerbs lopsided and keeping a difference
    /// set with a knob, a knob making its kerb round, below the minimum invalid, reset, and the values kept through a
    /// junction move, a split and a merge.</summary>
    private void Kerbs()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var r = g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        int node = r.Nodes[0];
        int stemId = r.Edges[0];
        int eastId = g.Arms(node).Single(a => a.Direction.X > 0.5f).EdgeId;
        bool eastAtStart = g.Arms(node).Single(a => a.EdgeId == eastId).AtStart;
        var hs = Junctions.KerbHandles(g, node, anarchy: false);
        Check("Kerb: a road handle per arm", hs.Count, 3);
        var stem = hs.Single(h => h.EdgeId == stemId);
        Check("Kerb: stem handle at its cut-back", stem.Station, 12f);
        Check("Kerb: stem handle on the centre line", stem.Position, V(100, 12));
        Check("Kerb: stem kerbs R 6", stem.Radii.All(x => MathF.Abs(x.Min - 6) < 0.05f && MathF.Abs(x.Max - 6) < 0.05f));
        // Pulled out alone, the stem's kerb ends grow until they're the street's largest kerb, R 16 at 6 + 16 = 22;
        // pulled in, until they're its tightest, R 2 at 6 + 2 = 8.
        Check("Kerb: stem max", stem.StationOf(stem.FactorMax), 22f);
        Check("Kerb: stem max is the largest kerb", stem.MaxLimit == KerbLimit.Radius);
        Check("Kerb: stem min", stem.StationOf(stem.FactorMin), 8f);
        // With Anarchy the minimum goes, but a kerb can't be more lopsided than 4 : 1 (6 m against 1.5 m).
        var anarchy = Junctions.KerbHandles(g, node, anarchy: true).Single(h => h.EdgeId == stemId);
        Check("Kerb: Anarchy goes to the flare limit", anarchy.StationOf(anarchy.FactorMin), 7.5f);
        var knobs = Junctions.KerbKnobs(g, node, anarchy: false);
        Check("Kerb: a knob per kerb", knobs.Count, 2);
        Check("Kerb: knob range R 2..16", knobs.All(k => MathF.Abs(k.Min - 2) < 0.01f && MathF.Abs(k.Max - 16) < 0.01f));
        var eastKnob = knobs.Single(k => k.EdgeId == eastId || k.OtherEdgeId == eastId);
        Check("Kerb: knob in the kerb's middle", Vector2.Distance(eastKnob.Position, eastKnob.At(6)) < 0.05f);

        g.SetKerb(stemId, true, new KerbEnds(14, 14)); // R 14 at the stem: its kerbs start 6 + 14 = 20 m up it
        var f = Junctions.Footprint(g, node)!;
        Check("Kerb: stem cut at the handle", f.CutBack(stemId, true), 20f);
        Check("Kerb: main arms still 12", f.Cuts.Where(c => c.EdgeId != stemId).All(c => MathF.Abs(c.CutBack - 12) < 0.01f));
        Check("Kerb: both curbs set and lopsided", f.Curbs.Count == 2 && f.Curbs.All(c => c.Set && c.Control is not null));
        Check("Kerb: rated R 6 at the main road, R 14 up the stem", f.Curbs.All(c => MathF.Abs(c.Radius - 6) < 0.01f && MathF.Abs(c.MaxRadius - 14) < 0.01f));
        Check("Kerb: outline reaches it", f.Outline.Any(p => Vector2.Distance(p, V(94, 20)) < 0.05f) && f.Outline.Any(p => Vector2.Distance(p, V(106, 20)) < 0.05f));
        Check("Kerb: no issues", Validation.Check(g).Count, 0);
        g.ResetKerbs(node);

        // The knob between east and the stem: that kerb round at R 10, the other one left at 6.
        g.SetKerb(eastKnob.EdgeId, eastKnob.AtStart, +1, 10);
        g.SetKerb(eastKnob.OtherEdgeId, eastKnob.OtherAtStart, -1, 10);
        f = Junctions.Footprint(g, node)!;
        Check("Kerb: knob makes its kerb round R 10", f.Curbs.Count(c => MathF.Abs(c.Radius - 10) < 0.01f && MathF.Abs(c.MaxRadius - 10) < 0.01f) == 1
            && f.Curbs.Count(c => MathF.Abs(c.Radius - 6) < 0.01f && MathF.Abs(c.MaxRadius - 6) < 0.01f) == 1);
        Check("Kerb: east cut 6 + 10", f.CutBack(eastId, eastAtStart), 16f);
        // The stem's road handle now scales its two ends (10 toward east, 6 toward west) by one factor.
        stem = Junctions.KerbHandles(g, node, anarchy: false).Single(h => h.EdgeId == stemId);
        Check("Kerb: stem handle sees R 10 and 6", stem.Sides.Count == 2 && stem.Sides.Any(x => MathF.Abs(x.Radius - 10) < 0.01f) && stem.Sides.Any(x => MathF.Abs(x.Radius - 6) < 0.01f));
        foreach (var side in stem.Sides) g.SetKerb(stemId, true, side.Side, side.Radius * 1.5f);
        var ends = g.Edge(stemId).KerbStart;
        Check("Kerb: road handle keeps the difference (15 and 9)", new[] { ends.Left ?? 0, ends.Right ?? 0 }.OrderBy(x => x).SequenceEqual(new[] { 9f, 15f }));

        // Below the minimum: only a control set with Anarchy gets there, and it's invalid.
        g.SetKerb(stemId, true, new KerbEnds(0.5f, 0.5f));
        Check("Kerb: below min is invalid", Validation.Check(g).Any(i => i.Code == "kerb-min" && i.Severity == Severity.Invalid));
        Check("Kerb: reset", g.ResetKerbs(node) && g.Arms(node).All(a => !g.Edge(a.EdgeId).KerbAt(a.AtStart).IsSet));

        // Kept through edits: a junction move (reconnect renumbers the edges), a split of the arm, a merge.
        g.SetKerb(stemId, true, new KerbEnds(14, 12));
        g.Reconnect(g.MoveNode(node, V(104, 0)), node);
        int moved = g.NodeAt(V(104, 0))!.Value;
        var stemArm = g.Arms(moved).Single(a => a.Direction.Y > 0.5f);
        Check("Kerb: kept through a junction move", g.Edge(stemArm.EdgeId).KerbAt(stemArm.AtStart) == new KerbEnds(14, 12));
        var (_, left, _) = g.SplitEdge(stemArm.EdgeId, 50);
        stemArm = g.Arms(moved).Single(a => a.Direction.Y > 0.5f);
        Check("Kerb: kept by the piece at the junction", left is not null && g.Edge(stemArm.EdgeId).KerbAt(stemArm.AtStart) == new KerbEnds(14, 12));
        var west = g.Arms(moved).Single(a => a.Direction.X < -0.5f);
        g.SetKerb(west.EdgeId, !west.AtStart, new KerbEnds(7, 8)); // its far (dead) end, to follow it through the merge
        g.RemoveEdge(stemArm.EdgeId);
        var merged = g.Edges.Single(e => e.Rules.Id == "street" && e.Alignment.Length > 150);
        Check("Kerb: far ends kept through a merge", merged.KerbStart == new KerbEnds(7, 8) || merged.KerbEnd == new KerbEnds(7, 8));
    }

    /// <summary>Kerb controls on junctions of every shape: T at 30–150°, skewed and square crossings of a street and an
    /// avenue, a 5-way and a 6-way, and a crossing on a curve. Every kerb gets a knob and every arm with a kerb a road
    /// handle, each with a real range (at least 4 m of radius), and taking either to both ends of its range builds with
    /// no issues: a road handle cut back where it says, a knob's kerb round at its radius.</summary>
    private void KerbShapes()
    {
        foreach (float deg in new[] { 30f, 45f, 60f, 75f, 90f, 120f, 150f })
        {
            var g = new SplineGraph();
            g.AddSpline(Line(V(0, 0), V(300, 0)), Street);
            g.AddSpline(Line(V(150, 0), V(150, 0) + Dir(deg) * 120), Street);
            Shape($"T {deg:0}°", g, V(150, 0), 3);
        }
        foreach (float deg in new[] { 45f, 60f, 90f })
        {
            var g = new SplineGraph();
            g.AddSpline(Line(V(0, 0), V(300, 0)), Avenue);
            g.AddSpline(Line(V(150, 0) - Dir(deg) * 120, V(150, 0) + Dir(deg) * 120), Street);
            Shape($"X {deg:0}°", g, V(150, 0), 4);
        }
        foreach (int arms in new[] { 5, 6 })
        {
            var g = new SplineGraph();
            g.AddSpline(Line(V(0, 0), V(300, 0)), Street);
            g.AddSpline(Line(V(150, -120), V(150, 120)), Street);
            g.AddSpline(Line(V(150, 0), V(150, 0) + Dir(45) * 120), Street);
            if (arms == 6) g.AddSpline(Line(V(150, 0), V(150, 0) + Dir(-135) * 120), Street);
            Shape($"{arms}-way", g, V(150, 0), arms);
        }
        {
            var g = new SplineGraph();
            g.AddSpline(new Alignment(new[] { new Pi(V(0, 200)), new Pi(V(150, 0), 120), new Pi(V(300, 200)) }), Avenue);
            var r = g.AddSpline(Line(V(125, -100), V(125, 300)), Street);
            Shape("on a curve", g, g.Node(r.Nodes.Single(n => g.Arms(n).Count == 4)).Position, 4);
        }

        static Vector2 Dir(float deg) => SplineMath.Direction(deg * MathF.PI / 180f);
    }

    private void Shape(string name, SplineGraph g, Vector2 at, int arms)
    {
        int node = g.NodeAt(at)!.Value;
        var f = Junctions.Footprint(g, node)!;
        var hs = Junctions.KerbHandles(g, node, anarchy: false);
        var knobs = Junctions.KerbKnobs(g, node, anarchy: false);
        // An arm between two straight-on neighbours (a T's top, both sides) has no kerb, so no handle.
        Check($"{name}: a kerb per corner ({f.Curbs.Count}), a knob each ({knobs.Count}), a road handle per arm with one ({hs.Count})",
            f.Curbs.Count >= arms - 1 && knobs.Count == f.Curbs.Count && hs.Count >= arms - 1);
        foreach (var h in hs)
        {
            string arm = $"{name} road {SplineMath.Angle(g.Arms(node).First(a => a.EdgeId == h.EdgeId && a.AtStart == h.AtStart).Direction) * 180 / MathF.PI:0}°";
            float r0 = h.Outer.Radius;
            Check($"{arm}: R {r0:0.#} in {r0 * h.FactorMin:0.#}..{r0 * h.FactorMax:0.#}, slides {h.Min:0.#}..{h.Max:0.#} m",
                h.FactorMin <= 1 + 1e-3f && 1 <= h.FactorMax + 1e-3f && r0 * (h.FactorMax - h.FactorMin) >= 4f && h.Max - h.Min >= 2f);
            foreach (float x in new[] { h.FactorMin, h.FactorMax })
            {
                var t = g.Clone();
                foreach (var side in h.Sides) t.SetKerb(h.EdgeId, h.AtStart, side.Side, side.Radius * x);
                var tf = Junctions.Footprint(t, node)!;
                bool cutOk = MathF.Abs(tf.CutBack(h.EdgeId, h.AtStart) - h.StationOf(x)) < 0.05f;
                var issues = Validation.Check(t);
                Check($"{arm}: at ×{x:0.##} it builds, cut {tf.CutBack(h.EdgeId, h.AtStart):0.#} m (handle {h.StationOf(x):0.#}){string.Concat(issues.Select(i => " · " + i.Message))}", cutOk && issues.Count == 0);
            }
        }
        foreach (var k in knobs)
        {
            string corner = $"{name} knob {SplineMath.Angle(k.Bisector) * 180 / MathF.PI:0}°";
            Check($"{corner}: R {k.Radii.Min:0.#} in {k.Min:0.#}..{k.Max:0.#}", k.Max - k.Min >= 4f);
            foreach (float r in new[] { k.Min, k.Max })
            {
                var t = g.Clone();
                t.SetKerb(k.EdgeId, k.AtStart, +1, r);
                t.SetKerb(k.OtherEdgeId, k.OtherAtStart, -1, r);
                var issues = Validation.Check(t);
                bool round = Junctions.Footprint(t, node)!.Curbs.Any(c => c.Set && MathF.Abs(c.Radius - r) < 0.05f && MathF.Abs(c.MaxRadius - r) < 0.05f);
                Check($"{corner}: at R {r:0.#} it builds round{string.Concat(issues.Select(i => " · " + i.Message))}", round && issues.Count == 0);
            }
        }
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
