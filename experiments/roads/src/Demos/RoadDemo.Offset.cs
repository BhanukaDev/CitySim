using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using CitySim.Splines;
using CitySim.Splines.Godot;
using CitySim.UI;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    /// <summary>
    /// <c>--demo-offset[=draw|replace|chevron]</c>: roads moved sideways (<see cref="GraphEdge.Offset"/>). Along z = 500, heading
    /// east: a four-lane road, a two-lane road with its south side lined up with it (offset −4), the same two-lane road
    /// moved 6 m north (+2), and the four-lane road again. Checks each node is a transition, the lined-up side runs
    /// straight through, and the lane links merge and branch (every lane in goes somewhere, every lane out is fed), with no
    /// chevrons until a lane loses its link (<c>=chevron</c> leaves that for a screenshot). Along
    /// z = 650: two-lane, four-lane, two-lane, then the four-lane replaced by the two-lane in line: one road again. At
    /// z = 950 a two-lane road widens to four lanes at a T, and at z = 1250 the same with the four-lane moved over so the
    /// T's side lines up (the other side tapers in from the two-lane's, no step).
    /// With a road picked (<c>--ui=open:roads,pick:two_lane</c>): a draw from a four-lane dead end at z = 800 with the
    /// cursor 3.6 m south of its line runs parallel, sides lined up, and Replace on that four-lane road moves it to where the
    /// cursor is. <c>=draw</c> / <c>=replace</c> leave that preview open for a screenshot instead. Prints
    /// "Demo offset: all ok".
    /// </summary>
    private async void RunOffset(string shot)
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("four_lane") is not { } fourP || Host.ProfileFor("two_lane") is not { } twoP
            || Network is null || Host.Visual is not { } visual)
        {
            GD.PrintErr("Demo offset: no four_lane or two_lane road, network or road visual");
            return;
        }
        ProfileRules four = fourP.ToRules(), two = twoP.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(ProfileRules r, float offset, float x0, float x1, float z) =>
            Network.Apply(g => g.AddSpline(new Alignment([new Pi(V(x0, z)), new Pi(V(x1, z))]), r, offset: offset));
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }
        var g = Network.Graph;

        // Heading east, left (+offset) is north (−z).
        Road(four, 0, 300, 500, 500);
        Road(two, -4, 500, 700, 500);   // south sides lined up: 4 + 8 = 12
        Road(two, 2, 700, 900, 500);    // the same road, 6 m further north
        Road(four, 0, 900, 1100, 500);
        g = Network.Graph;
        GD.Print($"Demo offset: {g.EdgeCount} edges, {Network.Footprints.Count} footprints, {Network.Issues.Count} issues");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        foreach (var x in new[] { 500f, 700f, 900f })
        {
            int n = g.NodeAt(V(x, 500)) ?? -1;
            if (n < 0 || !Network.Footprints.TryGetValue(n, out var f) || !f.Continuous) { problems.Add($"no transition at x = {x}"); continue; }
            GD.Print($"  x = {x}: cuts {string.Join(", ", f.Cuts.Select(c => $"{g.Edge(c.EdgeId).Rules.Id} {g.Edge(c.EdgeId).Offset:+0.#;-0.#;0} at {c.CutBack:0.#}"))}");
            Links(n, $"x = {x}");
        }
        // The lined-up south side runs straight through the first transition.
        if (g.NodeAt(V(500, 500)) is { } n500 && Network.Footprints.TryGetValue(n500, out var f500))
        {
            float worst = f500.Outline.Where(p => p.Y > 505).Select(p => MathF.Abs(p.Y - 512)).DefaultIfEmpty(99).Max();
            GD.Print($"  x = 500: south side off its line by {worst:0.###} m");
            Expect($"lined-up side off by {worst:0.##} m at x = 500", worst < 0.05f);
        }
        Expect($"{Network.Issues.Count} issues, want none", Network.Issues.Count == 0);

        // Chevrons only where a lane has no link: none with the road's own links, one once a merging lane loses its link.
        Expect($"{visual.Hatches} hatched areas with every lane linked, want none", visual.Hatches == 0);
        if (g.NodeAt(V(500, 500)) is { } drop && Network.Footprints.TryGetValue(drop, out var fd) && visual.LanesAt(g, fd) is { } dropArms)
        {
            var links = LaneLinks.Resolve(g, dropArms);
            var cut = links.First(l => dropArms[l.From].In.Count == 2 && l.FromLane == 0); // the kerbside lane of the two merging
            Network.Apply(gr => { LaneLinks.Set(gr, dropArms, links.Where(l => l != cut)); return true; });
            GD.Print($"  x = 500 without the kerbside lane's link: {visual.Hatches} hatched area(s)");
            Expect($"{visual.Hatches} hatched areas with a lane unlinked, want 1", visual.Hatches == 1);
            if (shot == "chevron") { GD.Print("Demo offset: chevron left"); return; }
            Network.Undo();
        }

        // Replace: the four-lane piece in a two-lane road, replaced by the two-lane in line, makes it one road again.
        Road(two, 0, 300, 500, 650);
        Road(four, 0, 500, 700, 650);
        Road(two, 0, 700, 900, 650);
        int OnLine(float z) => Network.Graph.Edges.Count(e => MathF.Abs(e.Alignment.Pis[0].Position.Y - z) < 0.1f && MathF.Abs(e.Alignment.Pis[^1].Position.Y - z) < 0.1f);
        int mid = Network.Graph.Edges.First(e => e.Rules.Id == "four_lane" && MathF.Abs(e.Alignment.Pis[0].Position.Y - 650) < 0.1f).Id;
        Network.Apply(gr => gr.ReplaceEdge(mid, two, 0));
        GD.Print($"  replaced at z = 650: {OnLine(650)} edge(s) on the line");
        Expect($"{OnLine(650)} edges at z = 650 after the replace, want 1", OnLine(650) == 1);

        // A two-lane road widening to four lanes at a T (a junction, not a transition): the four-lane side tapers in.
        Road(two, 0, 300, 500, 950);
        Road(four, 0, 500, 700, 950);
        Network.Apply(gr => gr.AddSpline(new Alignment([new Pi(V(500, 950)), new Pi(V(500, 1100))]), two));
        // The same, the four-lane moved over so its south side lines up (offset +4), the T on that side: only the north
        // side tapers in.
        Road(two, 0, 300, 500, 1250);
        Road(four, 4, 500, 700, 1250);
        Network.Apply(gr => gr.AddSpline(new Alignment([new Pi(V(500, 1250)), new Pi(V(500, 1400))]), two));
        if (Network.Graph.NodeAt(V(500, 1250)) is { } tOff && Network.Footprints.TryGetValue(tOff, out var ft))
        {
            // At the node the north side is the two-lane's (z = 1242), not stepped in to the four-lane's width.
            float off = ft.Outline.Where(p => MathF.Abs(p.X - 500) < 0.5f && p.Y < 1250).Select(p => MathF.Abs(p.Y - 1242)).DefaultIfEmpty(99).Max();
            GD.Print($"  offset T at z = 1250: north side off the two-lane's by {off:0.###} m at the node");
            Expect($"offset T's north side off by {off:0.##} m at the node", off < 0.05f);
        }

        // The tools, with a road picked: a parallel draw and a Replace.
        if (GetTree().Root.FindChild("SplineDrawTool", true, false) is SplineDrawTool draw && Host.Profile is { } picked && Host.Hud is { } hud)
        {
            await Frames(2);
            Road(four, 0, 300, 500, 800);
            hud.RoadOptions.SetMode(RoadDrawMode.Straight);
            draw.ForcedPlanCursor = V(500, 800);
            draw.PlaceForTest(hard: false);
            draw.ForcedPlanCursor = V(620, 803.6f);
            float legOffset = draw.LegOffsetForTest();
            GD.Print($"  draw {picked.Id} from the four-lane's end, cursor 3.6 m south: offset {legOffset:+0.#;-0.#;0}");
            if (picked.Id == "two_lane") Expect($"draw offset {legOffset}, want -4 (sides lined up)", MathF.Abs(legOffset + 4) < 0.01f);
            if (shot == "draw") { GD.Print("Demo offset: draw left open"); return; }
            draw.SnapClickForTest();
            var built = Network.Graph.Edges.FirstOrDefault(e => e.Rules.Id == picked.Id && MathF.Abs(e.Alignment.Pis[0].Position.Y - 800) < 0.1f);
            Expect("the parallel leg wasn't built on the line", built is not null && MathF.Abs(built.Alignment.Pis[^1].Position.Y - 800) < 0.1f);
            draw.FinishForTest();

            hud.RoadOptions.SetMode(RoadDrawMode.Replace);
            draw.ForcedPlanCursor = V(400, 795);
            await Frames(2);
            if (shot == "replace") { GD.Print("Demo offset: replace left open"); return; }
            float? replaced = draw.ReplaceForTest();
            GD.Print($"  replace the four-lane at z = 800 with {picked.Id}, cursor 5 m north: offset {replaced?.ToString("+0.#;-0.#;0") ?? "none"}");
            if (picked.Id == "two_lane") Expect($"replace offset {replaced}, want +4 (held within the next road)", replaced is { } r && MathF.Abs(r - 4) < 0.01f);
            hud.RoadOptions.SetMode(RoadDrawMode.Straight);
        }
        else GD.Print("  (no road picked: the draw and Replace checks need --ui=open:roads,pick:two_lane)");

        foreach (var p in problems) GD.PrintErr($"Demo offset: {p}");
        GD.Print(problems.Count == 0 ? "Demo offset: all ok" : $"Demo offset: {problems.Count} problem(s)");

        void Links(int node, string name)
        {
            if (!Network.Footprints.TryGetValue(node, out var f) || visual.LanesAt(Network.Graph, f) is not { } arms)
            {
                problems.Add($"{name}: no lanes");
                return;
            }
            var links = LaneLinks.Resolve(Network.Graph, arms);
            GD.Print($"    {links.Count} links: {string.Join(", ", links.Select(l => $"{l.From}.{l.FromLane}→{l.To}.{l.ToLane}"))}");
            for (int a = 0; a < arms.Count; a++)
            {
                for (int k = 0; k < arms[a].In.Count; k++)
                    Expect($"{name}: arm {a} lane in {k} has no link", links.Any(l => l.From == a && l.FromLane == k));
                for (int k = 0; k < arms[a].Out.Count; k++)
                    Expect($"{name}: arm {a} lane out {k} gets no link", links.Any(l => l.To == a && l.ToLane == k));
            }
        }
    }
}
