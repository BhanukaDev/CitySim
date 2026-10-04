using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines;
using CitySim.Splines.Godot;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    private const float HillX = 600, DipX = 800, RoadZ = 500, BumpX = 900, HillHeight = 14, HillRadius = 45,
        SteepX = 300, SteepZ = 800;

    /// <summary>
    /// <c>--demo-shape</c>: raises a hill and digs a dip on flat ground, builds a two-lane road over both, and checks that
    /// the road is level across (ground at both edges at its height), never steeper than its max grade, cut into the hill
    /// and on an embankment over the dip with banks at its side slope, red where the cut is deeper than its max, and that far
    /// ground is untouched. Then one undo
    /// puts the ground back, redo shapes it again, a T on the hillside gets a level junction at the road's height, and a terrain edit over the road (as a sculpt tool would make) leaves
    /// the road where it was and the ground is shaped back round it. Last, a road straight up a steep hill keeps its nodes at
    /// the ground where they were put and is red for its grade. Prints "Demo shape: all ok".
    /// </summary>
    private async void RunShape()
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("two_lane") is not { } profile || Network?.Terrain is not { Map: not null } terrain)
        {
            GD.PrintErr("Demo shape: no two_lane road or terrain");
            return;
        }
        var rules = profile.ToRules();
        float H(float x, float z) => terrain.GetHeightAtMap(x, z);
        float baseH = H(100, 100);

        Sculpt(terrain, (x, z) => HillHeight * Bell(x - HillX, z - RoadZ, HillRadius) - 9f * Bell(x - DipX, z - RoadZ, 50f));
        await Frames(2);
        float hillTop = H(HillX, RoadZ), dipBottom = H(DipX, RoadZ), farBefore = H(HillX, RoadZ + 160);

        Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(400, RoadZ)), new Pi(new NumVector2(1000, RoadZ))]), rules));
        await Frames(2);
        var e = Network.Graph.Edges.Single();
        var line = e.Heights!;
        float half = rules.Width / 2;
        GD.Print($"Demo shape: hill {hillTop - baseH:0.0} m, dip {dipBottom - baseH:0.0} m; road at hill {line.At(HillX - 400) - baseH:0.00} m, " +
                 $"at dip {line.At(DipX - 400) - baseH:0.00} m, steepest {line.Steepest().Grade * 100:0.0} %, cut {line.DeepestCut.Depth:0.0} m at {400 + line.DeepestCut.S:0}, fill {line.HighestFill.Depth:0.0} m at {400 + line.HighestFill.S:0}");

        // Level across: the ground at both edges of the road is the road's height.
        float worstRoll = 0;
        for (float s = 0; s <= e.Alignment.Length; s += 5)
        {
            float h = line.At(s), x = 400 + s;
            worstRoll = MathF.Max(worstRoll, MathF.Max(MathF.Abs(H(x, RoadZ + half) - h), MathF.Abs(H(x, RoadZ - half) - h)));
        }
        if (worstRoll > 0.1f) problems.Add($"ground at the road's edges is up to {worstRoll:0.00} m off its height");
        if (line.Steepest().Grade > rules.MaxGrade!.Value + 1e-3f) problems.Add($"steepest {line.Steepest().Grade * 100:0.0} %, max {rules.MaxGrade * 100:0} %");
        if (line.At(HillX - 400) > hillTop - 1) problems.Add("no cut through the hill");
        if (line.At(DipX - 400) < dipBottom + 1) problems.Add("no embankment over the dip");
        // The bank beside the cut rises at the side slope until it meets the hill.
        float apron = half + terrain.Map.CellSize, d = 6, road = line.At(HillX - 400);
        float natural = baseH + HillHeight * Bell(0, apron + d, HillRadius);
        float bank = H(HillX, RoadZ + apron + d) - road, wantBank = MathF.Min(d / rules.CutSlope, natural - road);
        if (MathF.Abs(bank - wantBank) > 0.6f) problems.Add($"cut bank {bank:0.00} m at {d} m out, want {wantBank:0.00}");
        if (MathF.Abs(H(HillX, RoadZ + 160) - farBefore) > 1e-3f) problems.Add("ground far from the road changed");
        if (Network.Issues.Any(i => i.Code == "grade")) problems.Add("grade issue on a road within its max grade");
        // Red exactly where the cut or fill is deeper than the road allows (this hill is steep enough for both).
        foreach (var (code, depth, max) in new[] { ("cut", line.DeepestCut.Depth, rules.MaxCut!.Value), ("fill", line.HighestFill.Depth, rules.MaxFill!.Value) })
        {
            bool red = Network.Issues.Any(i => i.Code == code && i.Severity == Severity.Invalid);
            if (red != depth > max + 0.05f) problems.Add($"{code} {depth:0.0} m, max {max} m, red {red}");
        }

        float cutDepth = hillTop - H(HillX, RoadZ);
        Network.Undo();
        await Frames(2);
        if (MathF.Abs(H(HillX, RoadZ) - hillTop) > 1e-3f) problems.Add("undo didn't put the hill back");
        Network.Redo();
        await Frames(2);
        if (MathF.Abs(hillTop - H(HillX, RoadZ) - cutDepth) > 1e-3f) problems.Add("redo didn't cut the hill again");

        // A T on the hillside: the junction is a level plate at its node's height, and the old road keeps its height.
        float roadAtT = line.At(640 - 400);
        Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(640, RoadZ)), new Pi(new NumVector2(640, RoadZ - 110))]), rules));
        await Frames(2);
        if (Network.Graph.NodeAt(new NumVector2(640, RoadZ)) is not { } tNode || Network.Graph.Node(tNode).Height is not { } tH)
            problems.Add("no junction at the T");
        else
        {
            if (MathF.Abs(tH - roadAtT) > 0.05f) problems.Add($"the T's junction is {tH - roadAtT:0.00} m off the road it joins");
            foreach (var (dx, dz) in new[] { (6f, 0f), (-6f, 0f), (0f, -6f), (4f, 4f) })
                if (MathF.Abs(H(640 + dx, RoadZ + dz) - tH) > 0.1f) problems.Add($"junction ground not level at ({dx}, {dz})");
        }

        // Someone sculpts a bump over the road: the road stays, the ground round it is shaped back.
        var bumped = Network.Graph.Edges.Single(x => x.Alignment.Curve.Sample(x.Alignment.Length).Position.X > BumpX);
        var before = bumped.Heights;
        float sBump = BumpX - bumped.Alignment.Curve.Sample(0).Position.X;
        float roadAtBump = before!.At(sBump);
        Sculpt(terrain, (x, z) => 6f * Bell(x - BumpX, z - RoadZ, 40f));
        await Frames(4);
        if (!ReferenceEquals(Network.Graph.Edge(bumped.Id).Heights, before)) problems.Add("the road moved with the ground");
        if (MathF.Abs(H(BumpX, RoadZ) - roadAtBump) > 0.05f) problems.Add($"ground under the road not shaped back ({H(BumpX, RoadZ) - roadAtBump:0.00} m)");
        if (H(BumpX, RoadZ + 50) - baseH < 1f) problems.Add("the bump away from the road is gone");

        // Straight up a steep hill: the nodes stay where they were put (at the ground) and the road is red for its grade.
        Sculpt(terrain, (x, z) => 20f * Bell(x - SteepX, z - SteepZ, 30f));
        await Frames(4);
        var trial = Network.Graph.Clone();
        NumVector2 top = new(SteepX, SteepZ), foot = new(SteepX, SteepZ + 40);
        trial.AddSpline(new Alignment([new Pi(top), new Pi(foot)]), rules);
        Network.Conform(trial);
        foreach (var at in new[] { top, foot })
            if (trial.NodeAt(at) is not { } id || trial.Node(id).Height is not { } nh || MathF.Abs(nh - H(at.X, at.Y)) > 0.01f)
                problems.Add($"node at {at} not at the ground under it");
        if (!Validation.Check(trial).Any(i => i is { Code: "grade", Severity: Severity.Invalid })) problems.Add("steep road not red for its grade");

        foreach (var p in problems) GD.PrintErr($"Demo shape: {p}");
        GD.Print(problems.Count == 0 ? "Demo shape: all ok" : $"Demo shape: {problems.Count} problem(s)");
    }

    /// <summary><c>--demo-slope</c> (with <c>--ui=open:roads,pick:two_lane</c>): raises a steep hill and leaves a two-leg
    /// draw open up it (a gentle leg, then a steep one to the top), so a screenshot shows each leg's slope and the red
    /// grade. Prints the issues.</summary>
    private async void RunSlope()
    {
        if (Network?.Terrain is not { Map: not null } terrain || GetTree().Root.FindChild("SplineDrawTool", true, false) is not SplineDrawTool draw)
        {
            GD.PrintErr("Demo slope: no terrain or draw tool");
            return;
        }
        Sculpt(terrain, (x, z) => 30f * Bell(x - HillX, z - RoadZ, 60f));
        await Frames(4);
        draw.ForcedPlanCursor = new NumVector2(HillX - 260, RoadZ);
        draw.PlaceForTest(hard: false);
        draw.ForcedPlanCursor = new NumVector2(HillX - 140, RoadZ);
        draw.PlaceForTest(hard: false);
        draw.ForcedPlanCursor = new NumVector2(HillX, RoadZ + 10);
        await Frames(4);
        foreach (var i in Network.Issues) GD.Print($"Demo slope: built issue {i.Message}");
        GD.Print("Demo slope: drawing");
    }

    private static float Bell(float dx, float dz, float r) => MathF.Exp(-(dx * dx + dz * dz) / (r * r));

    /// <summary>Adds <paramref name="delta"/> (metres, by map position) to the ground in one terrain edit.</summary>
    private static void Sculpt(Terrain terrain, Func<float, float, float> delta)
    {
        using var edit = terrain.BeginEdit();
        var hm = edit.Heights;
        var rect = new VertexRect(0, 0, hm.Width - 1, hm.Depth - 1);
        edit.Touch(rect);
        for (int z = 0; z < hm.Depth; z++)
            for (int x = 0; x < hm.Width; x++)
                hm[x, z] += delta(x * hm.CellSize, z * hm.CellSize);
    }

    private async System.Threading.Tasks.Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
