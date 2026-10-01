using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>A built edge as the Edit tool shows it: its alignment and corridor width.</summary>
public readonly record struct EditEdge(Alignment Alignment, float Width);

/// <summary>What the Edit tool shows this frame, handed to <see cref="SplineOverlay.ShowEdit"/> (plan space).</summary>
public sealed class EditFrame
{
    /// <summary>Selected edges: outlined, with their tangent legs, corner points and radius knobs.</summary>
    public IReadOnlyList<EditEdge> Selected { get; init; } = Array.Empty<EditEdge>();
    /// <summary>The edge a click would select (outlined thinner).</summary>
    public EditEdge? HoverEdge { get; init; }
    /// <summary>Every node, a white disc.</summary>
    public IReadOnlyList<NumVector2> Nodes { get; init; } = Array.Empty<NumVector2>();
    /// <summary>The handle under the cursor or being dragged: drawn bigger, a knob in the accent colour.</summary>
    public NumVector2? HotPoint { get; init; }
    public bool HotIsKnob { get; init; }
    /// <summary>The old shape while dragging (a faint outline until release, as in the storyboard).</summary>
    public IReadOnlyList<EditEdge> Ghosts { get; init; } = Array.Empty<EditEdge>();
    /// <summary>A moved point's way from where it was (an accent arrow).</summary>
    public (NumVector2 From, NumVector2 To)? Move { get; init; }
    public SnapResult? Snap { get; init; }
    /// <summary>The drag's tag (<c>+22 m</c>, <c>R 50 → 140 m</c>) and how it's styled.</summary>
    public FlashTag? DragTag { get; init; }
    public bool DragTagWarn { get; init; }
    public IReadOnlyList<Issue> Issues { get; init; } = Array.Empty<Issue>();
    public Severity? Worst { get; init; }
    public IReadOnlyList<FlashTag> Flashes { get; init; } = Array.Empty<FlashTag>();
    public Vector2 Mouse { get; init; }
    /// <summary>The mouse hints: key, then what it does now.</summary>
    public IReadOnlyList<(string Key, string Text)> Hints { get; init; } = Array.Empty<(string, string)>();
}

public partial class SplineOverlay
{
    private EditFrame? _edit;

    /// <summary>Shows the Edit tool's frame (null hides it) and redraws.</summary>
    public void ShowEdit(EditFrame? frame)
    {
        _edit = frame;
        _frame = null;
        QueueRedraw();
    }

    /// <summary>
    /// The Edit tool's view (storyboard → Editing after the fact): the old shape as a faint outline while dragging,
    /// every node as a disc, and each selected edge outlined with its tangent legs dashed thin, a dot at each corner
    /// point, and a ring knob in the middle of each arc (a square at a hard corner). The handle under the cursor is
    /// drawn bigger; the knob being dragged turns accent blue.
    /// </summary>
    private void DrawEdit(EditFrame f)
    {
        var ghost = Line with { A = 0.35f };
        foreach (var g in f.Ghosts) Outline(g, ghost, ThinWidth);
        if (f.HoverEdge is { } hover) Outline(hover, Line with { A = 0.7f }, 1.5f);

        var outline = f.Worst == Severity.Invalid ? Bad : f.Worst == Severity.Warn ? Warn : Line with { A = 0.9f };
        foreach (var e in f.Selected)
        {
            Outline(e, outline, ThinWidth);
            DashedPolyline(e.Alignment.Pis.Select(p => p.Position).ToList(), Line with { A = 0.6f }, 1.5f, 6f, 5f);
        }

        foreach (var n in f.Nodes) GroundDisc(n, 5f, Line with { A = 0.85f }, outline: true);

        foreach (var e in f.Selected)
        {
            var a = e.Alignment;
            for (int i = 1; i < a.Pis.Count - 1; i++)
            {
                var at = a.Pis[i].Position;
                var c = a.Corner(i);
                if (ScreenOf(at) is { } d) { DrawCircle(d, 3.5f, Shadow, true, -1, true); DrawCircle(d, 2.5f, Line, true, -1, true); }
                if (a.Pis[i].Hard || c.Radius <= 0)
                {
                    if (a.Pis[i].Hard && ScreenOf(at) is { } sq)
                    {
                        var r = new Rect2(sq - new Vector2(4, 4), new Vector2(8, 8));
                        DrawRect(r.Grow(1), Shadow);
                        DrawRect(r, Line);
                    }
                    continue;
                }
                GroundDisc(c.Mid, 6f, TagBg);
                GroundRing(c.Mid, 6f, Line, 2.5f);
            }
        }

        if (f.HotPoint is { } hot)
        {
            if (f.HotIsKnob)
            {
                GroundDisc(hot, 8f, TagBg);
                GroundRing(hot, 8f, Accent, 3f);
            }
            else
            {
                GroundDisc(hot, 9f, Line, outline: true);
            }
        }

        if (f.Move is { } mv && NumVector2.Distance(mv.From, mv.To) > 0.5f) Arrow(mv.From, mv.To);
        if (f.Snap is { } snap)
        {
            foreach (var g in snap.Guides) DashedPolyline(g.Points, Line, GuideWidth, GuideDash, GuideGap);
            DrawSnapMarker(snap);
            if (snap.Tag.Length > 0 && ScreenOf(snap.TagAt) is { } st)
                _tags.Add(new PendingTag(st + new Vector2(16, -30), snap.Tag, TagStyle.Snap, false, null));
        }

        foreach (var issue in f.Issues)
            if (ScreenOf(issue.Where) is { } at)
                _tags.Add(new PendingTag(at + new Vector2(14, 16), issue.Message, issue.Severity == Severity.Invalid ? TagStyle.Bad : TagStyle.Warn, false, null));
        if (f.DragTag is { } dt && ScreenOf(dt.At) is { } dta)
            _tags.Add(new PendingTag(dta + new Vector2(16, -12), dt.Text, dt.Bad ? TagStyle.Bad : f.DragTagWarn ? TagStyle.Warn : TagStyle.Plain, false, null));
        foreach (var flash in f.Flashes)
            if (ScreenOf(flash.At) is { } fa)
                _tags.Add(new PendingTag(fa + new Vector2(14, 14), flash.Text, flash.Bad ? TagStyle.Bad : TagStyle.Plain, false, null));

        var hintAt = f.Mouse + new Vector2(26, 10);
        foreach (var (key, text) in f.Hints) _tags.Add(new PendingTag(hintAt, text, TagStyle.Plain, false, key));
    }

    /// <summary>Both sides of an edge's corridor.</summary>
    private void Outline(EditEdge e, Color color, float width)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var edge = e.Alignment.Curve.Offset(side * e.Width / 2f);
            if (edge.Length > 0) SolidPolyline(edge.SampleEvery(2f).Select(x => x.Sample.Position).ToList(), color, width);
        }
    }

    /// <summary>An accent arrow on the ground from one point to another, its head in pixels.</summary>
    private void Arrow(NumVector2 from, NumVector2 to)
    {
        if (ScreenOf(from) is not { } a || ScreenOf(to) is not { } b || a.DistanceTo(b) < 8f) return;
        var d = (b - a).Normalized();
        var tip = b - d * 10f;
        ShadowLine(a, tip, Accent, 1.5f);
        var n = d.Orthogonal() * 4f;
        DrawColoredPolygon(new[] { tip + d * 7f, tip + n, tip - n }, Accent);
    }
}
