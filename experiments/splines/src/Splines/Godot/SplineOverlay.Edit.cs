using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>A built edge as the Edit tool shows it: its alignment and corridor width.</summary>
public readonly record struct EditEdge(Alignment Alignment, float Width);

/// <summary>One action round the radial menu: its label, its direction from the centre (degrees, screen space), and
/// whether it applies; a <see cref="Danger"/> action (Delete) is red.</summary>
public readonly record struct RadialItem(string Label, float Angle, bool Enabled, bool Danger);

/// <summary>The open radial menu: its centre on screen, its actions, and the hovered one.</summary>
public sealed record RadialMenu(Vector2 Centre, IReadOnlyList<RadialItem> Items, int? Hovered);

/// <summary>A kerb control on a selected junction: where it is, the track it can slide along, whether it's under the
/// cursor or held, whether it's been set, and whether it's a kerb's knob (else a road handle).</summary>
public readonly record struct KerbMark(NumVector2 At, IReadOnlyList<NumVector2> Track, bool Hot, bool Set, bool Knob);

/// <summary>What the Edit tool shows this frame, handed to <see cref="SplineOverlay.ShowEdit"/> (plan space).</summary>
public sealed class EditFrame
{
    /// <summary>Selected edges: outlined, with their tangent legs, corner points and radius knobs.</summary>
    public IReadOnlyList<EditEdge> Selected { get; init; } = Array.Empty<EditEdge>();
    /// <summary>Selected stretches of roads with corners: outlined like a selected edge, from dot to dot.</summary>
    public IReadOnlyList<EditEdge> Stretches { get; init; } = Array.Empty<EditEdge>();
    /// <summary>Corners whose handles show besides the selected edges' (a selected stretch's two).</summary>
    public IReadOnlyList<(Alignment Alignment, int Index)> Corners { get; init; } = Array.Empty<(Alignment, int)>();
    /// <summary>Selected nodes: an accent ring.</summary>
    public IReadOnlyList<NumVector2> SelectedNodes { get; init; } = Array.Empty<NumVector2>();
    /// <summary>The edge a click would select (outlined thinner).</summary>
    public EditEdge? HoverEdge { get; init; }
    /// <summary>A box select in progress (screen space) and what it would take.</summary>
    public Rect2? Box { get; init; }
    public IReadOnlyList<EditEdge> BoxEdges { get; init; } = Array.Empty<EditEdge>();
    public IReadOnlyList<NumVector2> BoxNodes { get; init; } = Array.Empty<NumVector2>();
    public RadialMenu? Menu { get; init; }
    /// <summary>Each corner's point on the road (a joint, an arc's middle): a small white dot, as while drawing.</summary>
    public IReadOnlyList<NumVector2> Points { get; init; } = Array.Empty<NumVector2>();
    /// <summary>Every node, a white disc.</summary>
    public IReadOnlyList<NumVector2> Nodes { get; init; } = Array.Empty<NumVector2>();
    /// <summary>The handle under the cursor or being dragged: drawn bigger, a knob in the accent colour.</summary>
    public NumVector2? HotPoint { get; init; }
    public bool HotIsKnob { get; init; }
    /// <summary>The old shape while dragging (a faint outline until release, as in the storyboard).</summary>
    public IReadOnlyList<EditEdge> Ghosts { get; init; } = Array.Empty<EditEdge>();
    /// <summary>The selected junctions' kerb handles, and the held junction's kerbs as built while one is dragged.</summary>
    public IReadOnlyList<KerbMark> KerbHandles { get; init; } = Array.Empty<KerbMark>();
    public IReadOnlyList<IReadOnlyList<NumVector2>> KerbGhosts { get; init; } = Array.Empty<IReadOnlyList<NumVector2>>();
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
    /// <summary>A selected corner's handles: a dot at its point, and a ring knob in the middle of its arc (a square
    /// at a hard corner).</summary>
    private void CornerHandles(Alignment a, int i)
    {
        var at = a.Pis[i].Position;
        var c = a.Corner(i);
        if (ScreenOf(at) is { } d) { DrawCircle(d, 3.5f, Rim, true, -1, true); DrawCircle(d, 2.5f, Line, true, -1, true); }
        if (a.Pis[i].Hard || c.Radius <= 0)
        {
            if (a.Pis[i].Hard && ScreenOf(at) is { } sq)
            {
                var r = new Rect2(sq - new Vector2(4, 4), new Vector2(8, 8));
                DrawRect(r.Grow(1), Rim);
                DrawRect(r, Line);
            }
            return;
        }
        GroundDisc(c.Mid, 6f, TagBg);
        GroundRing(c.Mid, 6f, Line, 2.5f);
    }

    /// <summary>A kerb control (docs/kerb-handles.html): its track as a soft white band with a dot at each end; a road
    /// handle a white disc ringed in the accent colour, a kerb's knob a dark disc ringed in white like a bend's radius
    /// knob; bigger and accent-ringed when under the cursor or held; a set one has an accent centre.</summary>
    private void KerbHandle(KerbMark k)
    {
        if (k.Track.Count > 1)
        {
            SolidPolyline(k.Track, Line with { A = k.Hot ? 0.55f : 0.3f }, 4f);
            GroundDisc(k.Track[0], 2.5f, Line with { A = 0.8f });
            GroundDisc(k.Track[^1], 2.5f, Line with { A = 0.8f });
        }
        float px = k.Hot ? 8f : 6f;
        GroundDisc(k.At, px, k.Knob ? TagBg : Line, outline: true);
        GroundRing(k.At, px, k.Hot || !k.Knob ? Accent : Line, k.Hot ? 3f : 2f);
        if (k.Set) GroundDisc(k.At, 2.5f, Accent);
    }

