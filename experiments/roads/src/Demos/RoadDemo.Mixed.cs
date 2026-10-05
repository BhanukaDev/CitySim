using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    /// <summary>
    /// <c>--demo-mixed</c>: the four-lane road with the two-lane one. Along z = 500: a four-lane road dropping to two lanes
    /// at 450 (a straight transition), a two-lane T off the four-lane at 600, a two-lane road crossing it at 750 (mixed
    /// 4-way), a four-lane 4-way at 900, then a drop to two lanes 30 m past it at 930. A four-lane curve from 900 runs
    /// south into a two-lane road at its end (a transition on a curve). Checks the transitions get tapers, nothing
    /// clusters or has issues, and the lane links at the mixed junctions. Prints "Demo mixed: all ok".
    /// </summary>
    private void RunMixed()
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("four_lane") is not { } fourP || Host.ProfileFor("two_lane") is not { } twoP
            || Network is null || Host.Visual is not { } visual)
        {
            GD.PrintErr("Demo mixed: no four_lane or two_lane road, network or road visual");
            return;
        }
        ProfileRules four = fourP.ToRules(), two = twoP.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(ProfileRules r, params Pi[] pis) => Network.Apply(g => g.AddSpline(new Alignment(pis), r));
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }

        Road(two, new Pi(V(300, 500)), new Pi(V(450, 500)));                                // two-lane, west end
        Road(four, new Pi(V(450, 500)), new Pi(V(930, 500)));                               // four-lane main road
        Road(two, new Pi(V(930, 500)), new Pi(V(1080, 500)));                               // drops to two-lane 30 m past the 4-way
        Road(two, new Pi(V(600, 500)), new Pi(V(600, 650)));                                // two-lane T off the four-lane
        Road(two, new Pi(V(750, 380)), new Pi(V(750, 650)));                                // two-lane crossing the four-lane
        Road(four, new Pi(V(900, 380)), new Pi(V(900, 500)));                               // four-lane 4-way at 900 (north arm)
        Road(four, new Pi(V(900, 500)), new Pi(V(900, 680), 60), new Pi(V(1000, 680)));      // south arm, curving east
        Road(two, new Pi(V(1000, 680)), new Pi(V(1120, 680)));                              // into a two-lane road

        var g = Network.Graph;
        int NodeAt(float x, float z) => g.NodeAt(V(x, z)) ?? -1;
        GD.Print($"Demo mixed: {g.EdgeCount} edges, {g.Nodes.Count()} nodes, {Network.Footprints.Count} footprints, {Network.Issues.Count} issues");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        Expect($"{Network.Issues.Count} issues, want none", Network.Issues.Count == 0);
        if (JunctionClusters.Find(g, Network.Footprints) is { Count: > 0 } cls)
            problems.Add($"{cls.Count} junction clusters, want none (nodes {string.Join(" | ", cls.Select(c => string.Join(",", c.Nodes)))})");

        // Transitions: a footprint at each, and the four-lane cut back by its taper (2.5 × the 8 m difference); 30 m past
        // the 4-way it gets what the 4-way leaves (13.5 m).
        foreach (var (x, z, name, min) in new[] { (450f, 500f, "west drop", 19f), (930f, 500f, "drop past the 4-way", 10f), (1000f, 680f, "drop after the curve", 19f) })
        {
            int n = NodeAt(x, z);
            if (n < 0 || !Network.Footprints.TryGetValue(n, out var f)) { problems.Add($"{name}: no transition footprint at ({x}, {z})"); continue; }
            var wide = f.Cuts.FirstOrDefault(c => g.Edge(c.EdgeId).Rules.Width > 20);
            GD.Print($"  {name}: cuts {string.Join(", ", f.Cuts.Select(c => $"{g.Edge(c.EdgeId).Rules.Width:0} m road at {c.CutBack:0.#}"))}");
            Expect($"{name}: the four-lane cut back {wide.CutBack:0.#} m, want at least {min}", wide.CutBack >= min);
        }

        // Lane links at the mixed junctions (lanes kerbside first; "in" lanes coming in, "out" going out).
        void Links(string name, float x, float z, int want)
        {
            int n = NodeAt(x, z);
            if (n < 0 || !Network.Footprints.TryGetValue(n, out var f) || visual.LanesAt(g, f) is not { } arms)
            {
                problems.Add($"{name}: no junction with lanes");
                return;
            }
            var links = LaneLinks.Resolve(g, arms);
            GD.Print($"  {name}: {links.Count} links " +
                     $"({links.Count(l => l.Move == Move.Straight)} straight, {links.Count(l => l.Move == Move.Right)} right, {links.Count(l => l.Move == Move.Left)} left)");
            foreach (var l in links)
                GD.Print($"    {Width(arms[l.From])} m arm lane {l.FromLane} → {Width(arms[l.To])} m arm lane {l.ToLane} {l.Move}");
            Expect($"{name}: {links.Count} links, want {want}", links.Count == want);
            // Every lane in gets somewhere, every lane out gets something.
            for (int a = 0; a < arms.Count; a++)
            {
                for (int k = 0; k < arms[a].In.Count; k++)
                    Expect($"{name}: arm {a} lane in {k} has no link", links.Any(l => l.From == a && l.FromLane == k));
                for (int k = 0; k < arms[a].Out.Count; k++)
                    Expect($"{name}: arm {a} lane out {k} gets no link", links.Any(l => l.To == a && l.ToLane == k));
            }
            float Width(ArmLanes al) => g.Edge(al.Edge).Rules.Width;
        }
        // T: four-lane both ways (2 in, 2 out each), two-lane south (1 in, 1 out). Straight 2 + 2, rights 1 + 1, lefts 1 + 1.
        Links("mixed T", 600, 500, 8);
        // Mixed 4-way: straight 2 + 2 + 1 + 1, one right and one left from each arm.
        Links("mixed 4-way", 750, 500, 14);
        // Four-lane 4-way: straight 2 per arm, one right and one left per arm.
        Links("four-lane 4-way", 900, 500, 16);

        foreach (var p in problems) GD.PrintErr($"Demo mixed: {p}");
        GD.Print(problems.Count == 0 ? "Demo mixed: all ok" : $"Demo mixed: {problems.Count} problem(s)");
    }
}
