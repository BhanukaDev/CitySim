using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    /// <summary>
    /// <c>--demo-cluster</c>: three junctions too close to fit apart (user report, 2026-10-05): a 4-way at (700, 500), a
    /// diagonal crossing its east arm 20 m on and joining its north arm 32 m up, so the three footprints overlap round a
    /// small triangle between the roads. Checks they're one cluster drawn with one island, a zebra out to it and chevrons,
    /// no markings on the roads inside it, and every surface; then undo (a 4-way and a T that fit apart) and redo. Prints
    /// "Demo cluster: all ok". <c>--demo-road</c> checks its plain junctions aren't clustered.
    /// </summary>
    private void RunCluster()
    {
        var problems = new List<string>();
        if (Host?.ProfileFor("two_lane") is not { } profile || Network is null || Host.Visual is not { } visual)
        {
            GD.PrintErr("Demo cluster: no two_lane road, network or road visual");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(NumVector2 a, NumVector2 b) => Network.Apply(g => g.AddSpline(new Alignment([new Pi(a), new Pi(b)]), rules));
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }
        List<JunctionCluster> Clusters() => JunctionClusters.Find(Network.Graph, Network.Footprints);

        Road(V(600, 500), V(820, 500));     // the street
        Road(V(700, 400), V(700, 600));     // a 4-way at 700
        Road(V(790, 570), V(720, 500));     // the diagonal from the south-east to the street...
        Road(V(720, 500), V(700, 468));     // ...and on to the north arm
        var g = Network.Graph;
        GD.Print($"Demo cluster: {g.EdgeCount} edges, {g.Nodes.Count()} nodes, {Network.Footprints.Count} junctions, {Network.Issues.Count} issues");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        var clusters = Clusters();
        foreach (var c in clusters) GD.Print($"  cluster: nodes {string.Join(",", c.Nodes)}, inner {string.Join(",", c.Inner)}, {c.Arms.Count} arms");
        GD.Print($"  {visual.Islands} islands, {visual.IslandCrossings} island crossings, {visual.Hatches} hatched");

        Expect($"{Network.Issues.Count} issues, want none", Network.Issues.Count == 0);
        Expect($"{clusters.Count} clusters, want 1", clusters.Count == 1);
        if (clusters.FirstOrDefault() is { } cl)
        {
            Expect($"cluster has {cl.Nodes.Count} nodes, want 3", cl.Nodes.Count == 3);
            Expect($"cluster has {cl.Inner.Count} inner edges, want 3 (the triangle)", cl.Inner.Count == 3);
            Expect($"cluster has {cl.Arms.Count} arms, want 5 (N, W, S, E, SE)", cl.Arms.Count == 5);
            Expect("an inner edge has a stop line or crossing", !visual.Marks.Keys.Any(k => cl.Inner.Contains(k.Edge)));
            Expect($"only {cl.Arms.Count(a => visual.Marks.TryGetValue((a.EdgeId, a.AtStart), out var m) && m.Zebra)} of 5 arms have a crossing",
                cl.Arms.All(a => visual.Marks.TryGetValue((a.EdgeId, a.AtStart), out var m) && m.Zebra));
        }
        Expect($"{visual.Islands} islands, want 1", visual.Islands == 1);
        Expect($"{visual.IslandCrossings} island crossings, want 1 (across the diagonal)", visual.IslandCrossings == 1);
        Expect("no chevrons (want the wedge up to the north join)", visual.Hatches >= 1);
        foreach (var kind in new[] { SurfaceKind.Asphalt, SurfaceKind.Gutter, SurfaceKind.Kerb, SurfaceKind.Sidewalk, SurfaceKind.Paint, SurfaceKind.Wear })
            Expect($"no {kind} drawn", visual.Counts.GetValueOrDefault(kind) > 0);

        // Without the diagonal's last leg: a 4-way and a T 20 m apart, which fit as two junctions.
        Network.Undo();
        Expect($"after undo: {Clusters().Count} clusters, want none", Clusters().Count == 0);
        Expect($"after undo: {visual.Islands} islands, want 0", visual.Islands == 0);
        Network.Redo();
        Expect($"after redo: {visual.Islands} islands, want 1", Clusters().Count == 1 && visual.Islands == 1);

        foreach (var p in problems) GD.PrintErr($"Demo cluster: {p}");
        GD.Print(problems.Count == 0 ? "Demo cluster: all ok" : $"Demo cluster: {problems.Count} problem(s)");
    }
}