    private void DrawEdit(EditFrame f)
    {
        var ghost = Line with { A = 0.35f };
        foreach (var g in f.Ghosts) Outline(g, ghost, ThinWidth);
        if (f.HoverEdge is { } hover) Outline(hover, Line with { A = 0.7f }, 1.5f);
        foreach (var e in f.BoxEdges) Outline(e, Accent, 1.5f);

        var outline = f.Worst == Severity.Invalid ? Bad : f.Worst == Severity.Warn ? Warn : Line with { A = 0.9f };
        foreach (var e in f.Selected)
        {
            Outline(e, outline, ThinWidth);
            DashedPolyline(e.Alignment.Pis.Select(p => p.Position).ToList(), Line with { A = 0.6f }, 1.5f, 6f, 5f);
        }
        foreach (var e in f.Stretches) Outline(e, outline, ThinWidth);
        foreach (var (a, i) in f.Corners)
            DashedPolyline(new[] { a.Pis[i - 1].Position, a.Pis[i].Position, a.Pis[i + 1].Position }, Line with { A = 0.6f }, 1.5f, 6f, 5f);

        foreach (var p in f.Points) GroundDisc(p, 4f, Line with { A = 0.75f });
        foreach (var n in f.Nodes) GroundDisc(n, 5f, Line with { A = 0.85f }, outline: true);
        foreach (var n in f.BoxNodes) GroundRing(n, 8f, Accent with { A = 0.7f }, 2f);
        foreach (var n in f.SelectedNodes) GroundRing(n, 8f, Accent, 2.5f);

        foreach (var k in f.KerbGhosts) SolidPolyline(k, ghost, ThinWidth);
        foreach (var k in f.KerbHandles) KerbHandle(k);

        foreach (var e in f.Selected)
            for (int i = 1; i < e.Alignment.Pis.Count - 1; i++) CornerHandles(e.Alignment, i);
        foreach (var (a, i) in f.Corners) CornerHandles(a, i);

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

        if (f.Box is { } box)
        {
            DrawRect(box, Accent with { A = 0.12f });
            DrawRect(box, Accent, false, 1.5f);
        }
        if (f.Menu is { } menu) DrawRadial(menu);

        // Beside the menu when it's open, so they don't cover its actions.
        var hintAt = f.Menu is { } open ? open.Centre + new Vector2(RadialRadius + 12, -12) : f.Mouse + new Vector2(26, 10);
        foreach (var (key, text) in f.Hints) _tags.Add(new PendingTag(hintAt, text, TagStyle.Plain, false, key));
    }

    private const float RadialRadius = 60f, RadialInner = 16f, RadialLabel = 36f;

    /// <summary>The radial menu (storyboard → Right-click a node): a dark disc round the point, an action in each
    /// quarter, the hovered quarter lit in the accent colour, Delete in red, and actions that don't apply greyed.</summary>
    private void DrawRadial(RadialMenu m)
    {
        var c = m.Centre;
        DrawCircle(c, RadialRadius + 2, Rim, true, -1, true);
        DrawCircle(c, RadialRadius, TagBg, true, -1, true);
        DrawArc(c, RadialRadius, 0, Mathf.Tau, 64, Line with { A = 0.25f }, 1f, true);
        for (int i = 0; i < m.Items.Count; i++)
        {
            var item = m.Items[i];
            float a = Mathf.DegToRad(item.Angle);
            if (m.Hovered == i && item.Enabled)
            {
                // The quarter, from the inner ring out.
                var wedge = new List<Vector2>();
                for (int k = 0; k <= 12; k++) wedge.Add(c + Vector2.FromAngle(a - Mathf.Pi / 4 + k * Mathf.Pi / 24) * (RadialRadius - 2));
                for (int k = 12; k >= 0; k--) wedge.Add(c + Vector2.FromAngle(a - Mathf.Pi / 4 + k * Mathf.Pi / 24) * RadialInner);
                DrawColoredPolygon(wedge.ToArray(), (item.Danger ? Bad : Accent) with { A = 0.28f });
            }
            var color = !item.Enabled ? Line with { A = 0.3f } : item.Danger ? Bad : m.Hovered == i ? Accent : TagText;
            var size = _font.GetStringSize(item.Label, HorizontalAlignment.Left, -1, FontSize);
            var at = c + Vector2.FromAngle(a) * RadialLabel;
            DrawString(_font, at + new Vector2(-size.X / 2, FontSize / 2f - 2), item.Label, HorizontalAlignment.Left, -1, FontSize, color);
        }
        DrawCircle(c, 6f, Rim, true, -1, true);
        DrawCircle(c, 5f, Line, true, -1, true);
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
        ScreenLine(a, tip, Accent, 1.5f);
        var n = d.Orthogonal() * 4f;
        DrawColoredPolygon(new[] { tip + d * 7f, tip + n, tip - n }, Accent);
    }
}
