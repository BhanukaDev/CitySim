using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Grid mode (key 4; DESIGN.md → Modes, 4): corner, width, depth. The second click sets the first edge's direction
/// (snaps and Ctrl steps apply, and the grid takes its rotation) and its width; the third sets the side and the depth.
/// The outline is where the clicks are; inside it the blocks per axis are <see cref="ISplineToolHost.GridBlocks"/>
/// (<c>[</c> <c>]</c> / Shift+wheel along, Shift+<c>[</c> <c>]</c> / Ctrl+Shift+wheel across, or the options bar),
/// sharing the space by <see cref="ISplineToolHost.GridFit"/>. A side on a built road reuses it. From the second click
/// the whole grid is tried on a copy of the graph and drawn in place of the built one; the third builds every road
/// in one undo step. Its ends don't continue dead ends, so its corners stay square.
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>The second click (the first edge's end), or null before it.</summary>
    private NumVector2? _gridAlongEnd;
    /// <summary>The second click snapped onto a built road or node: the far column stays there.</summary>
    private bool _gridAlongPinned;
    private bool _showingGridTrial;

    private GridLayout? Layout(ProfileRules rules, NumVector2 alongEnd, NumVector2? depthAt, bool pinAlong, bool pinAcross = false)
    {
        var (cols, rows) = Host?.GridBlocks ?? (GridLayout.DefaultCols, GridLayout.DefaultRows);
        var built = Network is null ? null : GridLayout.Built(Network.Graph, rules);
        return GridLayout.From(_session.Pis[0].Position, alongEnd, depthAt, cols, rows, Host?.GridFit ?? GridFit.Even, rules, built,
            pinAlong, pinAcross);
    }

    /// <summary>Whether the cursor snapped onto a built road or node (that side of the grid then stays on it).</summary>
    private bool SnappedOnBuilt => _snap?.Kind is SnapKind.Node or SnapKind.Edge;

    /// <summary>The third click's point: the cursor, or the built road or node it snapped onto. Other snaps (angle,
    /// length, guides from the corner) don't apply to the depth.</summary>
    private NumVector2 GridDepthAt(NumVector2 raw) => SnappedOnBuilt ? _snap!.Position : raw;

    /// <summary>The first edge while the width is picked: the corner to the cursor (in lot steps with that fit).</summary>
    private Alignment GridWidthPreview(ProfileRules rules)
    {
        var start = _session.Pis[0].Position;
        var end = Layout(rules, _snap!.Position, null, SnappedOnBuilt)?.AlongEnd ?? _snap.Position;
        return new Alignment(new[] { new Pi(start), new Pi(end) });
    }

    private string? GridWidthLabel(ProfileRules rules)
    {
        if (_session.IsEmpty || _snap is null || Layout(rules, _snap.Position, null, SnappedOnBuilt) is not { } g) return null;
        return g.Label();
    }

    /// <summary>A click: <paramref name="at"/> is where it snapped, <paramref name="raw"/> the cursor.</summary>
    private void GridClick(NumVector2 at, NumVector2 raw)
    {
        if (Host?.Profile is not { } profile || Network is null) return;
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
            if (Layout(rules, at, null, SnappedOnBuilt) is not null) (_gridAlongEnd, _gridAlongPinned) = (at, SnappedOnBuilt);
            return;
        }
        if (Layout(rules, _gridAlongEnd.Value, GridDepthAt(raw), _gridAlongPinned, SnappedOnBuilt) is { } grid) BuildGrid(grid, profile);
    }

    /// <summary>The whole grid on a copy of the graph, and the issues it brings (not ones already there).</summary>
    private (SplineGraph Graph, List<Issue> Issues, List<int> Nodes) TryGrid(GridLayout grid, ProfileRules rules)
    {
        var g = Network!.Graph.Clone();
        var edges = new HashSet<int>();
        var nodes = new HashSet<int>();
        foreach (var line in grid.Lines(Network.Graph, rules))
        {
            var r = g.AddSpline(line, rules, Ends.None);
            nodes.UnionWith(r.Nodes);
        }
        // Later lines split the earlier ones: every edge at the nodes the grid touched is the grid's (or one it joined).
        foreach (int n in nodes.Where(g.HasNode)) edges.UnionWith(g.Node(n).Edges);
        var issues = Validation.Check(g, edges.Where(g.HasEdge), nodes.Where(g.HasNode))
            .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
        if (grid.TooSmall)
            issues.Add(new Issue(Severity.Invalid, "grid-block", $"blocks under {GridLayout.MinLots} {rules.SnapUnitName}s · Ctrl+A allows",
                grid.Point(grid.Cols, grid.Rows)));
        return (g, issues, nodes.Where(g.HasNode).ToList());
    }

    private void ProcessGrid(SplineProfile profile, ProfileRules rules, NumVector2 raw)
    {
        _trial = null;
        _suggestion = null;
        Network!.Hide(Array.Empty<int>());
        _renderer!.SetPreview(null, 0);
        _renderer.SetGhost(null, 0);
        var grid = Layout(rules, _gridAlongEnd!.Value, GridDepthAt(raw), _gridAlongPinned, SnappedOnBuilt);
        List<Issue> issues = new();
        if (grid is not null)
        {
            Network.RegisterProfile(profile);
            var (g, found, _) = TryGrid(grid, rules);
            issues = found;
            Network.ShowTrial(g);
            _showingGridTrial = true;
        }
        _issueList?.Show(issues, Network.Issues, Host!.Anarchy);

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        _overlay!.Show(new OverlayFrame
        {
            Rules = rules,
            BuiltEnds = SplineToolView.Points(Network.Graph),
            Mouse = _view.MouseScreen(),
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Grid = grid,
            GridLabel = grid?.Label(),
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
        if (TryGrid(grid, rules).Issues.FirstOrDefault(i => i.Severity == Severity.Invalid) is { } bad && !Host!.Anarchy)
        {
            _flashes.Add((new FlashTag(far, $"Can't build: {bad.Message}", Bad: true), until));
            return;
        }
        Network!.RegisterProfile(profile);
        var lines = grid.Lines(Network.Graph, rules);
        Network.Apply(g =>
        {
            foreach (var line in lines) g.AddSpline(line, rules, Ends.None);
            return 0;
        });
        _flashes.Add((new FlashTag(far, grid.Label()), until));
        CancelSession();
    }

    /// <summary>Back to the built graph's own visuals, if the grid's trial was showing.</summary>
    private void ClearGridTrial()
    {
        if (!_showingGridTrial) return;
        _showingGridTrial = false;
        Network?.ShowTrial(null);
    }

    /// <summary><c>[</c> <c>]</c> / Shift+wheel: one block more or less along the first edge (or across). The count
    /// steps from what is shown (fewer than set where the outline can't take more), so the outline never moves.</summary>
    private void AdjustGridBlocks(int step, bool across)
    {
        if (Host is null) return;
        var (c, r) = Host.GridBlocks;
        if (!_session.IsEmpty && _snap is not null && Host.Profile?.ToRules() is { } rules)
        {
            var shown = _gridAlongEnd is { } end
                ? Layout(rules, end, GridDepthAt(_snap.Position), _gridAlongPinned, SnappedOnBuilt)
                : Layout(rules, _snap.Position, null, SnappedOnBuilt);
            if (shown is not null) { c = Math.Min(c, shown.Cols); if (shown.Rows > 0) r = Math.Min(r, shown.Rows); }
        }
        if (across) r += step; else c += step;
        Host.SetGridBlocks(c, r);
    }
}
