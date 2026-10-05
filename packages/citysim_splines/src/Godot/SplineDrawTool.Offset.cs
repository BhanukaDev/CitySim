using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Moving roads sideways with the mouse (<see cref="Offsets"/>; the CS2 way, not CS1's always-centred): a leg drawn
/// straight on from a dead end runs parallel to it, its centre where the cursor is across the road, and Replace mode
/// puts the picked profile on a built edge the same way. The piece never turns to reach its offset: its nodes stay on
/// the old line and the road runs beside them (<see cref="GraphEdge.Offset"/>), easing across at the node
/// (<see cref="Junctions.IsTransition"/>).
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>A cursor at least this far ahead of a dead end (or the road's half width) runs the leg on parallel.</summary>
    private const float ParallelAhead = 8f;
    /// <summary>How far past the range across the road the cursor may be and still pick an offset (beyond, the leg turns).</summary>
    private const float ParallelSlack = 2f;

    /// <summary>The offset the leg being drawn gets: the dead end's it starts from (so it runs on as one road), or one
    /// the cursor picked (<see cref="ParallelLeg"/>).</summary>
    private float _legOffset;
    private OffsetBar? _offsetBar;

    /// <summary>Replace mode: the edge under the cursor and what it would become.</summary>
    private (int EdgeId, ProfileRules Rules, float Offset)? _replace;
    private (int EdgeId, string Profile, float Offset)? _replaceShown;
    private List<Issue> _replaceIssues = new();

    /// <summary>
    /// Sets <see cref="_legOffset"/> for the leg from the chain's last point. From a dead end it's that road's offset;
    /// in Draw mode, with the cursor ahead of it within the range across it, the leg runs straight on and the cursor's
    /// position across picks the offset (the snap moves onto the road's line, so a click builds it there).
    /// </summary>
    private void ParallelLeg(ProfileRules rules, NumVector2 cursor)
    {
        _offsetBar = null;
        _legOffset = 0;
        if (_session.IsEmpty || Network is null || _snap is not { } snap) return;
        var g = Network.Graph;
        var start = _session.Pis[0].Position;
        if (g.DeadEndAt(start, rules) is not { } dead || g.NodeAt(start) is not { } node) return;
        var old = g.Edge(dead.EdgeId);
        var travel = -g.Arms(node)[0].Direction;
        float oldOffset = -old.OffsetFrom(dead.AtStart); // left of the way the leg goes on
        _legOffset = oldOffset;
        if (Mode != DrawMode.Draw || snap.Kind is SnapKind.Node or SnapKind.Edge) return;

        var left = SplineMath.Left(travel);
        float along = NumVector2.Dot(snap.Position - start, travel), across = NumVector2.Dot(cursor - start, left);
        float half = rules.Width / 2, oldHalf = old.Rules.Width / 2;
        if (along < MathF.Max(ParallelAhead, half) || MathF.Abs(across - oldOffset) > MathF.Max(half, oldHalf) + ParallelSlack) return;
        var pick = Offsets.Pick(across, half, new[] { (oldOffset, oldHalf) });
        _legOffset = pick.Offset;
        var end = start + travel * along;
        _snap = snap with { Position = end, Tag = "" };
        var at = end - travel * MathF.Min(along / 3, 8f);
        string label = Offsets.Label(pick.Offset - oldOffset) + (pick.Snap.Length > 0 ? $" · {pick.Snap}" : "");
        _offsetBar = new OffsetBar(at + left * pick.Min, at + left * pick.Max, pick.Marks.Select(v => at + left * v).ToList(),
            at + left * pick.Offset, label);
    }

    // --- Replace ---

    /// <summary>
    /// Replace mode, every frame: the edge under the cursor (on its road, of a profile the picked one connects to) is
    /// shown as it would be with the picked profile, its centre where the cursor is across it (held within the roads it
    /// runs on into). The whole network is drawn as that trial, so the transitions show as they'll be built.
    /// </summary>
    private void ProcessReplace(SplineProfile profile, ProfileRules rules, NumVector2 cursor)
    {
        ClearBends();
        Network!.Hide(Array.Empty<int>());
        _renderer!.SetPreview(null, 0);
        _renderer.SetGhost(null, 0);
        _replace = null;
        _trial = null;
        _suggestion = null;
        OffsetBar? bar = null;
        var g = Network.Graph;
        if (EdgeUnder(g, cursor, rules) is { } hit)
        {
            var (e, cp) = hit;
            var pick = Offsets.Pick(cp.Offset, rules.Width / 2, Neighbours(g, e));
            _replace = (e.Id, rules, pick.Offset);
            if (_replaceShown != (e.Id, rules.Id, pick.Offset))
            {
                _replaceShown = (e.Id, rules.Id, pick.Offset);
                var trial = g.Clone();
                int id = trial.ReplaceEdge(e.Id, rules, pick.Offset);
                Network.Conform(trial);
                var touched = new[] { trial.Edge(id).Start, trial.Edge(id).End };
                var edges = touched.SelectMany(n => trial.Node(n).Edges).Distinct();
                _replaceIssues = Validation.Check(trial, edges, touched)
                    .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
                    .ToList();
                Network.ShowTrial(trial);
            }
            var sample = e.Alignment.Curve.Sample(cp.S);
            var left = SplineMath.Left(sample.Tangent);
            NumVector2 At(float u) => sample.Position + left * u;
            string label = Offsets.Label(pick.Offset) + (pick.Snap.Length > 0 ? $" · {pick.Snap}" : "");
            bar = new OffsetBar(At(pick.Min), At(pick.Max), pick.Marks.Select(At).ToList(), At(pick.Offset), label);
        }
        else ClearReplace();

        var issues = _replace is null ? new List<Issue>() : _replaceIssues;
        _issueList?.Show(issues, Network.Issues, Host!.Anarchy);
        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        _overlay!.Show(new OverlayFrame
        {
            Rules = rules,
            Mouse = _view.MouseScreen(),
            OffsetBar = bar,
            PlaceLabel = "Replace",
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Issues = issues,
            Worst = Validation.Worst(issues),
        });
    }

    /// <summary>LMB in Replace mode: builds what's shown (one undo step), unless it's refused (red, without Anarchy).</summary>
    private void ReplaceClick()
    {
        if (_replace is not { } r || Network is null || Host?.Profile is not { } profile) return;
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        var at = Network.Graph.Edge(r.EdgeId).Alignment.Curve.Sample(Network.Graph.Edge(r.EdgeId).Alignment.Length / 2).Position;
        if (_replaceIssues.FirstOrDefault(i => i.Severity == Severity.Invalid) is { } bad && !Host.Anarchy)
        {
            _flashes.Add((new FlashTag(at, $"Can't build: {bad.Message}", Bad: true), until));
            return;
        }
        Network.RegisterProfile(profile);
        Network.Apply(g => g.ReplaceEdge(r.EdgeId, r.Rules, r.Offset));
        _replaceShown = null;
        _replace = null;
    }

    /// <summary>Back to the built network after a Replace preview.</summary>
    private void ClearReplace()
    {
        if (_replaceShown is null) return;
        _replaceShown = null;
        Network?.ShowTrial(null);
    }

    /// <summary>The edge whose road the cursor is on (nearest its centre), of a profile <paramref name="rules"/>
    /// connects to, with the cursor's place along it and across its alignment.</summary>
    private static (GraphEdge Edge, CurvePoint At)? EdgeUnder(SplineGraph g, NumVector2 p, ProfileRules rules)
    {
        (GraphEdge, CurvePoint)? best = null;
        float bestD = float.PositiveInfinity;
        foreach (var e in g.Edges)
        {
            if (!SplineGraph.Connects(rules, e.Rules) && e.Rules.Id != rules.Id) continue;
            var cp = e.Alignment.Curve.ClosestPoint(p);
            float d = MathF.Abs(cp.Offset - e.Offset);
            if (d > e.Rules.Width / 2 || d >= bestD) continue;
            var at = e.Alignment.Curve.Sample(cp.S);
            if (MathF.Abs(NumVector2.Dot(p - at.Position, at.Tangent)) > 0.5f) continue; // past an end
            (best, bestD) = ((e, cp), d);
        }
        return best;
    }

    /// <summary>The roads an edge runs on into at its ends (nodes with just the two of them): each one's offset across
    /// the edge's heading and its half width.</summary>
    private static List<(float Offset, float Half)> Neighbours(SplineGraph g, GraphEdge e)
    {
        var list = new List<(float, float)>();
        foreach (int n in new[] { e.Start, e.End }.Distinct())
        {
            var arms = g.Arms(n);
            if (arms.Count != 2) continue;
            var other = arms.FirstOrDefault(a => a.EdgeId != e.Id);
            if (other.EdgeId == 0) continue;
            // At the start the other road heads the opposite way, so its left is this one's right.
            list.Add((n == e.Start ? -other.Offset : other.Offset, other.Rules.Width / 2));
        }
        return list;
    }

    // --- Test hooks ---

    /// <summary>Replace mode at <see cref="ForcedPlanCursor"/>: one frame, then a click. Returns the offset it built
    /// (null when nothing was under the cursor).</summary>
    public float? ReplaceForTest()
    {
        _Process(0);
        if (_replace is not { } r) return null;
        ReplaceClick();
        return r.Offset;
    }

    /// <summary>The offset the preview leg would be built with, after one frame (Draw mode).</summary>
    public float LegOffsetForTest()
    {
        _Process(0);
        return _legOffset;
    }
}
