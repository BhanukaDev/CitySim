using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-modes</c> (S6): drives <see cref="SplineDrawTool"/>'s test hooks in Curve, Freehand and Grid mode and checks
/// what they build: a curve at the radius that fits, a chain of curves as one road, a rail curve refused below its
/// minimum; a noisy stroke fitted to a few points that stay on it; a 3 × 2 grid with the right junctions, spacing and
/// one undo step, and a grid laid over a road joining it. Prints <c>Demo modes: all ok</c> or the failures.
/// </summary>
public partial class ModesDemo : Node
{
    [Export] public SplineDrawTool? DrawTool { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-modes")) Callable.From(Run).CallDeferred();
    }

    private void Run()
    {
        if (DrawTool is null || Testbed is null || Network is null)
        {
            GD.PrintErr("Demo modes: FAILED (no DrawTool/Testbed/Network)");
            return;
        }
        Curve(Network);
        Freehand(Network);
        Grid(Network);
        CornerJunctions(Network);
        Testbed.SetMode(DrawMode.Draw);
        DrawTool.ForcedPlanCursor = null;

        foreach (var f in _failures) GD.PrintErr($"Demo modes: FAILED {f}");
        GD.Print($"Demo modes: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private void Curve(SplineNetwork network)
    {
        Testbed!.SetMode(DrawMode.Curve);
        Use("street");
        int before = network.Graph.EdgeCount;
        // Legs of 100 and 60 m at 90°: the bend fits R 60 (the whole of the shorter leg).
        Click(2000, 2000);
        Click(2100, 2000);
        Check("bend placed: nothing built yet", network.Graph.EdgeCount, before);
        Click(2100, 2060);
        Check("third click builds the curve", network.Graph.EdgeCount, before + 1);
        var road = EdgeThrough(network, new NumVector2(2100, 2000));
        Check("the bend's radius is the fit (m)", (int)MathF.Round(road.Alignment.EffectiveRadius(1)), 60);
        Check("and isn't clamped", !road.Alignment.IsClamped(1));
        Check("still drawing: the chain goes on", DrawTool!.IsDrawing);

        // The next curve continues the road on its tangent: the joint runs straight through, so the bend gets the
        // whole of that leg (legs 140 and 100 at 90°: R 100).
        Click(2100, 2200);
        Click(2200, 2200);
        Check("next curve continues the same road", network.Graph.EdgeCount, before + 1);
        road = EdgeThrough(network, new NumVector2(2100, 2200));
        int bend = road.Alignment.Pis.FindIndex(p => p.Position == new NumVector2(2100, 2200));
        Check("one road through both bends", road.Alignment.Pis.Count, 5);
        Check("second bend's fit (m)", (int)MathF.Round(road.Alignment.EffectiveRadius(bend)), 100);
        Check("and isn't clamped", !road.Alignment.IsClamped(bend));
        Check("the joint runs straight through", road.Alignment.EffectiveRadius(bend - 1) == 0 && MathF.Abs(road.Alignment.Corner(bend - 1).TurnDegrees) < 1e-2f);
        DrawTool.UndoForTest();
        Check("Ctrl+Z takes back one curve", EdgeThrough(network, new NumVector2(2100, 2000)).Alignment.Pis.Count, 3);
        Click(2100, 2300);
        DrawTool.UndoForTest();
        Check("Ctrl+Z with a bend placed drops just the bend", DrawTool.IsDrawing);
        DrawTool.FinishForTest();

        Circle(network);
        FreeBend(network);

        // Rail can't take the R 60 these clicks fit: refused, then built red with Anarchy.
        Use("rail");
        before = network.Graph.EdgeCount;
        Click(2000, 2500);
        Click(2100, 2500);
        Click(2100, 2560);
        Check("rail curve under its minimum: refused", network.Graph.EdgeCount, before);
        Testbed.SetAnarchy(true);
        Click(2100, 2560);
        Testbed.SetAnarchy(false);
        DrawTool.FinishForTest();
        Check("built with Anarchy", network.Graph.EdgeCount, before + 1);
        Check("and it stays red", network.Issues.Any(i => i.Code == "radius-min"));
        network.Undo();
    }

    /// <summary>A circle in four quarter curves (R 100 round 1500, 1500): each bend clicked off the tangent lands on
    /// it, the last snaps to where the tangent meets the start's line, and the end on the start closes one loop road
    /// with no kink anywhere.</summary>
    private void Circle(SplineNetwork network)
    {
        Click(1500, 1400);
        Click(1600, 1400);
        Click(1600, 1500);
        Check("bend held on the tangent", SnapClick(1608, 1600), "tangent");
        Click(1500, 1600);
        SnapClick(1400, 1607);
        Click(1400, 1500);
        Check("last bend snaps to close the loop", SnapClick(1401, 1402), "close loop · tangent");
        Click(1500, 1400);
        Check("closing on the start ends the chain", !DrawTool!.IsDrawing);

        var loop = EdgeThrough(network, new NumVector2(1600, 1500));
        var a = loop.Alignment;
        Check("one loop road", loop.Start == loop.End);
        Check("start, 4 bends, 3 joints, end", a.Pis.Count, 9);
        for (int i = 1; i < 8; i += 2)
        {
            Check($"bend {i / 2 + 1} at R 100", (int)MathF.Round(a.EffectiveRadius(i)), 100);
            Check($"bend {i / 2 + 1} not clamped", !a.IsClamped(i));
        }
        Check("every joint straight through", Enumerable.Range(1, 3).All(j => MathF.Abs(a.Corner(2 * j).TurnDegrees) < 1e-2f));
        Check("all arc: length 2πR (m)", (int)MathF.Round(a.Length), (int)MathF.Round(2 * MathF.PI * 100));
        var arms = network.Graph.Arms(loop.Start);
        Check("the loop's node is seamless", arms.Count == 2 && NumVector2.Dot(arms[0].Direction, arms[1].Direction) < -0.9999f);
        Check("no issues on the circle", !network.Issues.Any(i => i.EdgeId == loop.Id || i.NodeId == loop.Start));

        // A joint is a point to connect to: a road started near it snaps onto it and makes a T there.
        Check("a joint snaps like a node", SnapClick(1603, 1501), "snap: node");
        Click(1700, 1500);
        Click(1700, 1560);
        DrawTool.FinishForTest();
        Check("and joins the circle there (3 arms)",
            network.Graph.NodeAt(new NumVector2(1600, 1500)) is { } t ? network.Graph.Arms(t).Count : 0, 3);

        // So is an arc's middle (where its radius knob sits): the first quarter's, at 45°.
        var mid = new NumVector2(1500 + 100 / MathF.Sqrt(2), 1500 - 100 / MathF.Sqrt(2));
        Check("an arc's middle snaps like a node", SnapClick(mid.X + 2, mid.Y - 1), "snap: node");
        Click(1700, 1300);
        Click(1760, 1300);
        DrawTool.FinishForTest();
        Check("and joins the circle there (3 arms)",
            network.Graph.NodeAt(mid) is { } m ? network.Graph.Arms(m).Count : 0, 3);
    }

    /// <summary>
    /// Corner junctions (<c>docs/corner-junctions.html</c>): a bend's dot slides out to its corner point. At the corner
    /// point a road drawn along a side makes a square T, part way out the bend tightens and a branch leaves at any
    /// angle, below the min radius it's refused, and a leg drawn onto the slider ends on it.
    /// </summary>
    private void CornerJunctions(SplineNetwork network)
    {
        Testbed!.SetMode(DrawMode.Draw);
        Use("street");
        var pi = new NumVector2(3100, 1000);
        Click(3000, 1000);
        Click(pi.X, pi.Y);
        Click(3100, 1100);
        DrawTool!.FinishForTest();
        var bend = EdgeThrough(network, pi);
        Check("an L street, R 16 at its corner", (int)MathF.Round(bend.Alignment.EffectiveRadius(1)), 16);
        int edges = network.Graph.EdgeCount;

        // At the corner point, drawn north along the vertical side: a square T, the bend gone.
        Check("the slider's end is the corner point", SnapClick(pi.X + 0.3f, pi.Y - 0.3f), "corner point");
        Click(3100, 900);
        DrawTool.FinishForTest();
        var t = network.Graph.NodeAt(pi);
        Check("one node at the corner point", t is not null);
        if (t is { } tn)
        {
            Check("with 3 arms", network.Graph.Arms(tn).Count, 3);
            Check("square T", Junctions.Label(network.Graph, tn) ?? "", "T-junction · 90°");
            Check("no issues at it", !network.Issues.Any(i => i.NodeId == tn));
        }
        Check("the arms are straight (no corner left)", network.Graph.Edges.All(e => !e.Alignment.Pis.Skip(1).SkipLast(1).Any(q => q.Position == pi)));
        network.Undo();
        Check("one undo puts the bend back", network.Graph.EdgeCount == edges && EdgeThrough(network, pi).Alignment.EffectiveRadius(1) > 15.9f);

        // Part way out: R 12, and a branch at 45° off it.
        var a = EdgeThrough(network, pi).Alignment;
        var at12 = a.BendPoint(1, 12);
        Check("part way out tightens the bend", SnapClick(at12.X, at12.Y), "R 16 → 12 m");
        Click(at12.X + 40, at12.Y - 40);
        DrawTool.FinishForTest();
        var y = network.Graph.NodeAt(at12);
        Check("a node on the tightened bend", y is not null);
        if (y is { } yn)
        {
            var arms = network.Graph.Arms(yn);
            Check("with 3 arms", arms.Count, 3);
            Check("both halves at R 12", arms.Where(x => network.Graph.Edge(x.EdgeId).Rules.Id == "street")
                .Select(x => network.Graph.Edge(x.EdgeId).Alignment)
                .Count(al => Enumerable.Range(1, Math.Max(0, al.Pis.Count - 2)).Any(i => MathF.Abs(al.EffectiveRadius(i) - 12) < 0.05f)), 2);
            Check("the halves show no dots of their own",
                !SplineToolView.RoadPoints(network.Graph).Any(p => NumVector2.Distance(p, at12) < 10));
        }
        network.Undo();

        // Below the street's min radius (10 m): refused.
        a = EdgeThrough(network, pi).Alignment;
        var at6 = a.BendPoint(1, 6);
        SnapClick(at6.X, at6.Y);
        Click(at6.X + 40, at6.Y - 40);
        Check("R 6 is refused (min 10)", network.Graph.EdgeCount, edges);
        DrawTool.FinishForTest();

        // A leg drawn onto the slider ends there: from the north onto the corner point.
        Click(3100, 900);
        Check("ending on the corner point", SnapClick(pi.X + 0.3f, pi.Y - 0.3f), "corner point");
        DrawTool.FinishForTest();
        Check("makes the T there too", network.Graph.NodeAt(pi) is { } e ? network.Graph.Arms(e).Count : 0, 3);
        network.Undo();
    }

    /// <summary>Space frees the bend: off the tangent, the joint is a rounded corner as before.</summary>
    private void FreeBend(SplineNetwork network)
    {
        Click(1800, 1400);
        Click(1900, 1400);
        Click(1900, 1500);
        DrawTool!.ForcedModifiers = DrawModifiers.Space;
        SnapClick(1960, 1580);
        DrawTool.ForcedModifiers = DrawModifiers.None;
        Click(1960, 1700);
        DrawTool.FinishForTest();
        var a = EdgeThrough(network, new NumVector2(1900, 1500)).Alignment;
        int joint = a.Pis.FindIndex(p => p.Position == new NumVector2(1900, 1500));
        Check("Space: bend placed off the tangent", a.Pis.Any(p => p.Position == new NumVector2(1960, 1580)));
        Check("and the joint turns", MathF.Abs(a.Corner(joint).TurnDegrees) > 1f);
    }

    private void Freehand(SplineNetwork network)
    {
        Testbed!.SetMode(DrawMode.Freehand);
        Use("street");
        var rules = Testbed.Profile!.ToRules();
        int before = network.Graph.EdgeCount;
        // The storyboard's stroke: an S with a few metres of hand wobble, 60 samples over ~330 m.
        var stroke = new List<NumVector2>();
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            float noise = 3.5f * MathF.Sin(i * 1.9f) + 2.2f * MathF.Sin(i * 0.73f);
            stroke.Add(new NumVector2(3000 + t * 275, 3160 - 120 * t + 38 * MathF.Sin(t * MathF.PI * 1.6f) + noise));
        }
        DrawTool!.StrokeForTest(stroke);
        Check("stroke builds one road", network.Graph.EdgeCount, before + 1);
        var road = EdgeThrough(network, stroke[0]);
        var a = road.Alignment;
        Check($"a few points ({a.Pis.Count})", a.Pis.Count is >= 3 and <= 6);
        float worst = stroke.Max(p => NumVector2.Distance(a.Curve.ClosestPoint(p).Position, p));
        Check($"stays on the stroke (worst {worst:0.0} m)", worst <= FreehandFit.Tolerance(rules) + 6f);
        Check("every bend at least the minimum", Enumerable.Range(1, a.Pis.Count - 2).All(i => a.EffectiveRadius(i) >= rules.MinRadius - 1e-2f));
        Check("no bend clamped", Enumerable.Range(1, a.Pis.Count - 2).All(i => !a.IsClamped(i)));
        Check("nothing red", !network.Issues.Any(i => i.EdgeId == road.Id && i.Severity == Severity.Invalid));

        var straight = Enumerable.Range(0, 40).Select(i => new NumVector2(3000 + i * 5, 3400 + MathF.Sin(i) * 0.8f)).ToList();
        DrawTool.StrokeForTest(straight);
        Check("a straight stroke is two points", EdgeThrough(network, straight[0]).Alignment.Pis.Count, 2);
    }

    private void Grid(SplineNetwork network)
    {
        Testbed!.SetMode(DrawMode.Grid);
        Use("street");
        Testbed.SetGridBlocks(3, 2);
        Testbed.SetGridFit(GridFit.Even);
        int before = network.Graph.EdgeCount, nodesBefore = network.Graph.Nodes.Count();
        int Arms(float x, float z) => network.Graph.NodeAt(new NumVector2(x, z)) is { } n ? network.Graph.Node(n).Edges.Count : 0;

        // The outline is the clicks; 3 × 2 blocks share it evenly.
        Click(4000, 4000);
        Click(4000 + 230, 4000);
        Click(4100, 4000 + 150);
        // The outline is one loop round its corners (rounded, as the Draw tool makes them), split by the 6 Ts.
        Check("3 × 2 grid: 6 + 7 edges", network.Graph.EdgeCount, before + 13);
        Check("8 nodes", network.Graph.Nodes.Count() - nodesBefore, 8);
        var arms = Enumerable.Range(0, 4).SelectMany(c => Enumerable.Range(0, 3).Select(r => Arms(4000 + c * 230f / 3, 4000 + r * 75f))).ToList();
        Check("far side on the cursor's outline", Arms(4230, 4075), 3);
        Check("4 rounded corners, no nodes", arms.Count(n => n == 0), 4);
        Check("corners at the default radius", network.Graph.Edges.SelectMany(e => e.Alignment.Pis.Skip(1).SkipLast(1))
            .Count(p => p.Position.X is >= 3999 and <= 4231 && p.Position.Y is >= 3999 and <= 4151
                && MathF.Abs(p.Radius - Testbed.Profile.ToRules().DefaultRadius) < 0.01f), 4);
        Check("6 T-junctions", arms.Count(n => n == 3), 6);
        Check("2 four-ways", arms.Count(n => n == 4), 2);
        Check("not drawing after the third click", !DrawTool!.IsDrawing);
        Check("nothing red", !network.Issues.Any(i => i.Severity == Severity.Invalid));
        network.Undo();
        Check("one undo removes the grid", network.Graph.EdgeCount, before);

        var rules = Testbed.Profile!.ToRules();
        var even = GridLayout.From(new(0, 0), new(230, 0), new(0, 150), 3, 2, GridFit.Even, rules)!;
        Check("even block: 230/3 − 12 m along (×10)", (int)MathF.Round(even.BlockSize(0, 0).Along * 10), 647);
        Check("even block is a part lot", !even.Whole(even.BlockSize(0, 0).Along));
        var steps = GridLayout.From(new(0, 0), new(230, 0), new(0, 150), 3, 2, GridFit.LotSteps, rules)!;
        Check("lot steps: 8 lots each, far road at 228 m", (int)MathF.Round(steps.Width), 228);
        Check("lot steps: whole lots", steps.Whole(steps.BlockSize(1, 1).Along) && steps.Whole(steps.BlockSize(1, 1).Across));
        var pinned = GridLayout.From(new(0, 0), new(230, 0), new(0, 150), 3, 2, GridFit.LotSteps, rules, pinAlong: true, pinAcross: true)!;
        Check("lot steps, sides clicked onto built: they stay on the clicks", (int)MathF.Round(pinned.Width) == 230 && (int)MathF.Round(pinned.Depth) == 150);
        var many = GridLayout.From(new(0, 0), new(230, 0), new(0, 150), 40, 2, GridFit.Even, rules)!;
        Check("40 blocks asked on 230 m: the most that keep 2 lots (8)", many.Cols, 8);
        Check("none under 2 lots", !many.TooSmall);
        var tiny = GridLayout.From(new(0, 0), new(20, 0), new(0, 150), 3, 2, GridFit.Even, rules)!;
        Check("a 20 m outline is one block, too small", tiny.Cols == 1 && tiny.TooSmall);

        // Across a built street (between the first two columns): it joins each row it crosses.
        DrawTool.AddBuiltForTest(Testbed.Profile, new Alignment(new[] { new Pi(new NumVector2(4038, 3900)), new Pi(new NumVector2(4038, 4300)) }));
        Click(4000, 4000);
        Click(4000 + 230, 4000);
        Click(4100, 4000 + 150);
        Check("grid over a street: joined at each row", Enumerable.Range(0, 3).Count(r => Arms(4038, 4000 + r * 75f) == 4), 3);
        network.Undo();
        network.Undo();

        // On a built street: the first edge reuses it, the columns meet it in Ts.
        before = network.Graph.EdgeCount;
        DrawTool.AddBuiltForTest(Testbed.Profile, new Alignment(new[] { new Pi(new NumVector2(3900, 5000)), new Pi(new NumVector2(4400, 5000)) }));
        Click(4000, 5000);
        Click(4230, 5000);
        Click(4100, 5150);
        Check("on a street: no overlap, nothing red", !network.Issues.Any(i => i.Severity == Severity.Invalid));
        Check("on a street: 4 Ts along it", Enumerable.Range(0, 4).Count(c => Arms(4000 + c * 230f / 3, 5000) == 3), 4);
        Check("on a street: 5 + 5 + 7 edges", network.Graph.EdgeCount, before + 17);
        network.Undo();
        Check("one undo keeps the street", network.Graph.EdgeCount, before + 1);
        network.Undo();

        // On a built avenue (24 m): reused, and the blocks beside it measured from its kerb.
        Use("avenue");
        DrawTool.AddBuiltForTest(Testbed.Profile!, new Alignment(new[] { new Pi(new NumVector2(3900, 5600)), new Pi(new NumVector2(4400, 5600)) }));
        Use("street");
        Click(4000, 5600);
        Click(4230, 5600);
        Click(4100, 5750);
        Check("on an avenue: nothing red", !network.Issues.Any(i => i.Severity == Severity.Invalid));
        Check("on an avenue: equal clear rows, the middle road at 78 m", Arms(4000, 5678), 3);
        network.Undo();
        network.Undo();
        Testbed.SetGridBlocks(GridLayout.DefaultCols, GridLayout.DefaultRows);
    }

    private static GraphEdge EdgeThrough(SplineNetwork network, NumVector2 p) =>
        network.Graph.Edges.OrderBy(e => NumVector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p))
            .ThenBy(e => e.Alignment.Pis.Any(q => q.Position == p) ? 0 : 1).First();

    private void Use(string id) => Testbed!.SelectProfile(Testbed.Profiles.First(p => p.Id == id));

    private void Click(float x, float z)
    {
        DrawTool!.ForcedPlanCursor = new NumVector2(x, z);
        DrawTool.PlaceForTest(hard: false);
    }

    private string SnapClick(float x, float z)
    {
        DrawTool!.ForcedPlanCursor = new NumVector2(x, z);
        return DrawTool.SnapClickForTest();
    }

    private void Check(string name, string got, string want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: \"{got}\" (want \"{want}\") {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
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
}
