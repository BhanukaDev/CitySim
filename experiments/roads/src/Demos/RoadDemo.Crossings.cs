using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using CitySim.Splines;
using CitySim.UI;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    [Export] public CrossingTool? CrossingTool { get; set; }

    /// <summary>
    /// <c>--demo-crossings</c>: a street with T-junctions at different spacings and a 4-way, then the Crossings tool's
    /// clicks: a crossing along the road, an arm set to No, a dropped arm set to Yes, the crossing along the road taken
    /// away again, and undo; then a crossing along the road at x 700 is left for screenshots. Checks the crossings after each and prints "Demo crossings: all ok". Leaves the tool picked;
    /// <c>--crossing-cursor=x,z</c> puts its mouse there for a screenshot.
    /// </summary>
    private async void RunCrossings(NumVector2? cursor)
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("two_lane") is not { } profile || Network is null || Host.Visual is not { } visual
            || CrossingTool is not { } tool || Host.Hud is not { } hud)
        {
            GD.PrintErr("Demo crossings: no two_lane road, network, visual, tool or HUD");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(NumVector2 a, NumVector2 b) => Network.Apply(g => g.AddSpline(new Alignment([new Pi(a), new Pi(b)]), rules));

        Road(V(400, 500), V(900, 500));     // the street
        Road(V(500, 500), V(500, 650));     // T at 500
        Road(V(530, 500), V(530, 350));     // T at 530: 30 m on, room for one crossing between them
        Road(V(600, 500), V(600, 650));     // T at 600: its crossing toward 640 is too close to that one's
        Road(V(640, 500), V(640, 350));     // T at 640
        Road(V(760, 350), V(760, 650));     // 4-way at 760, well clear

        hud.OpenById("roads");
        hud.Tray.OpenTab("services");
        hud.Tray.Pick(hud.Library.Item("crossings"));
        if (hud.PickedRoadTool() is null) problems.Add("the Crossings card didn't pick the tool");

        var g = Network.Graph;
        int NodeAt(float x, float z) => g.NodeAt(V(x, z)) ?? -1;
        // The arm of the street at node x heading east (+1) or west (−1).
        (int Edge, bool AtStart) Arm(float x, int dir)
        {
            var a = g.Arms(NodeAt(x, 500)).First(a => a.Direction.X * dir > 0.9f);
            return (a.EdgeId, a.AtStart);
        }
        bool Zebra((int Edge, bool AtStart) arm) => visual.Marks.TryGetValue(arm, out var m) && m.Zebra;
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }
        void Dump(string when)
        {
            GD.Print($"  {when}: {g.EdgeCount} edges, {g.NodeCount} nodes, {visual.Marks.Values.Count(m => m.Zebra)} crossings");
            foreach (float x in new[] { 500f, 530f, 600f, 640f, 760f })
                if (NodeAt(x, 500) >= 0)
                    GD.Print($"    node {x}: west {(Zebra(Arm(x, -1)) ? "zebra" : "-")}, east {(Zebra(Arm(x, 1)) ? "zebra" : "-")}");
        }

        Dump("auto");
        Expect("auto: the 4-way's arms should all have crossings", g.Arms(NodeAt(760, 500)).All(a => Zebra((a.EdgeId, a.AtStart))));
        Expect("auto: only one crossing fits between the Ts at 500 and 530", Zebra(Arm(500, 1)) != Zebra(Arm(530, -1)));
        Expect("auto: the Ts at 600 and 640 are too close for both crossings between them", Zebra(Arm(600, 1)) != Zebra(Arm(640, -1)));
        Expect("auto: the T at 600 keeps its crossing toward 530 (far enough)", Zebra(Arm(600, -1)));
        var side500 = g.Arms(NodeAt(500, 500)).First(a => a.Direction.Y > 0.9f);
        Expect("auto: the side road of the T at 500 has a crossing", Zebra((side500.EdgeId, side500.AtStart)));

        // A crossing along the road between 640 and 760.
        int edges = g.EdgeCount, nodes = g.NodeCount;
        Expect("LMB along the road should place a crossing", tool.ClickAt(V(700, 500), add: true));
        g = Network.Graph;
        Dump("LMB at 700");
        Expect("the new crossing's node should be selected", tool.Selected is { } sn && g.Arms(sn).Count == 2);
        // Too close to that crossing (either side): refused, nothing changes, no issue left behind.
        foreach (float x in new[] { 706f, 690f })
        {
            int before = g.EdgeCount;
            tool.ClickAt(V(x, 500), add: true);
            g = Network.Graph;
            Expect($"a crossing at {x} (too close) should be refused", g.EdgeCount == before && Network.Issues.Count == 0);
        }
        Expect("a crossing along the road should split it with a node", g.EdgeCount == edges + 1 && g.NodeCount == nodes + 1);
        var mid = g.NodeAt(V(698.5f, 500));
        Expect("the new node should sit half a crossing before the mouse", mid is not null);
        if (mid is { } m)
        {
            var arms = g.Arms(m);
            Expect("the crossing along the road should have a zebra", arms.Count(a => Zebra((a.EdgeId, a.AtStart))) == 1);
            Expect("both sides should get a stop line", arms.All(a => visual.Marks.TryGetValue((a.EdgeId, a.AtStart), out var mk) && mk.StopAt is not null));
        }

        // RMB on the 4-way's west arm: no crossing there, for good.
        var west760 = Arm(760, -1);
        Expect("LMB on an unselected junction's arm should hit it", tool.ClickAt(ArmPoint(west760), add: true));
        Expect("LMB on an unselected junction should select it, not change it",
            tool.Selected == NodeAt(760, 500) && ModeOf(west760) == CrossingMode.Auto && Zebra(west760));
        Expect("RMB on an arm should hit it", tool.ClickAt(ArmPoint(west760), add: false));
        g = Network.Graph;
        Expect("RMB should set the arm to No", ModeOf(Arm(760, -1)) == CrossingMode.No && !Zebra(Arm(760, -1)));

        // LMB on the arm that lost out at 640: forced Yes wins, the automatic one at 600 goes.
        bool had600 = Zebra(Arm(600, 1));
        var lost = had600 ? Arm(640, -1) : Arm(600, 1);
        var other = had600 ? Arm(600, 1) : Arm(640, -1);
        tool.Select(g.Edge(lost.Edge) is var le && lost.AtStart ? le.Start : le.End);
        Expect("LMB on an arm should hit it", tool.ClickAt(ArmPoint(lost), add: true));
        g = Network.Graph;
        Dump("LMB on the dropped arm");
        Expect("a forced crossing should be drawn", Zebra(lost));
        Expect("the automatic crossing too close to a forced one should go", !Zebra(other));

        // RMB on the crossing along the road: it and its node go, the road is one edge again.
        if (mid is { } mm)
        {
            var arm = g.Arms(mm).First(a => Zebra((a.EdgeId, a.AtStart)));
            tool.Select(mm);
            Expect("RMB on the crossing along the road should hit it", tool.ClickAt(ArmPoint((arm.EdgeId, arm.AtStart)), add: false));
            g = Network.Graph;
            Expect("removing the crossing along the road should merge the road back", !g.HasNode(mm) && g.EdgeCount == edges);
        }

        // Undo steps back through all four clicks.
        for (int i = 0; i < 4; i++) Network.Undo();
        g = Network.Graph;
        Dump("undone");
        Expect("LMB on nothing should clear the selection", !tool.ClickAt(V(450, 800), add: true) && tool.Selected is null);
        Expect("undo should bring back the network as built", g.EdgeCount == edges && g.Edges.All(e => e.DataStart is null && e.DataEnd is null));
        for (int i = 0; i < 4; i++) Network.Redo();
        g = Network.Graph;
        Dump("redone");
        // One left along the road between 640 and 760 for screenshots.
        Expect("LMB along the road should place a crossing", tool.ClickAt(V(700, 500), add: true));

        await Slope(problems, rules, tool);
        tool.Select(g.NodeAt(V(698.5f, 500)));
        tool.ForcedPlanCursor = cursor;
        foreach (var p in problems) GD.PrintErr($"Demo crossings: {p}");
        GD.Print(problems.Count == 0 ? "Demo crossings: all ok" : $"Demo crossings: {problems.Count} problem(s)");

        CrossingMode ModeOf((int Edge, bool AtStart) a) => Crossings.ModeOf(g.Edge(a.Edge), a.AtStart);
        // The middle of an arm's crossing place.
        NumVector2 ArmPoint((int Edge, bool AtStart) a)
        {
            var e = g.Edge(a.Edge);
            var st = visual.SectionStyle;
            var fp = Network.Footprints;
            float u = Crossings.CutOf(e, a.AtStart, fp)
                + Crossings.ZebraFrom(Crossings.AtJunction(e, a.AtStart, fp), st) + st.CrossingWidth / 2;
            return e.Alignment.Curve.Sample(a.AtStart ? u : e.Alignment.Length - u).Position;
        }
    }

    /// <summary>A crossing placed along a road up a hill keeps the road's height line as it was (no kink at its node),
    /// and taking it away again joins the line back the same.</summary>
    private async System.Threading.Tasks.Task Slope(List<string> problems, ProfileRules rules, CrossingTool tool)
    {
        if (Network?.Terrain is not { Map: not null } terrain) { problems.Add("no terrain for the slope check"); return; }
        Sculpt(terrain, (x, z) => 18f * Bell(x - 620, z - 900, 120));
        await Frames(2);
        Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(450, 900)), new Pi(new NumVector2(800, 900))]), rules));
        await Frames(2);
        var road = Network.Graph.Edges.Single(e => e.Alignment.Pis[0].Position.Y > 850);
        var before = road.Heights!;
        float Line(float x)
        {
            var g = Network.Graph;
            var e = g.Edges.Where(e => e.Alignment.Pis[0].Position.Y > 850)
                .First(e => e.Alignment.Pis[0].Position.X <= x + 1e-3f && e.Alignment.Pis[^1].Position.X >= x - 1e-3f);
            return e.Heights!.At(x - e.Alignment.Pis[0].Position.X);
        }
        float Worst() => Enumerable.Range(0, 175).Select(i => 450 + i * 2f).Max(x => MathF.Abs(Line(x) - before.At(x - 450)));

        tool.Select(null);
        if (!tool.ClickAt(new NumVector2(560, 900), add: true)) problems.Add("slope: LMB along the road missed");
        await Frames(2);
        float off = Worst();
        GD.Print($"  slope: crossing at 560 on a {before.Steepest().Grade * 100:0.0} % road, line moved up to {off * 100:0.0} cm");
        if (off > 0.02f) problems.Add($"slope: the crossing's node bent the road's line by {off:0.00} m");
        if (tool.Selected is { } n)
        {
            var arm = Network.Graph.Arms(n).First(a => Crossings.ModeOf(Network.Graph.Edge(a.EdgeId), a.AtStart) == CrossingMode.Yes);
            var e = Network.Graph.Edge(arm.EdgeId);
            var at = e.Alignment.Curve.Sample(arm.AtStart ? 1.5f : e.Alignment.Length - 1.5f).Position;
            if (!tool.ClickAt(at, add: false)) problems.Add("slope: RMB on the crossing missed");
            await Frames(2);
            off = Worst();
            if (Network.Graph.Edges.Count(e => e.Alignment.Pis[0].Position.Y > 850) != 1) problems.Add("slope: removing the crossing didn't join the road");
            if (off > 0.02f) problems.Add($"slope: joining the road back moved its line by {off:0.00} m");
        }
        else problems.Add("slope: the new crossing wasn't selected");
    }
}
