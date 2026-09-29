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
/// Prints <c>Demo draw: all ok</c> or the failures. Composes with <c>--screenshot=</c>/<c>--cam=</c> as usual.
/// </summary>
public partial class DrawDemo : Node
{
    [Export] public SplineDrawTool? DrawTool { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }

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

    private void Check(string name, int got, int want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: {got} (want {want}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }
}
