using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines;

/// <summary>TEMPORARY, not part of S3: builds one edge, then hovers near its extension guide with an empty
/// session, to screenshot-verify the pre-click snap-tag/marker fix. Delete before committing.</summary>
public partial class TempSnapVisualCheck : Node
{
    [Export] public CitySim.Splines.Godot.SplineDrawTool? DrawTool { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--temp-snap-visual")) Callable.From(Run).CallDeferred();
    }

    private void Run()
    {
        if (DrawTool is null || Testbed is null) { GD.PrintErr("TempSnapVisualCheck: missing refs"); return; }
        var profile = Testbed.Profiles.FirstOrDefault(p => p.Id == "street") ?? Testbed.Profiles[0];
        Testbed.SelectProfile(profile);

        DrawTool.ForcedPlanCursor = new NumVector2(0, 0);
        DrawTool.PlaceForTest(hard: false);
        DrawTool.ForcedPlanCursor = new NumVector2(100, 0);
        DrawTool.PlaceForTest(hard: false);
        DrawTool.FinishForTest();

        // Hover near the extension of that edge's end (100,0), off to the side, with no PI placed this session.
        DrawTool.ForcedPlanCursor = new NumVector2(130, 2);
        GD.Print("TempSnapVisualCheck: built one edge, hovering at (130, 2)");
    }
}
