using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using CitySim.Splines.Godot;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    /// <summary>
    /// <c>--demo-bend-t[=&lt;road id&gt;]</c> (user report, 2026-10-05): a road turning a 90° corner at the default radius, from the south
    /// at (600, 600) up to (600, 500) and east to (700, 500), with a road joined to the middle of its bend at 45° from the
    /// south-east, the way the Draw tool's bend dot makes it. Checks that every wear path stays on the junction's
    /// asphalt and the chevrons are only where no lane drives. Prints "Demo bend T: all ok".
    /// </summary>
    private void RunBendT(string roadId)
    {
        var problems = new List<string>();
        if (Host?.ProfileFor(roadId) is not { } profile || Network is null || Host.Visual is not { } visual)
        {
            GD.PrintErr($"Demo bend T: no road \"{roadId}\", network or road visual");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        Network.Apply(g => g.AddSpline(new Alignment([new Pi(V(600, 600)), new Pi(V(600, 500), rules.DefaultRadius), new Pi(V(700, 500))]), rules));
        var bend = Network.Graph.Edges.First();
        var at = bend.Alignment.RoadPoint(1);
        Network.Apply(g =>
        {
            g.SplitBend(g.Edges.First().Id, 1, bend.Alignment.EffectiveRadius(1));
            return g.AddSpline(new Alignment([new Pi(at), new Pi(at + V(80, 80))]), rules);
        });
        var g = Network.Graph;
        GD.Print($"Demo bend T: {g.EdgeCount} edges, {Network.Footprints.Count} junctions, {Network.Issues.Count} issues, " +
                 $"node at ({at.X:0.#}, {at.Y:0.#}), {visual.Hatches} hatched islands");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        if (Network.Footprints.Count != 1) problems.Add($"{Network.Footprints.Count} junctions, want 1");
        foreach (var f in Network.Footprints.Values)
        {
            var outline = f.Outline.Select(p => new Vector2(p.X, p.Y)).ToArray();
            if (visual.LanesAt(g, f) is not { } arms) continue;
            foreach (var l in LaneLinks.Resolve(g, arms))
            {
                var pts = LaneLinks.Path(arms, l).ToList();
                int off = pts.Count(p => !Geometry2D.IsPointInPolygon(new Vector2(p.P.X, p.P.Y), outline));
                GD.Print($"  link {l.From}.{l.FromLane} → {l.To}.{l.ToLane} {l.Move}: {pts.Count} points, {off} off the junction");
                if (off > 1) problems.Add($"link {l.From} → {l.To} ({l.Move}) leaves the junction at {off} points");
            }
        }
        foreach (var p in problems) GD.PrintErr($"Demo bend T: {p}");
        GD.Print(problems.Count == 0 ? "Demo bend T: all ok" : $"Demo bend T: {problems.Count} problem(s)");
    }

    /// <summary>
    /// <c>--demo-grid-bend</c> (with <c>--ui=open:roads,pick:two_lane,mode:grid</c>; windowed only): the 90° bend of
    /// <see cref="RunBendT"/>, then a grid whose first click is on the bend's slider at its corner point (the corner
    /// magnet, user report 2026-10-05: it didn't work in Grid mode), going on north and west: the bend becomes a 4-way.
    /// Checks the click took the slider's magnet, and the grid's corner is a 4-way at the corner point. Prints "Demo grid bend: all ok".
    /// </summary>
    private async void RunGridBend()
    {
        var problems = new List<string>();
        await Frames(2);
        if (Host?.ProfileFor("two_lane") is not { } profile || Network is null
            || GetTree().Root.FindChild("SplineDrawTool", true, false) is not SplineDrawTool draw)
        {
            GD.PrintErr("Demo grid bend: no two_lane road, network or draw tool");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        Network.Apply(g => g.AddSpline(new Alignment([new Pi(V(600, 600)), new Pi(V(600, 500), rules.DefaultRadius), new Pi(V(700, 500))]), rules));
        var a = Network.Graph.Edges.First().Alignment;
        var half = a.BendPoint(1, 0);
        draw.ForcedPlanCursor = half;
        string tag = draw.SnapClickForTest();
        GD.Print($"Demo grid bend: first click on the slider at ({half.X:0.#}, {half.Y:0.#}): \"{tag}\"");
        if (tag != "corner point") problems.Add($"the first click should take the bend's corner point, got \"{tag}\"");
        draw.ForcedPlanCursor = half + V(0, -96);
        draw.SnapClickForTest();
        draw.ForcedPlanCursor = half + V(-64, -48);
        draw.SnapClickForTest();
        var g = Network.Graph;
        var node = g.NodeAt(half) is { } n ? g.Node(n) : null;
        GD.Print($"Demo grid bend: {g.EdgeCount} edges, {Network.Footprints.Count} junctions, {Network.Issues.Count} issues; " +
                 $"node at the slider point: {(node is null ? "none" : $"{node.Edges.Count} roads")}");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        if (node is not { Edges.Count: 4 }) problems.Add("the grid's corner should be a 4-way at the bend's corner point");
        foreach (var p in problems) GD.PrintErr($"Demo grid bend: {p}");
        GD.Print(problems.Count == 0 ? "Demo grid bend: all ok" : $"Demo grid bend: {problems.Count} problem(s)");
    }
}
