using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    [Export] public LaneLinkTool? LaneLinkTool { get; set; }

    /// <summary>
    /// <c>--demo-lane-links[=links|pick|add]</c>: a street with a 4-way at 700 and a T at 500, then the Lane Links tool's
    /// clicks: select the 4-way, pick its left turn from the west and remove it (RMB), undo / redo, link it again by dragging
    /// between the lane dots (both ways, and a drag let go on nothing), a U-turn, undo back to auto, a split of an arm that keeps the links; at the T the right turn from the west is
    /// removed and the wear must follow. Prints "Demo lane links: all ok". Leaves the 4-way selected without its left
    /// turn from the west, for screenshots; <c>pick</c> also picks its right turn from the west with the mouse on it,
    /// <c>add</c> leaves a drag from the west lane coming in held over the lane it lost.
    /// </summary>
    private void RunLaneLinks(string shot)
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("two_lane") is not { } profile || Network is null || Host.Visual is not { } visual
            || LaneLinkTool is not { } tool || Host.Hud is not { } hud)
        {
            GD.PrintErr("Demo lane links: no two_lane road, network, visual, tool or HUD");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(NumVector2 a, NumVector2 b) => Network.Apply(g => g.AddSpline(new Alignment([new Pi(a), new Pi(b)]), rules));
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }

        Road(V(400, 500), V(900, 500));     // the street
        Road(V(700, 350), V(700, 650));     // a 4-way at 700
        Road(V(500, 500), V(500, 650));     // a T at 500

        hud.OpenById("roads");
        hud.Tray.OpenTab("services");
        hud.Tray.Pick(hud.Library.Item("lane_links"));
        Expect("the Lane Links card didn't pick the tool", hud.PickedRoadTool?.Tool == RoadTool.LaneLinksTool);

        int NodeAt(float x, float z) => Network.Graph.NodeAt(V(x, z)) ?? -1;
        int cross = NodeAt(700, 500), tee = NodeAt(500, 500);
        // The arm of the selected junction pointing west (outward −x), by its lane ends.
        bool West(LaneLinkTool.LaneEnd l)
        {
            var e = Network.Graph.Edge(l.Edge);
            var other = Network.Graph.Node(l.AtStart ? e.End : e.Start).Position;
            var here = Network.Graph.Node(l.AtStart ? e.Start : e.End).Position;
            return other.X < here.X - 1;
        }
        int Wear() => visual.Counts.GetValueOrDefault(Roads.Geometry.SurfaceKind.Wear);

        Expect("LMB on the 4-way should hit it", tool.ClickAt(V(700, 500), left: true));
        Expect("LMB on the 4-way should select it", tool.Selected == cross);
        var links = tool.Links();
        GD.Print($"Demo lane links: 4-way auto, {links.Count} links " +
                 $"({links.Count(l => l.Move == Move.Straight)} straight, {links.Count(l => l.Move == Move.Right)} right, {links.Count(l => l.Move == Move.Left)} left)");
        Expect($"a two-lane 4-way should have 12 auto links, has {links.Count}", links.Count == 12);
        Expect("4 of each move", links.Count(l => l.Move == Move.Straight) == 4 && links.Count(l => l.Move == Move.Right) == 4);

        // Pick the left turn from the west, then RMB removes it.
        var left = links.First(l => l.Move == Move.Left && West(l.Key.From)).Key;
        int wear0 = Wear();
        Expect("LMB on a link should hit it", tool.LinkPoint(left) is { } lp && tool.ClickAt(lp, left: true));
        Expect("LMB on a link should pick it", tool.Picked == left);
        tool.ClickAt(V(700, 560), left: false);
        links = tool.Links();
        Expect("RMB should remove the picked link", links.Count == 11 && links.All(l => l.Key != left) && tool.Picked is null);
        Expect("the wear should lose the removed turn", Wear() < wear0);
        GD.Print($"  removed the left turn from the west: {links.Count} links, wear {wear0} → {Wear()} triangles");

        Network.Undo();
        tool.ClickAt(V(0, 0), left: true); // a click on nothing: drops nothing picked, clears the selection
        Expect("a click on nothing should clear the selection", tool.Selected is null);
        tool.Select(cross);
        Expect("undo should bring the link back", tool.Links().Count == 12);
        Network.Redo();
        tool.Select(cross);
        Expect("redo should take it away again", tool.Links().Count == 11);

        // Link it again by dragging between the dots. Let go on nothing: no change.
        var lin = tool.LanePoint(left.From, false)!.Value;
        var lout = tool.LanePoint(left.To, true)!.Value;
        Expect("a drag from a lane's dot should start", tool.DragAt(lin, V(700, 500)));
        Expect("a drag let go on nothing should change nothing", tool.Links().Count == 11 && tool.DragFrom is null);
        Expect("a drag from a lane in to a lane in should change nothing", tool.DragAt(lin, tool.LanePoint(left.To, false)!.Value) && tool.Links().Count == 11);
        Expect("a drag from a lane going out back to a lane coming in should link them", tool.DragAt(lout, lin));
        links = tool.Links();
        Expect("the link should be back", links.Count == 12 && links.Any(l => l.Key == left));
        Network.Undo();
        tool.Select(cross);
        Expect("a drag from a lane coming in to a lane going out should link them", tool.DragAt(lin, lout) && tool.Links().Count == 12);

        // A U-turn, drawn by hand (never automatic).
        var back = new LaneLinkTool.LaneEnd(left.From.Edge, left.From.AtStart, 0);
        tool.Link(new LaneLinkTool.LinkKey(left.From, back));
        links = tool.Links();
        Expect("a U-turn should be added", links.Count == 13 && links.Any(l => l.Move == Move.UTurn));

        // Undo back past every edit: the road's rule again, nothing stored.
        for (int i = 0; i < 3; i++) Network.Undo();
        tool.Select(cross);
        links = tool.Links();
        Expect("undoing every edit should put the road's rule back", links.Count == 12 && Network.Graph.Edges.All(e => e.DataStart is null && e.DataEnd is null));

        // Remove the left turn again; splitting the east arm away from the junction keeps it removed.
        tool.ClickAt(tool.LinkPoint(left)!.Value, left: true);
        tool.RemovePicked();
        Expect("Del should remove the picked link", tool.Links().Count == 11);
        var east = Network.Graph.Arms(cross).First(a => a.Direction.X > 0.9f);
        Network.Apply(g => g.SplitEdge(east.EdgeId, east.AtStart ? 60 : g.Edge(east.EdgeId).Alignment.Length - 60));
        tool.Select(cross);
        links = tool.Links();
        Expect("a split of an arm should keep the junction's links", links.Count == 11 && links.All(l => l.Key.From != left.From || l.Move != Move.Left));

        // The T: no right turn from the west; the wear (and chevrons) follow.
        tool.Select(tee);
        var tLinks = tool.Links();
        Expect($"a two-lane T should have 6 auto links, has {tLinks.Count}", tLinks.Count == 6);
        var right = tLinks.First(l => l.Move == Move.Right && West(l.Key.From)).Key;
        int hatch0 = visual.Hatches;
        wear0 = Wear();
        tool.ClickAt(tool.LinkPoint(right)!.Value, left: true);
        tool.ClickAt(V(0, 0), left: false);
        GD.Print($"  T without the right turn from the west: {tool.Links().Count} links, wear {wear0} → {Wear()}, hatched islands {hatch0} → {visual.Hatches}");
        Expect("the T should lose its right turn", tool.Links().Count == 5 && Wear() < wear0);

        // Left for screenshots.
        tool.Select(cross);
        if (shot == "pick")
        {
            var pick = tool.Links().First(l => l.Move == Move.Right && West(l.Key.From)).Key;
            tool.ClickAt(tool.LinkPoint(pick)!.Value, left: true);
            tool.ForcedPlanCursor = tool.LinkPoint(pick);
        }
        else if (shot == "add")
        {
            // Mid-drag from the west lane coming in, the mouse on the lane it lost.
            tool.StartDragAt(tool.LanePoint(left.From, false)!.Value);
            tool.ForcedPlanCursor = tool.LanePoint(left.To, true);
        }
        foreach (var p in problems) GD.PrintErr($"Demo lane links: {p}");
        GD.Print(problems.Count == 0 ? "Demo lane links: all ok" : $"Demo lane links: {problems.Count} problem(s)");
    }
}
