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
        Testbed.SetGridLots(GridLayout.DefaultLots, GridLayout.DefaultLots);
        var rules = Testbed.Profile!.ToRules();
        float pitch = GridLayout.Pitch(GridLayout.DefaultLots, rules); // 8 lots + one street = 76 m
        Check("pitch (m)", (int)pitch, 76);
        int before = network.Graph.EdgeCount, nodesBefore = network.Graph.Nodes.Count();

        Click(4000, 4000);
        Click(4000 + 230, 4000);          // 230 m rounds to 3 blocks
        Click(4100, 4000 + 150);          // 150 m to this side rounds to 2
        Check("3 × 2 grid: 9 + 8 edges", network.Graph.EdgeCount, before + 17);
        Check("12 nodes", network.Graph.Nodes.Count() - nodesBefore, 12);
        var arms = Enumerable.Range(0, 4).SelectMany(c => Enumerable.Range(0, 3).Select(r => new NumVector2(4000 + c * pitch, 4000 + r * pitch)))
            .Select(p => network.Graph.NodeAt(p) is { } n ? network.Graph.Node(n).Edges.Count : 0).ToList();
        Check("4 square corners", arms.Count(n => n == 2), 4);
        Check("6 T-junctions", arms.Count(n => n == 3), 6);
        Check("2 four-ways", arms.Count(n => n == 4), 2);
        Check("not drawing after the third click", !DrawTool!.IsDrawing);
        Check("nothing red", !network.Issues.Any(i => i.Severity == Severity.Invalid));
        network.Undo();
        Check("one undo removes the grid", network.Graph.EdgeCount, before);

        // Across a built street (between the first two columns): it joins each row it crosses.
        DrawTool.AddBuiltForTest(Testbed.Profile, new Alignment(new[] { new Pi(new NumVector2(4038, 3900)), new Pi(new NumVector2(4038, 4300)) }));
        Click(4000, 4000);
        Click(4000 + 230, 4000);
        Click(4100, 4000 + 150);
        Check("grid over a street: joined at each row",
            Enumerable.Range(0, 3).Count(r => network.Graph.NodeAt(new NumVector2(4038, 4000 + r * pitch)) is { } n && network.Graph.Node(n).Edges.Count == 4), 3);
        network.Undo();
        network.Undo();

        // Wider blocks along, the same across.
        Testbed.SetGridLots(12, 8);
        before = network.Graph.EdgeCount;
        Click(4000, 4600);
        Click(4000 + 210, 4600);          // one 108 m block → 2 blocks
        Click(4000, 4600 + 76);
        Check("12 × 8 lots: 2 × 1 grid", network.Graph.EdgeCount, before + 7);
        Check("column spacing 12 lots + a street", network.Graph.NodeAt(new NumVector2(4000 + 108, 4600)) is not null);
        network.Undo();
        Testbed.SetGridLots(GridLayout.DefaultLots, GridLayout.DefaultLots);
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
