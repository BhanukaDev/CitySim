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
        DrawTool.FinishForTest();
        Check("square rail branch refused", network.Graph.EdgeCount, start + 1);
        Testbed.SetAnarchy(true);
        DrawTool.FinishForTest();
        Testbed.SetAnarchy(false);
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
    }

    private void Click(float x, float z)
    {
        DrawTool!.ForcedPlanCursor = new NumVector2(x, z);
        DrawTool.PlaceForTest(hard: false);
    }

    private void Check(string name, int got, int want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: {got} (want {want}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }
}
