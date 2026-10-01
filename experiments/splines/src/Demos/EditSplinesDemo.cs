using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-edit-splines</c> (S5): checks the Edit tool's graph operations in Core. Moving a corner point keeps the
/// other radii, a dragged junction stays one junction, a road dragged across another joins it (or is an Invalid
/// crossing where the profiles don't connect), a dropped dead end joins a node, a road or continues a dead end, the
/// radius knob's maths, Alt-straighten, deleting one edge between two junctions, custom data across a reconnect (S5a);
/// the radial menu's Smooth · Hard · Straighten · Delete on a corner point and on a node, and moving a group (S5b).
/// Prints each case, then <c>Demo edit-splines: all ok</c> or <c>FAILED</c>. Pure Core, no scene needed.
/// </summary>
public partial class EditSplinesDemo : Node
{
    private readonly List<string> _failures = new();

    private static readonly ProfileRules Street = new()
    {
        Id = "street", Width = 12, DefaultRadius = 16, MinRadius = 10, JunctionKind = JunctionKind.Node,
        MinJunctionAngle = 30, ConnectsTo = new[] { "avenue" },
    };
    private static readonly ProfileRules Avenue = Street with { Id = "avenue", Width = 24, DefaultRadius = 60, MinRadius = 40, MinJunctionAngle = 45, ConnectsTo = new[] { "street" } };
    private static readonly ProfileRules Canal = new() { Id = "canal", Width = 14, DefaultRadius = 40, MinRadius = 25, MinJunctionAngle = 45 };

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-edit-splines")) Run();
    }

    private void Run()
    {
        MovePiKeepsRadii();
        MoveJunction();
        DragAcross();
        DropDeadEnd();
        Knob();
        Straighten();
        DeletePart();
        KeepsData();
        RadialPi();
        RadialNode();
        MoveGroup();
        foreach (var f in _failures) GD.PrintErr($"Demo edit-splines: FAILED {f}");
        GD.Print($"Demo edit-splines: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private static Vector2 V(float x, float z) => new(x, z);
    private static Alignment Line(Vector2 a, Vector2 b) => new(new[] { new Pi(a), new Pi(b) });

    /// <summary>The edge's PI nearest a point (for finding a corner after an edit renumbers edges).</summary>
    private static (GraphEdge Edge, int Index) PiNear(SplineGraph g, Vector2 p) =>
        g.Edges.SelectMany(e => e.Alignment.Pis.Select((q, i) => (e, i, d: Vector2.Distance(q.Position, p))))
            .OrderBy(x => x.d).Select(x => (x.e, x.i)).First();

    private void MovePiKeepsRadii()
    {
        var g = new SplineGraph();
        g.AddSpline(new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 16), new Pi(V(100, 100), 20), new Pi(V(200, 100)) }), Street);
        int id = g.Edges.Single().Id;
        g.MovePi(id, 1, V(110, -10));
        var r = g.Reconnect(new[] { id });
        Check("Move PI: still one edge", g.EdgeCount, 1);
        Check("Move PI: result is that edge", r.Edges.Count, 1);
        var a = g.Edges.Single().Alignment;
        Check("Move PI: moved", a.Pis[1].Position, V(110, -10));
        Check("Move PI: its radius kept", a.EffectiveRadius(1), 16f);
        Check("Move PI: other radius kept", a.EffectiveRadius(2), 20f);
    }

    /// <summary>Re-adding a junction's arms one by one mustn't merge them (no dead-end continuing on reconnect).</summary>
    private void MoveJunction()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var t = g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        int node = t.Nodes[0];
        var r = g.Reconnect(g.MoveNode(node, V(110, 10)), node);
        Check("Move junction: edges", g.EdgeCount, 3);
        Check("Move junction: nodes", g.NodeCount, 4);
        var at = g.NodeAt(V(110, 10));
        Check("Move junction: node moved", at is not null);
        Check("Move junction: arms", at is { } n ? g.Arms(n).Count : 0, 3);
        Check("Move junction: result edges", r.Edges.Count, 3);
    }

    private void DragAcross()
    {
        foreach (var (rules, name) in new[] { (Street, "street"), (Canal, "canal") })
        {
            var g = new SplineGraph();
            g.AddSpline(Line(V(0, 0), V(200, 0)), rules);
            var b = g.AddSpline(Line(V(100, 20), V(100, 100)), Street);
            int start = b.Nodes[0];
            g.Reconnect(g.MoveNode(start, V(100, -20)), start);
            if (name == "street")
            {
                Check("Across street: edges", g.EdgeCount, 4);
                var x = g.NodeAt(V(100, 0));
                Check("Across street: 4-way", x is { } n && g.Arms(n).Count == 4);
                Check("Across street: no issues", Validation.Check(g).Count, 0);
            }
            else
            {
                Check("Across canal: not joined", g.EdgeCount, 2);
                Check("Across canal: Invalid crossing", Validation.Check(g).Any(i => i is { Code: "crossing", Severity: Severity.Invalid }));
            }
        }
    }

    private void DropDeadEnd()
    {
        // On a same-profile dead end: continues it, one road with a corner at the joint.
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var b = g.AddSpline(Line(V(250, 50), V(250, 150)), Street);
        int start = b.Nodes[0];
        g.Reconnect(g.MoveNode(start, V(200, 0)), start);
        Check("Drop on dead end: one edge", g.EdgeCount, 1);
        Check("Drop on dead end: rounded joint", g.Edges.Single().Alignment.EffectiveRadius(1), 16f);

        // On a junction node: joins it.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        var t = g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        b = g.AddSpline(Line(V(150, -60), V(150, -150)), Street);
        start = b.Nodes[0];
        g.Reconnect(g.MoveNode(start, V(100, 0)), start);
        Check("Drop on junction: 4 arms", g.Arms(t.Nodes[0]).Count, 4);

        // On a road's side: splits it into a T.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        b = g.AddSpline(Line(V(120, 40), V(120, 140)), Street);
        start = b.Nodes[0];
        g.Reconnect(g.MoveNode(start, V(120, 0)), start);
        Check("Drop on road: edges", g.EdgeCount, 3);
        Check("Drop on road: T", g.NodeAt(V(120, 0)) is { } n && g.Arms(n).Count == 3);

        // A street's end dropped on an avenue's dead end: continues it round the joint, then the avenue is split back
        // off where the corner starts.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Avenue);
        b = g.AddSpline(Line(V(300, 60), V(300, 200)), Street);
        start = b.Nodes[0];
        g.Reconnect(g.MoveNode(start, V(200, 0)), start);
        Check("Drop street on avenue: edges", g.EdgeCount, 2);
        var street = g.Edges.Single(e => e.Rules.Id == "street");
        var avenue = g.Edges.Single(e => e.Rules.Id == "avenue");
        Check("Drop street on avenue: joined", street.Start == avenue.End || street.End == avenue.End || street.Start == avenue.Start);
        Check("Drop street on avenue: street carries the corner", street.Alignment.Pis.Count, 3);
        Check("Drop street on avenue: no node at the old end", g.NodeAt(V(200, 0)) is null);
    }

    private void Knob()
    {
        var a = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 30), new Pi(V(100, 100)) });
        Check("Knob: radius from its own mid", SplineGraph.KnobRadius(a, 1, a.Corner(1).Mid) ?? -1, 30f);
        Check("Knob: at the PI it's sharp", SplineGraph.KnobRadius(a, 1, V(100, 0)) ?? -1, 0f);
        float far = SplineGraph.KnobRadius(a, 1, V(40, 60)) ?? -1;
        Check("Knob: further in is larger", far > 30);
        var straight = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 30), new Pi(V(200, 0)) });
        Check("Knob: none on a straight corner", SplineGraph.KnobRadius(straight, 1, V(100, 10)) is null);

        var g = new SplineGraph();
        g.AddSpline(a, Street);
        int id = g.Edges.Single().Id;
        g.SetRadius(id, 1, 50);
        g.Reconnect(new[] { id });
        Check("Knob: set radius", g.Edges.Single().Alignment.EffectiveRadius(1), 50f);
    }

    private void Straighten()
    {
        var g = new SplineGraph();
        g.AddSpline(new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 30), 16), new Pi(V(200, 0)) }), Street);
        var e = g.Edges.Single();
        g.MovePi(e.Id, 1, SplineGraph.OntoLine(V(0, 0), V(200, 0), V(90, 40)));
        g.Reconnect(new[] { e.Id });
        var a = g.Edges.Single().Alignment;
        Check("Straighten: on the line", a.Pis[1].Position, V(90, 0));
        Check("Straighten: no turn", a.Corner(1).TurnDegrees, 0f);
    }

    private void DeletePart()
    {
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(300, 0)), Street);
        g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        g.AddSpline(Line(V(200, 0), V(200, 100)), Street);
        Check("Delete part: edges before", g.EdgeCount, 5);
        var middle = g.ClosestEdge(V(150, 0), 1f)!.Value.Edge.Id;
        g.RemoveEdge(middle);
        Check("Delete part: edges after", g.EdgeCount, 4);
        Check("Delete part: gap", g.ClosestEdge(V(150, 0), 1f) is null);
        Check("Delete part: both junction nodes stay", g.NodeAt(V(100, 0)) is not null && g.NodeAt(V(200, 0)) is not null);
    }

    private void KeepsData()
    {
        var g = new SplineGraph();
        g.AddSpline(new Alignment(new[] { new Pi(V(0, 50)), new Pi(V(100, 50), 16), new Pi(V(200, 150)) }), Street, data: "bus lane");
        g.AddSpline(Line(V(0, 0), V(300, 0)), Street);
        var (e, i) = PiNear(g, V(100, 50));
        g.MovePi(e.Id, i, V(100, -40)); // now crosses the other road on both legs
        var r = g.Reconnect(new[] { e.Id });
        Check("Data: edited edge split at both crossings", r.Edges.Count, 3);
        Check("Data: kept on every piece", r.Edges.All(id => g.Edge(id).CustomData as string == "bus lane"));
    }

    /// <summary>The radial menu on a corner point: Smooth takes the largest radius that fits, Hard, Straighten keeps
    /// the point on its neighbours' line, Delete takes it out.</summary>
    private void RadialPi()
    {
        Alignment Bend() => new(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 16), new Pi(V(160, 60), 20), new Pi(V(300, 60)) });
        var g = new SplineGraph();
        g.AddSpline(Bend(), Street);
        int id = g.Edges.Single().Id;
        float fit = g.Edge(id).Alignment.MaxRadius(1);
        g.SetRadius(id, 1, fit);
        g.Reconnect(new[] { id });
        var a = g.Edges.Single().Alignment;
        Check("Smooth PI: largest that fits", a.EffectiveRadius(1), fit);
        Check("Smooth PI: not clamped", !a.IsClamped(1));
        Check("Smooth PI: other radius kept", a.EffectiveRadius(2), 20f);

        g = new SplineGraph();
        g.AddSpline(Bend(), Street);
        id = g.Edges.Single().Id;
        g.SetHard(id, 1);
        Check("Hard PI: sharp", g.Edge(id).Alignment.EffectiveRadius(1), 0f);
        Check("Hard PI: flagged", g.Edge(id).Alignment.Pis[1].Hard);

        g = new SplineGraph();
        g.AddSpline(Bend(), Street);
        id = g.Edges.Single().Id;
        g.RemovePi(id, 1);
        var r = g.Reconnect(new[] { id });
        a = g.Edge(r.Edges.Single()).Alignment;
        Check("Delete PI: one fewer", a.Pis.Count, 3);
        Check("Delete PI: next radius kept", a.EffectiveRadius(1), 20f);
    }

    /// <summary>The radial menu on a node: Smooth rounds a joint (same profile → one edge; another profile → a
    /// rounded joint split back off), Straighten lines a joint up, Delete takes a junction's arms with it.</summary>
    private void RadialNode()
    {
        // A kinked joint (as deleting a junction's third arm leaves; a draw onto the dead end would continue it).
        var g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(100, 0)), Street);
        g.AddSpline(Line(V(100, 0), V(200, 60)), Street, Ends.None);
        int node = g.NodeAt(V(100, 0))!.Value;
        Check("Smooth node: joint turn", MathF.Round(g.JointTurn(node) ?? -1), 31f);
        var r = g.SmoothNode(node);
        Check("Smooth node: one edge", g.EdgeCount, 1);
        var a = g.Edges.Single().Alignment;
        Check("Smooth node: a corner where the node was", a.Pis.Count, 3);
        Check("Smooth node: largest that fits", a.EffectiveRadius(1), a.MaxRadius(1));
        Check("Smooth node: stored as built", a.Pis[1].Radius < 1e5f && !a.IsClamped(1));
        Check("Smooth node: no node left there", g.NodeAt(V(100, 0)) is null);
        Check("Smooth node: result", r?.Edges.Count ?? 0, 1);

        // Another profile: a street running on into an avenue keeps both, the corner rounded on the street.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Avenue);
        g.AddSpline(Line(V(200, 0), V(300, 100)), Street, Ends.None);
        node = g.NodeAt(V(200, 0))!.Value;
        g.SmoothNode(node);
        Check("Smooth mixed: two edges", g.EdgeCount, 2);
        Check("Smooth mixed: both profiles", g.Edges.Select(e => e.Rules.Id).Distinct().Count(), 2);
        Check("Smooth mixed: rounded", g.Edges.Any(e => e.Alignment.Pis.Count == 3 && e.Alignment.EffectiveRadius(1) > 0));
        Check("Smooth mixed: no issues", Validation.Check(g).Count, 0);

        // Straighten: the joint moves onto the line through its neighbours.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(100, 20)), Street);
        g.AddSpline(Line(V(100, 20), V(200, 0)), Street, Ends.None);
        node = g.NodeAt(V(100, 20))!.Value;
        var to = g.StraightenedNode(node);
        Check("Straighten node: target", to ?? V(-1, -1), V(100, 0));
        g.Reconnect(g.MoveNode(node, to!.Value), node);
        Check("Straighten node: straight through", g.JointTurn(g.NodeAt(V(100, 0))!.Value) ?? -1, 0f);

        // Delete a junction: its arms go, the through road's far pieces stay apart.
        g = new SplineGraph();
        g.AddSpline(Line(V(0, 0), V(200, 0)), Street);
        g.AddSpline(Line(V(100, 0), V(100, 100)), Street);
        g.RemoveNode(g.NodeAt(V(100, 0))!.Value);
        Check("Delete node: arms gone", g.EdgeCount, 0);
        Check("Delete node: no nodes left", g.NodeCount, 0);
        Check("Smooth node: none at a dead end", new Func<bool>(() =>
        {
            var h = new SplineGraph();
            h.AddSpline(Line(V(0, 0), V(100, 0)), Street);
            return h.SmoothNode(h.NodeAt(V(0, 0))!.Value) is null;
        })());
    }

    /// <summary>Moving a group: a selected edge moves rigidly, an edge with one end in the group stretches, and the
    /// moved end joins a road it's dropped on.</summary>
    private void MoveGroup()
    {
        var g = new SplineGraph();
        g.AddSpline(new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(50, 30), 16), new Pi(V(100, 0)) }), Street);
        g.AddSpline(Line(V(100, 0), V(100, 100)), Street, Ends.None);
        g.AddSpline(Line(V(0, 200), V(300, 200)), Street);
        var bend = g.Edges.First(e => e.Alignment.Pis.Count == 3).Id;
        var delta = V(0, 50);
        var changed = g.MoveGroup(Array.Empty<int>(), new[] { bend }, delta);
        Check("Group: moved and stretched", changed.Count, 2);
        g.Reconnect(changed);
        var moved = g.Edges.Single(e => e.Alignment.Pis.Count == 3).Alignment;
        Check("Group: rigid corner moved", moved.Pis[1].Position, V(50, 80));
        Check("Group: radius kept", moved.EffectiveRadius(1), 16f);
        var stretched = g.Edges.Single(e => e.Alignment.Pis.Count == 2 && e.Alignment.Pis.Any(p => Vector2.Distance(p.Position, V(100, 100)) < 0.01f));
        Check("Group: stretched end", stretched.Alignment.Pis.Any(p => Vector2.Distance(p.Position, V(100, 50)) < 0.01f));

        // A node alone dropped onto the far road joins it.
        int far = g.NodeAt(V(100, 100))!.Value;
        g.Reconnect(g.MoveGroup(new[] { far }, Array.Empty<int>(), V(0, 100)));
        Check("Group: dropped end joins the road", g.NodeAt(V(100, 200)) is { } n && g.Arms(n).Count == 3);
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
}
