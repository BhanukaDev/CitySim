using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-draw</c> (S2): for each test profile, drives <see cref="SplineDrawTool"/>'s public API directly
/// (<see cref="SplineDrawTool.ForcedPlanCursor"/> + <c>PlaceForTest</c>/<c>FinishForTest</c>, no simulated
/// InputEvents) to draw the same 90° corner as <c>--demo-geometry</c>'s <c>Corner()</c> case, then checks it built.
/// S4 adds the tool's graph flows: a square rail branch refused, then built with Anarchy, graph undo/redo, and a
/// deleted branch merging the road back. Prints <c>Demo draw: all ok</c> or the failures. Composes with
/// <c>--screenshot=</c>/<c>--cam=</c> as usual.
/// </summary>
public partial class DrawDemo : Node
{
    [Export] public SplineDrawTool? DrawTool { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-draw")) Callable.From(Run).CallDeferred();
    }

    private void Run()
    {
        if (DrawTool is null || Testbed is null)
        {
            GD.PrintErr("Demo draw: FAILED (no DrawTool/Testbed)");
            return;
        }

        int i = 0;
        foreach (var profile in Testbed.Profiles)
        {
            Testbed.SelectProfile(profile);
            Draw(baseX: i * 250f);
            Check($"{profile.Id} built", DrawTool.BuiltCount, i + 1);
            i++;
        }
        if (Network is not null) GraphFlows(Network);
        DrawTool.ForcedPlanCursor = null;

        foreach (var f in _failures) GD.PrintErr($"Demo draw: FAILED {f}");
        GD.Print($"Demo draw: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    /// <summary>(baseX,0) → (baseX+100,0) → (baseX+100,100): a right turn at the profile's default radius.</summary>
    private void Draw(float baseX)
    {
        DrawTool!.ForcedPlanCursor = new NumVector2(baseX, 0);
        DrawTool.PlaceForTest(hard: false);
        DrawTool.ForcedPlanCursor = new NumVector2(baseX + 100, 0);
        DrawTool.PlaceForTest(hard: false);
        DrawTool.ForcedPlanCursor = new NumVector2(baseX + 100, 100);
        DrawTool.PlaceForTest(hard: false);
        DrawTool.FinishForTest();
    }

    private void GraphFlows(SplineNetwork network)
    {
        var rail = Testbed!.Profiles.First(p => p.Id == "rail");
        var street = Testbed.Profiles.First(p => p.Id == "street");
        int start = network.Graph.EdgeCount;

        Testbed.SelectProfile(rail);
        DrawTool!.AddBuiltForTest(rail, new Alignment(new[] { new Pi(new NumVector2(0, 1000)), new Pi(new NumVector2(800, 1000)) }));
        Check("rail line", network.Graph.EdgeCount, start + 1);
        Click(300, 1000);
        Click(300, 900);
        Check("square rail branch refused", network.Graph.EdgeCount, start + 1);
        Check("refused leg: still drawing from the start", DrawTool.IsDrawing ? 1 : 0, 1);
        Testbed.SetAnarchy(true);
        Click(300, 900);
        Testbed.SetAnarchy(false);
        DrawTool.FinishForTest();
        Check("built with Anarchy (line split + branch)", network.Graph.EdgeCount, start + 3);
        Check("and it stays red", network.Issues.Count(i => i.Code == "turnout"), 1);
        network.Undo();
        Check("undo", network.Graph.EdgeCount, start + 1);
        network.Redo();
        Check("redo", network.Graph.EdgeCount, start + 3);
        network.Undo();

        Testbed.SelectProfile(street);
        DrawTool.AddBuiltForTest(street, new Alignment(new[] { new Pi(new NumVector2(0, 1400)), new Pi(new NumVector2(400, 1400)) }));
        Click(200, 1400);
        Click(200, 1560);
        DrawTool.FinishForTest();
        Check("T built", network.Graph.EdgeCount, start + 4);
        Check("T footprint", network.Footprints.Count, 1);
        var branch = network.Graph.Edges.First(e => e.Alignment.Curve.Sample(0).Position == new NumVector2(200, 1400) &&
                                                     e.Alignment.Pis[^1].Position == new NumVector2(200, 1560));
        network.Apply(g => g.RemoveEdge(branch.Id));
        Check("delete merges the road", network.Graph.EdgeCount, start + 2);
        Check("no footprint left", network.Footprints.Count, 0);

        // Continue from the road's dead end: the start is the live corner (Shift+wheel sizes it), one road results.
        Click(400, 1400);
        DrawTool.AdjustRadiusForTest(2f);
        Click(400, 1600);
        DrawTool.FinishForTest();
        Check("continued: still one road", network.Graph.EdgeCount, start + 2);
        var road = network.Graph.Edges.First(e => e.Alignment.Pis.Any(p => p.Position == new NumVector2(400, 1400)));
        Check("continued: joint radius (m)", (int)MathF.Round(road.Alignment.EffectiveRadius(road.Alignment.Pis.FindIndex(p => p.Position == new NumVector2(400, 1400)))), 32);
        network.Undo();
        Check("continued: undo restores the old end", network.Graph.EdgeCount, start + 2);
        Check("continued: undo restores the node", network.Graph.NodeAt(new NumVector2(400, 1400)) is null ? 0 : 1, 1);

        // Shift+wheel can't grow the live corner past what fits: a 40 m end leg at 90° fits R 40 at most.
        Click(400, 1400);
        DrawTool.ForcedPlanCursor = new NumVector2(400, 1440);
        DrawTool.ForcedModifiers = DrawModifiers.Space; // no snapping: the headless catch distance is huge
        DrawTool._Process(0);
        DrawTool.ForcedModifiers = DrawModifiers.None;
        DrawTool.AdjustRadiusForTest(10f);
        Click(400, 1440);
        DrawTool.FinishForTest();
        road = network.Graph.Edges.First(e => e.Alignment.Pis.Any(p => p.Position == new NumVector2(400, 1400)));
        int joint = road.Alignment.Pis.FindIndex(p => p.Position == new NumVector2(400, 1400));
        Check("wheel capped at the fit (m)", (int)MathF.Round(road.Alignment.Pis[joint].Radius), 40);
        Check("capped corner isn't clamped", road.Alignment.IsClamped(joint) ? 1 : 0, 0);
        network.Undo();

        // A corner squeezed below MinRadius (a 5 m leg fits R 5 < 10) is refused, even though it was only clamped.
        Click(400, 1400);
        Click(400, 1405);
        Check("clamped below min: refused", network.Graph.NodeAt(new NumVector2(400, 1405)) is null ? 0 : 1, 0);
        Testbed.SetAnarchy(true);
        Click(400, 1405);
        Testbed.SetAnarchy(false);
        DrawTool.FinishForTest();
        Check("clamped below min: built with Anarchy", network.Graph.NodeAt(new NumVector2(400, 1405)) is null ? 0 : 1, 1);
        Check("and it stays red", network.Issues.Count(i => i.Code == "radius-min"), 1);
        network.Undo();

        // Ending on a dead end: that click places the point and finishes (no double-click).
        DrawTool.AddBuiltForTest(street, new Alignment(new[] { new Pi(new NumVector2(600, 1300)), new Pi(new NumVector2(600, 1100)) }));
        Click(400, 1400);
        Click(600, 1400);
        Click(600, 1300);
        Check("click on a dead end finishes", DrawTool.IsDrawing ? 1 : 0, 0);
        Check("ended on a dead end: the two roads are one", network.Graph.EdgeCount, start + 2);
        network.Undo();
        network.Undo();
        network.Undo();

        // Each click builds its leg; Ctrl+Z mid-draw takes the last one back and the chain goes on from the point before.
        int before = network.Graph.EdgeCount;
        Click(1000, 1400);
        Click(1100, 1400);
        Check("click builds the leg", network.Graph.EdgeCount, before + 1);
        Click(1100, 1500);
        Check("next leg continues the same road", network.Graph.EdgeCount, before + 1);
        Check("the corner is on it", network.Graph.Edges.Any(e => e.Alignment.Pis.Count == 3 && e.Alignment.Pis[1].Position == new NumVector2(1100, 1400)));
        DrawTool.UndoForTest();
        Check("Ctrl+Z mid-draw: back to one leg", network.Graph.Edges.Any(e => e.Alignment.Pis.Count == 2 && e.Alignment.Pis[1].Position == new NumVector2(1100, 1400)));
        Check("Ctrl+Z mid-draw: still drawing", DrawTool.IsDrawing ? 1 : 0, 1);
        Click(1200, 1500);
        DrawTool.FinishForTest();
        Check("double-click ends the chain", DrawTool.IsDrawing ? 1 : 0, 0);
        Check("one road", network.Graph.EdgeCount, before + 1);

        // A loop back across the chain's own first leg builds, with a 4-way there.
        before = network.Graph.EdgeCount;
        Click(1000, 1800);
        Click(1200, 1800);
        Click(1200, 1700);
        Click(1100, 1700);
        Click(1100, 1850);
        DrawTool.FinishForTest();
        Check("loop across its own road: 4-way", network.Graph.NodeAt(new NumVector2(1100, 1800)) is { } x ? Junctions.Label(network.Graph, x) ?? "" : "", "4-way · 90°");
        Check("loop across its own road: three edges", network.Graph.EdgeCount, before + 3);
    }

    private void Click(float x, float z)
    {
        DrawTool!.ForcedPlanCursor = new NumVector2(x, z);
        DrawTool.PlaceForTest(hard: false);
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
