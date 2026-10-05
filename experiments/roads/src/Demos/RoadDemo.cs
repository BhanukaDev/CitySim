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

/// <summary>
/// <c>--demo-road[=&lt;road id&gt;]</c> (default <c>two_lane</c>): builds a small network with that road (a street with a
/// dead end, a T, a 4-way, a 60° skewed T, a curve, and two hard corners), checks its cross-section and that every
/// kind of surface was drawn, and prints "Demo road: all ok" (or the problems). Add <c>--screenshot</c> and
/// <c>--cam</c> to look at it; run headless with <c>--quit-after</c> for the checks alone.
/// <c>--demo-shape</c>: a road over a hill and a dip (<see cref="RunShape"/>). <c>--demo-slope</c>: a draw left open up a
/// steep hill, for a screenshot of the slope pills and the red grade (<see cref="RunSlope"/>). <c>--demo-continue</c>: a draw
/// left open from a short dead end off a 4-way (<see cref="RunContinue"/>; with <c>--ui=open:roads,pick:two_lane</c>).
/// <c>--demo-grid[=&lt;road id&gt;]</c>: a 3 × 2 grid as Grid mode builds it, for its 90° corners and Ts (<see cref="RunGrid"/>).
/// <c>--demo-mixed</c>: the four-lane road with the two-lane one: lane drops, mixed junctions (<see cref="RunMixed"/>).
/// <c>--demo-offset[=draw|replace|chevron]</c>: roads moved sideways, transitions, the Replace mode (<see cref="RunOffset"/>).
/// <c>--demo-crossings</c>: crossings and the Crossings tool (<see cref="RunCrossings"/>).
/// <c>--demo-lane-links[=links|pick|add]</c>: lane links and the Lane Links tool (<see cref="RunLaneLinks"/>).
/// <c>--road-age=&lt;0..1&gt;</c>: every road that old, to look at cracks (the game sets age, not the player).
/// <c>--bake-road-thumbnails[=&lt;road id&gt;]</c>: renders the road cards' pictures (<see cref="RoadThumbnailBaker"/>) and quits.
/// <c>--bake-road-textures</c>: rewrites the road shaders' noise textures (<see cref="RoadTextureBaker"/>) and quits.
/// </summary>
public partial class RoadDemo : Node
{
    [Export] public RoadToolHost? Host { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    public override void _Ready()
    {
        // Queued first, so the demos below build their roads at this age.
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--road-age="))
            {
                float age = float.Parse(arg[(arg.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                Callable.From(() => { if (Host?.Visual is { } v) v.AgeOf = _ => age; }).CallDeferred();
            }
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg == "--demo-road" || arg.StartsWith("--demo-road="))
            {
                string id = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : "two_lane";
                Callable.From(() => Run(id)).CallDeferred();
            }
            else if (arg == "--demo-shape") Callable.From(RunShape).CallDeferred();
            else if (arg == "--demo-grid" || arg.StartsWith("--demo-grid="))
            {
                string id = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : "two_lane";
                Callable.From(() => RunGrid(id)).CallDeferred();
            }
            else if (arg == "--demo-mixed") Callable.From(RunMixed).CallDeferred();
            else if (arg == "--demo-offset" || arg.StartsWith("--demo-offset="))
            {
                string shot = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : "";
                Callable.From(() => RunOffset(shot)).CallDeferred();
            }
            else if (arg == "--demo-cluster") Callable.From(RunCluster).CallDeferred();
            else if (arg == "--demo-crossings")
            {
                var at = OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--crossing-cursor="))?["--crossing-cursor=".Length..].Split(',');
                NumVector2? cursor = at is { Length: 2 } ? new NumVector2(float.Parse(at[0], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(at[1], System.Globalization.CultureInfo.InvariantCulture)) : null;
                Callable.From(() => RunCrossings(cursor)).CallDeferred();
            }
            else if (arg == "--demo-lane-links" || arg.StartsWith("--demo-lane-links="))
            {
                string shot = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : "links";
                Callable.From(() => RunLaneLinks(shot)).CallDeferred();
            }
            else if (arg == "--demo-slope") Callable.From(RunSlope).CallDeferred();
            else if (arg == "--demo-continue") Callable.From(RunContinue).CallDeferred();
            else if (arg == "--bake-road-thumbnails" || arg.StartsWith("--bake-road-thumbnails="))
                AddChild(new RoadThumbnailBaker(arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null));
            else if (arg == "--bake-road-textures")
            {
                RoadTextureBaker.Bake();
                GetTree().Quit();
            }
    }

    private void Run(string roadId)
    {
        var problems = new List<string>();
        if (Host?.ProfileFor(roadId) is not { } profile || Network is null || Host.Visual is not { } visual)
        {
            GD.PrintErr($"Demo road: no road \"{roadId}\", network or road visual");
            return;
        }
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        void Road(params Pi[] pis) => Network.Apply(g => g.AddSpline(new Alignment(pis), rules));

        Road(new Pi(V(400, 500)), new Pi(V(900, 500)));                                     // the street, dead end at the west
        Road(new Pi(V(500, 500)), new Pi(V(500, 700)));                                     // a T at 500
        Road(new Pi(V(700, 350)), new Pi(V(700, 700)));                                     // a 4-way at 700, dead end north
        Road(new Pi(V(820, 500)), new Pi(V(820 + 80, 500 + 138.56f)));                      // a 60° skewed T at 820
        Road(new Pi(V(500, 700)), new Pi(V(600, 800), 40), new Pi(V(700, 700)));            // a curve; hard corners at both ends
        Road(new Pi(V(600, 500)), new Pi(V(600 - 120 * 0.866f, 500 - 120 * 0.5f)));         // a 30° Y at 600: spare asphalt, hatched

        var g = Network.Graph;
        GD.Print($"Demo road: {roadId}, {g.EdgeCount} edges, {g.Nodes.Count()} nodes, {Network.Footprints.Count} junctions, " +
                 $"{Network.Issues.Count} issues");
        foreach (var i in Network.Issues) GD.Print($"  issue: {i.Severity} {i.Code} {i.Message}");
        if (Network.Footprints.Count < 3) problems.Add($"{Network.Footprints.Count} junction footprints, want at least 3");
        // Junctions with room between them stay apart (--demo-cluster has ones that don't).
        if (JunctionClusters.Find(g, Network.Footprints) is { Count: > 0 } cls)
            problems.Add($"{cls.Count} junction clusters, want none (nodes {string.Join(" | ", cls.Select(c => string.Join(",", c.Nodes)))})");

        var sec = visual.SectionOf(g.Edges.First());
        GD.Print($"  section: half width {sec.HalfWidth}, half carriageway {sec.HalfCarriageway}");
        foreach (var b in sec.Bands) GD.Print($"    band {b.Surface} {b.Left:0.##} .. {b.Right:0.##}{(b.Raised ? " raised" : "")}");
        foreach (var l in sec.Lines) GD.Print($"    line at {l.Offset:0.##}, {l.Width} wide, {(l.Dash > 0 ? $"dashed {l.Dash}:{l.Gap}" : "solid")}");
        if (roadId == "two_lane")
        {
            if (MathF.Abs(sec.HalfWidth - 8) > 1e-3f) problems.Add($"half width {sec.HalfWidth}, want 8 (16 m road)");
            if (MathF.Abs(sec.HalfCarriageway - 5) > 1e-3f) problems.Add($"half carriageway {sec.HalfCarriageway}, want 5");
            if (sec.Lines.Count != 3) problems.Add($"{sec.Lines.Count} lines, want 3 (centre + 2 edges)");
            if (!sec.Lines.Any(l => l.Centre && MathF.Abs(l.Offset) < 1e-3f && l.Dash == 3 && l.Gap == 6)) problems.Add("no 3:6 centre line at 0");
            if (!sec.Lines.Any(l => !l.Centre && l.Dash == 0 && MathF.Abs(MathF.Abs(l.Offset) - 3.06f) < 1e-3f)) problems.Add("no solid edge line at ±3.06");
            var outline = sec.Outline();
            float top = outline.Max(p => p.Height), crown = sec.CarriagewayHeight(0);
            if (MathF.Abs(top - 0.15f) > 1e-3f) problems.Add($"sidewalk top {top}, want 0.15");
            if (MathF.Abs(crown - 0.1f) > 1e-3f) problems.Add($"crown {crown}, want 0.1 (2 % over 5 m)");
        }
        if (roadId == "four_lane")
        {
            if (MathF.Abs(sec.HalfWidth - 12) > 1e-3f) problems.Add($"half width {sec.HalfWidth}, want 12 (24 m road)");
            if (MathF.Abs(sec.HalfCarriageway - 9) > 1e-3f) problems.Add($"half carriageway {sec.HalfCarriageway}, want 9");
            if (sec.Lines.Count != 5) problems.Add($"{sec.Lines.Count} lines, want 5 (centre + 2 lane + 2 edges)");
            if (!sec.Lines.Any(l => l.Centre && MathF.Abs(l.Offset) < 1e-3f && l.Dash == 0)) problems.Add("no solid centre line at 0");
            if (sec.Lines.Count(l => !l.Centre && l.Dash > 0 && MathF.Abs(MathF.Abs(l.Offset) - 3.5f) < 1e-3f) != 2)
                problems.Add("no dashed lane lines at ±3.5");
        }
        foreach (var (kind, n) in visual.Counts) GD.Print($"  {kind}: {n} triangles");
        GD.Print($"  {visual.Hatches} hatched islands");
        if (roadId == "two_lane" && visual.Hatches == 0) problems.Add("no hatching (want some at the 30° Y)");
        foreach (var kind in new[] { SurfaceKind.Asphalt, SurfaceKind.Gutter, SurfaceKind.Kerb, SurfaceKind.Sidewalk, SurfaceKind.Paint })
            if (sec.HasSidewalks && (kind != SurfaceKind.Gutter || sec.GutterWidth > 0) && visual.Counts.GetValueOrDefault(kind) == 0)
                problems.Add($"no {kind} drawn");

        foreach (var p in problems) GD.PrintErr($"Demo road: {p}");
        GD.Print(problems.Count == 0 ? "Demo road: all ok" : $"Demo road: {problems.Count} problem(s)");
    }

    /// <summary>A 3 × 2 grid at (600, 500), built the way Grid mode builds one: its outer corners are two roads meeting
    /// square at a node, its sides Ts.</summary>
    private void RunGrid(string roadId)
    {
        if (Host?.ProfileFor(roadId) is not { } profile || Network is null)
        {
            GD.PrintErr($"Demo grid: no road \"{roadId}\" or network");
            return;
        }
        var rules = profile.ToRules();
        if (GridLayout.From(new NumVector2(600, 500), new NumVector2(780, 500), new NumVector2(600, 620), 3, 2, GridFit.Even, rules) is not { } grid)
            return;
        var lines = grid.Lines(Network.Graph, rules);
        Network.Apply(g => { foreach (var line in lines) g.AddSpline(line, rules, Ends.None); return true; });
        GD.Print($"Demo grid: {Network.Graph.EdgeCount} edges, {Network.Footprints.Count} junctions, {Network.Issues.Count} issues");
    }

    /// <summary>A 4-way with a short dead end off it, and a draw left open continuing that dead end round a bend: the
    /// junction and the old road up to the bend stay drawn as built while the draw is open. Prints "Demo continue: all ok".</summary>
    private async void RunContinue()
    {
        if (Host?.ProfileFor("two_lane") is not { } profile || Network is null || Host.Visual is not { } visual
            || GetTree().Root.FindChild("SplineDrawTool", true, false) is not SplineDrawTool draw)
        {
            GD.PrintErr("Demo continue: no two_lane road, network, road visual or draw tool");
            return;
        }
        var problems = new List<string>();
        var rules = profile.ToRules();
        static NumVector2 V(float x, float z) => new(x, z);
        Network.Apply(g => g.AddSpline(new Alignment(new[] { new Pi(V(600, 500)), new Pi(V(800, 500)) }), rules));
        Network.Apply(g => g.AddSpline(new Alignment(new[] { new Pi(V(700, 600)), new Pi(V(700, 470)) }), rules));
        await Frames(2);
        int junctionTris = visual.Counts.GetValueOrDefault(SurfaceKind.Asphalt);
        draw.ForcedPlanCursor = V(700, 470);
        draw.PlaceForTest(hard: false);
        draw.ForcedPlanCursor = V(740, 445);
        await Frames(4);
        var hidden = Network.Hidden;
        GD.Print($"Demo continue: hidden {string.Join(", ", hidden.Select(kv => $"{kv.Key} {kv.Value.From:0.#}..{kv.Value.To:0.#}"))}");
        if (hidden.Count != 1) problems.Add($"{hidden.Count} edges hidden, want the dead end");
        else if (hidden.Values.First() is { IsEmpty: true }) problems.Add("the dead end is hidden whole, want it kept up to the bend");
        int during = visual.Counts.GetValueOrDefault(SurfaceKind.Asphalt);
        GD.Print($"Demo continue: asphalt {junctionTris} triangles built, {during} while drawing");
        if (during < junctionTris * 0.8f) problems.Add("asphalt went missing while drawing (the junction?)");
        foreach (var p in problems) GD.PrintErr($"Demo continue: {p}");
        GD.Print(problems.Count == 0 ? "Demo continue: all ok" : $"Demo continue: {problems.Count} problem(s)");
    }
}
