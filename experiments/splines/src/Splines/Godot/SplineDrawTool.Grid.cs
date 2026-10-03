using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Grid mode (key 4; DESIGN.md → Modes, 4): corner, width, depth. The second click sets the first edge's direction
/// (snaps and Ctrl steps apply, and the grid takes its rotation) and its width in whole blocks; the third sets the
/// side and the depth in whole blocks. The block size is per axis, in lots of clear space between the roads
/// (<see cref="SplinesTestbed.GridLots"/>: <c>[</c> <c>]</c> / Shift+wheel along, Shift+<c>[</c> <c>]</c> /
/// Ctrl+Shift+wheel across, or the options bar). From the second click the whole grid is tried on a copy of the
/// graph and drawn in place of the built one; the third builds every road in one undo step. Its ends don't continue
/// dead ends, so its corners stay square.
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>The second click (the first edge's end, rounded to whole blocks), or null before it.</summary>
    private NumVector2? _gridAlongEnd;
    private bool _showingGridTrial;

    private GridLayout? Layout(ProfileRules rules, NumVector2 alongEnd, NumVector2? depthAt)
    {
        var (along, across) = Testbed?.GridLots ?? (GridLayout.DefaultLots, GridLayout.DefaultLots);
        return GridLayout.From(_session.Pis[0].Position, alongEnd, depthAt, along, across, rules);
    }

    /// <summary>The first edge while the width is picked: the corner to the cursor, rounded to whole blocks.</summary>
    private Alignment GridWidthPreview(ProfileRules rules)
    {
        var start = _session.Pis[0].Position;
        var end = Layout(rules, _snap!.Position, null)?.AlongEnd ?? _snap.Position;
        return new Alignment(new[] { new Pi(start), new Pi(end) });
    }

    private string? GridWidthLabel(ProfileRules rules)
    {
        if (_session.IsEmpty || _snap is null || Layout(rules, _snap.Position, null) is not { } g) return null;
        var (along, _) = Testbed!.GridLots;
        return $"{g.Cols} blocks · {along} {rules.SnapUnitName}s · {g.Width:0} m";
    }

    private void GridClick(NumVector2 at)
    {
        if (Testbed?.Profile is not { } profile || Network is null) return;
        var rules = profile.ToRules();
        if (_session.IsEmpty)
        {
            _sessionProfile = profile;
            _session.Reset(profile.DefaultRadius);
            _session.Place(at, false);
            return;
        }
        if (_gridAlongEnd is null)
        {
            _gridAlongEnd = Layout(rules, at, null)?.AlongEnd;
            return;
        }
        if (Layout(rules, _gridAlongEnd.Value, at) is { } grid) BuildGrid(grid, profile);
    }

    /// <summary>The whole grid on a copy of the graph, and the issues it brings (not ones already there).</summary>
    private (SplineGraph Graph, List<Issue> Issues, List<int> Nodes) TryGrid(GridLayout grid, ProfileRules rules)
    {
        var g = Network!.Graph.Clone();
        var edges = new HashSet<int>();
        var nodes = new HashSet<int>();
        foreach (var line in grid.Lines())
        {
            var r = g.AddSpline(line, rules, Ends.None);
            nodes.UnionWith(r.Nodes);
        }
        // Later lines split the earlier ones: every edge at the nodes the grid touched is the grid's (or one it joined).
        foreach (int n in nodes.Where(g.HasNode)) edges.UnionWith(g.Node(n).Edges);
        var issues = Validation.Check(g, edges.Where(g.HasEdge), nodes.Where(g.HasNode))
            .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
        return (g, issues, nodes.Where(g.HasNode).ToList());
    }

    private void ProcessGrid(SplineProfile profile, ProfileRules rules, NumVector2 raw)
    {
        _trial = null;
        _suggestion = null;
        Network!.Hide(Array.Empty<int>());
        _renderer!.SetPreview(null, 0);
        _renderer.SetGhost(null, 0);
        var grid = Layout(rules, _gridAlongEnd!.Value, raw);
        List<Issue> issues = new();
        if (grid is not null)
        {
            Network.RegisterProfile(profile);
            var (g, found, _) = TryGrid(grid, rules);
            issues = found;
            Network.ShowTrial(g);
            _showingGridTrial = true;
        }
        _issueList?.Show(issues, Network.Issues, Testbed!.Anarchy);

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        var (along, across) = Testbed.GridLots;
        _overlay!.Show(new OverlayFrame
        {
            Rules = rules,
            BuiltEnds = SplineToolView.Points(Network.Graph),
            Mouse = _view.MouseScreen(),
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Grid = grid,
            GridLabel = grid?.Label(along, across, rules.SnapUnitName),
            Issues = issues,
            Worst = Validation.Worst(issues),
            PlaceLabel = "Place depth",
        });
    }

    /// <summary>Builds every road of the grid as one undo step, unless it has an Invalid issue and Anarchy is off.</summary>
    private void BuildGrid(GridLayout grid, SplineProfile profile)
    {
        var rules = profile.ToRules();
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        var far = grid.Point(grid.Cols, grid.Rows);
        if (TryGrid(grid, rules).Issues.FirstOrDefault(i => i.Severity == Severity.Invalid) is { } bad && !Testbed!.Anarchy)
        {
            _flashes.Add((new FlashTag(far, $"Can't build: {bad.Message}", Bad: true), until));
            return;
        }
        Network!.RegisterProfile(profile);
        Network.Apply(g =>
        {
            foreach (var line in grid.Lines()) g.AddSpline(line, rules, Ends.None);
            return 0;
        });
        var (along, across) = Testbed!.GridLots;
        _flashes.Add((new FlashTag(far, grid.Label(along, across, rules.SnapUnitName)), until));
        CancelSession();
    }

    /// <summary>Back to the built graph's own visuals, if the grid's trial was showing.</summary>
    private void ClearGridTrial()
    {
        if (!_showingGridTrial) return;
        _showingGridTrial = false;
        Network?.ShowTrial(null);
    }

    /// <summary><c>[</c> <c>]</c> / Shift+wheel: one lot more or less along the first edge (or across).</summary>
    private void AdjustGridLots(int step, bool across)
    {
        if (Testbed is null) return;
        var (a, c) = Testbed.GridLots;
        if (across) c += step; else a += step;
        Testbed.SetGridLots(a, c);
    }
}
