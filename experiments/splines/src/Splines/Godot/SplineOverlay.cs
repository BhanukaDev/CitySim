using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>What the Draw tool shows this frame, handed to <see cref="SplineOverlay"/> (plan space, map metres).</summary>
public sealed class OverlayFrame
{
    public IReadOnlyList<Pi> SessionPis { get; init; } = Array.Empty<Pi>();
    /// <summary>The placed PIs plus the snapped cursor point; null before the first click.</summary>
    public Alignment? Preview { get; init; }
    /// <summary>The draw continues a dead end at its start / its cursor end: <see cref="Preview"/> then has the old
    /// road's leg before the first point / after the cursor, drawn as a leg but with no length pill.</summary>
    public bool LeadIn { get; init; }
    public bool LeadOut { get; init; }
    /// <summary>A click here also finishes the draw (the cursor is on a dead end it continues).</summary>
    public bool ClickFinishes { get; init; }
    public SnapResult? Snap { get; init; }
    public NumVector2? StartHeading { get; init; }
    /// <summary>The draw starts / ends on a dead end it doesn't continue (another profile, say a narrower road): the
    /// direction from that point along the old road, so the joint's angle is drawn between the two roads.</summary>
    public NumVector2? StartArm { get; init; }
    public NumVector2? EndArm { get; init; }
    /// <summary>The draw ends on the side of a road (a T drawn into it): that road's direction there.</summary>
    public NumVector2? EndHeading { get; init; }
    public ProfileRules? Rules { get; init; }
    public IReadOnlyList<NumVector2> BuiltEnds { get; init; } = Array.Empty<NumVector2>();
    /// <summary>Where the mouse is on screen (the hint stack sits next to it).</summary>
    public Vector2 Mouse { get; init; }
    /// <summary>Ctrl steps are on: the fan of step spokes is drawn around the leg's start.</summary>
    public float CtrlStepDegrees { get; init; }
    public bool HardRefused { get; init; }
    /// <summary>Tags that stay a moment after an action ("Total 334 m" and the junctions made, after a finish; a red
    /// refusal).</summary>
    public IReadOnlyList<FlashTag> Flashes { get; init; } = Array.Empty<FlashTag>();
    /// <summary>The junctions the draw would make, each with a dashed ring and its tag (<c>T-junction · 90°</c>).</summary>
    public IReadOnlyList<JunctionMark> Junctions { get; init; } = Array.Empty<JunctionMark>();
    /// <summary>What's wrong with the draw, tagged where it is (amber or red).</summary>
    public IReadOnlyList<Issue> Issues { get; init; } = Array.Empty<Issue>();
    /// <summary>The draw's worst issue: the preview's outline turns amber or red with it.</summary>
    public Severity? Worst { get; init; }
    /// <summary>A too-sharp junction angle, drawn as an amber arc between its two arms.</summary>
    public IReadOnlyList<(NumVector2 Vertex, NumVector2 A, NumVector2 B)> SharpAngles { get; init; } = Array.Empty<(NumVector2, NumVector2, NumVector2)>();
    /// <summary>The legal alternative offered for a refused branch (a turnout), and its tag.</summary>
    public Alignment? Suggestion { get; init; }
    public string? SuggestionLabel { get; init; }
    /// <summary>The built edge a Delete would remove (no draw in progress), outlined red.</summary>
    public Curve? DeleteTarget { get; init; }
    public float DeleteWidth { get; init; }
}

/// <summary>A tag left on screen for a moment after an action.</summary>
public readonly record struct FlashTag(NumVector2 At, string Text, bool Bad = false);

/// <summary>A junction the draw makes: where, its tag, the ring's radius in metres, and whether it's amber.</summary>
public readonly record struct JunctionMark(NumVector2 At, string Label, float Radius, bool Warn);

/// <summary>
/// The Draw tool's feedback, in a Cities: Skylines 2 style (DESIGN.md → Feedback → Overlay): thick white dashed legs
/// between the points, a white outline along the preview's edges, white node discs lying on the ground, an angle arc
/// drawn between the two lines at every corner with a small dark pill next to it (<c>∡ 97°</c>), a length pill in the
/// middle of every leg (<c>↔ 59 m</c>), thick white dashed guides with rings where they catch, and a stack of mouse
/// hints next to the cursor. Rings, discs and arcs are laid on the ground plane, so they follow the camera's
/// perspective; line widths and text stay the same size in pixels at every zoom. Plan points are draped on the
/// ground and projected through <see cref="Project"/>.
/// </summary>
public partial class SplineOverlay : Control
{
    public static readonly Color Accent = new("#6A9CF2");
    public static readonly Color Warn = new("#E5A430");
    public static readonly Color Bad = new("#E7654F");
    public static readonly Color Line = new(1f, 1f, 1f, 0.95f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.3f);
    private static readonly Color TagBg = new(0.07f, 0.08f, 0.10f, 0.86f);
    private static readonly Color TagText = new(1f, 1f, 1f);

    private const int FontSize = 13;
    private const float LegWidth = 4f, LegDash = 12f, LegGap = 9f;
    private const float GuideWidth = 3f, GuideDash = 9f, GuideGap = 7f;
    private const float ThinWidth = 2f;
    /// <summary>Radius of a corner's angle arc, in pixels on the ground.</summary>
    private const float ArcPx = 44f;
    private const float GlyphWidth = 20f;

    private enum TagStyle { Plain, Snap, Warn, Bad }

    private readonly record struct PendingTag(Vector2 At, string Text, TagStyle Style, bool Centered, string? Key);

    private readonly SystemFont _font = new()
    {
        FontNames = new[] { "SF Pro Text", "Helvetica Neue", "Inter", "Segoe UI", "Noto Sans", "sans-serif" },
        FontWeight = 600,
    };
    private readonly Dictionary<TagStyle, StyleBoxFlat> _tagBoxes = new();
    private readonly List<Rect2> _placedTags = new();
    private readonly List<PendingTag> _tags = new();
    private OverlayFrame? _frame;

    /// <summary>Plan point → screen position, or null when it's behind the camera.</summary>
    public Func<NumVector2, Vector2?>? Project { get; set; }

    public SplineOverlay()
    {
        Name = "SplineOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var (style, border) in new[] { (TagStyle.Plain, Colors.Transparent), (TagStyle.Snap, Accent), (TagStyle.Warn, Warn), (TagStyle.Bad, Bad) })
        {
            var box = new StyleBoxFlat { BgColor = TagBg, BorderColor = border, AntiAliasing = true };
            box.SetCornerRadiusAll(5);
            box.SetBorderWidthAll(style == TagStyle.Plain ? 0 : 1);
            _tagBoxes[style] = box;
        }
    }

    /// <summary>Shows a frame (null hides everything) and redraws.</summary>
    public void Show(OverlayFrame? frame)
    {
        _frame = frame;
        _edit = null;
        QueueRedraw();
    }

    public override void _Draw()
    {
        _placedTags.Clear();
        _tags.Clear();
        if (_edit is { } ef && Project is not null)
        {
            DrawEdit(ef);
            foreach (var t in _tags) Tag(t);
            return;
        }
        if (_frame is not { } f || Project is null) return;

        foreach (var end in f.BuiltEnds) GroundDisc(end, 4f, Line with { A = 0.75f });
        if (f.DeleteTarget is { } del) DrawDeleteTarget(del, f.DeleteWidth);
        if (f.Suggestion is { } sug) DrawSuggestion(sug, f.SuggestionLabel);

        var snap = f.Snap;
        bool drawing = f.Preview is not null && f.SessionPis.Count > 0;
        if (drawing && f.CtrlStepDegrees > 0) CtrlFan(f.SessionPis[^1].Position, f.CtrlStepDegrees);
        if (drawing) DrawPreview(f, f.Preview!, snap);
        if (snap is not null) DrawGuides(f, snap);
        if (snap is not null) DrawSnapMarker(snap);
        if (drawing) DrawJunctionsAndIssues(f);

        if (snap is { Tag.Length: > 0, Kind: not (SnapKind.Angle or SnapKind.CtrlAngle) } s && ScreenOf(s.TagAt) is { } tagAt)
            _tags.Add(new PendingTag(tagAt + new Vector2(16, -30), s.Tag, TagStyle.Snap, false, null));
        foreach (var flash in f.Flashes)
            if (ScreenOf(flash.At) is { } fa)
                _tags.Add(new PendingTag(fa + new Vector2(14, 14), flash.Text, flash.Bad ? TagStyle.Bad : TagStyle.Plain, false, null));
        if (f.HardRefused)
            _tags.Add(new PendingTag(f.Mouse + new Vector2(24, -34), "Hard corners not allowed", TagStyle.Bad, false, null));
        Hints(f);

        // Tags last, so they sit on top of every line.
        foreach (var t in _tags) Tag(t);
    }

    // --- The draw in progress ---

    private void DrawPreview(OverlayFrame f, Alignment preview, SnapResult? snap)
    {
        var pis = preview.Pis;

        // The cursor's point, and the first drawn one (the old road's leg sits before or after them when continuing).
        int cur = pis.Count - (f.LeadOut ? 2 : 1), drawnFrom = f.LeadIn ? 1 : 0;
        // Only the new part is outlined: from where the continued joint's corner starts to where the end one's ends.
        float s0 = f.LeadIn ? preview.CornerStations(1).Start : 0;
        float s1 = f.LeadOut ? preview.CornerStations(pis.Count - 2).End : preview.Length;

        // The ribbon's outline along both edges: amber with a warning, red when refused.
        if (f.Rules is { Width: > 0.5f } rules && s1 > s0)
        {
            var outline = f.Worst == Severity.Invalid ? Bad : f.Worst == Severity.Warn ? Warn : Line with { A = 0.9f };
            int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 2f));
            foreach (float side in new[] { -1f, 1f })
            {
                var pts = new List<NumVector2>(n + 1);
                for (int k = 0; k <= n; k++)
                {
                    var sample = preview.Curve.Sample(s0 + (s1 - s0) * k / n);
                    pts.Add(sample.Position + SplineMath.Left(sample.Tangent) * (side * rules.Width / 2f));
                }
                SolidPolyline(pts, outline, ThinWidth);
            }
        }

        // The legs: thick white dashes from point to point (the tangent polygon the corners round off), the new ones only.
        DashedPolyline(pis.Skip(drawnFrom).Take(cur - drawnFrom + 1).Select(p => p.Position).ToList(), Line, LegWidth, LegDash, LegGap);

        // Corners: the angle arc between the two legs and its pill; the radius knob, or a square at a hard corner.
        int live = pis.Count - 2;
        for (int i = 1; i < pis.Count - 1; i++)
        {
            var at = pis[i].Position;
            var u = pis[i - 1].Position - at;
            var v = pis[i + 1].Position - at;
            if (u.Length() < SplineMath.Epsilon || v.Length() < SplineMath.Epsilon) continue;
            // A lock against the start road shows on the continued joint, where that road is the previous leg.
            var lk = snap?.Angle is { } a && (a.Against == AngleReference.Leg || a.Against == AngleReference.StartEdge && f.LeadIn && i == 1)
                && NumVector2.Distance(a.Vertex, at) < 1e-3f ? a : (AngleLock?)null;
            // The joint with a continued road gets arms: the old road's leg isn't dashed.
            bool joint = f.LeadIn && i == 1 || f.LeadOut && i == pis.Count - 2;
            // Straight on: the 180° mark, and no radius to show.
            if (Straight(u, v))
            {
                AngleWithPill(at, u, v, lk, arms: joint);
                continue;
            }
            var c = preview.Corner(i);
            // The live corner's pill also carries the radius it builds.
            string? radius = i != live || pis[i].Hard || c.Radius <= 0 ? null : $"R {c.Radius:0} m";
            AngleWithPill(at, u, v, lk, radius, arms: joint);

            if (pis[i].Hard || c.Radius <= 0)
            {
                if (ScreenOf(at) is { } sq)
                {
                    var r = new Rect2(sq - new Vector2(4, 4), new Vector2(8, 8));
                    DrawRect(r.Grow(1), Shadow);
                    DrawRect(r, Line);
                }
                if (i == live && ScreenOf(at) is { } hs)
                    _tags.Add(new PendingTag(hs + new Vector2(16, 18), "Hard corner", TagStyle.Plain, false, "Alt"));
                continue;
            }
            GroundDisc(c.Mid, 6f, TagBg);
            GroundRing(c.Mid, 6f, Line, 2.5f);
        }

        // The first leg against the road the draw started on: the old road itself at a dead end it joins, else the
        // edge's line on the smaller-angle side (a branch).
        var first = pis.Count >= 2 ? pis[1].Position - pis[0].Position : NumVector2.Zero;
        if (!f.LeadIn && first.Length() > SplineMath.Epsilon)
        {
            var lk = snap?.Angle is { Against: AngleReference.StartEdge } a && NumVector2.Distance(a.Vertex, pis[0].Position) < 1e-3f ? a : (AngleLock?)null;
            if (f.StartArm is { } arm) AngleWithPill(pis[0].Position, arm, first, lk, arms: true);
            else if (f.StartHeading is { } h && h.LengthSquared() > SplineMath.Epsilon)
                AngleWithPill(pis[0].Position, SideOf(h, first), first, lk, arms: true);
        }

        // The same at the cursor end, when it lands on a road it doesn't continue (a T drawn into it, or a joint).
        var last = pis.Count >= 2 ? pis[cur - 1].Position - pis[cur].Position : NumVector2.Zero;
        if (!f.LeadOut && last.Length() > SplineMath.Epsilon)
        {
            if (f.EndArm is { } arm) AngleWithPill(pis[cur].Position, arm, last, null, arms: true);
            else if (f.EndHeading is { } h && h.LengthSquared() > SplineMath.Epsilon)
                AngleWithPill(pis[cur].Position, SideOf(h, last), last, null, arms: true);
        }

        // Nodes: a disc at the start and at the cursor end, a small dot at each corner point.
        GroundDisc(pis[drawnFrom].Position, 6f, Line, outline: true);
        GroundDisc(pis[cur].Position, 6f, Line, outline: true);
        for (int i = 1; i < pis.Count - 1; i++)
            if (ScreenOf(pis[i].Position) is { } d) { DrawCircle(d, 3.5f, Shadow, true, -1, true); DrawCircle(d, 2.5f, Line, true, -1, true); }

        // Length pills in the middle of every leg; the current one says when it's whole steps or equal to another leg.
        int? steps = LegSteps(f, pis[cur].Position);
        for (int j = drawnFrom; j < cur; j++)
        {
            var a = pis[j].Position;
            var b = pis[j + 1].Position;
            float len = NumVector2.Distance(a, b);
            if (len < 0.5f || ScreenOf((a + b) / 2) is not { } m) continue;
            bool current = j == cur - 1;
            string text = $"↔ {len:0} m";
            var style = TagStyle.Plain;
            if (current && snap?.EqualLength is { } eq) { text = $"↔ = {eq.Length:0} m"; style = TagStyle.Snap; }
            else if (current && steps is { } n && f.Rules is { } r) { text = $"↔ {len:0} m · {n} × {r.SnapLength:0.#} m"; style = TagStyle.Snap; }
            _tags.Add(new PendingTag(m, text, style, true, null));
        }

        // Ticks: every snap step along the current leg, and a bold tick on both legs of an equal-length match.
        if (f.Rules is { SnapLength: > 0 } rl && steps is { } count && count <= 80)
        {
            var a = f.SessionPis[^1].Position;
            var dir = NumVector2.Normalize(pis[cur].Position - a);
            for (int k = 1; k < count; k++) Tick(a + dir * (k * rl.SnapLength), dir, 4f, Line with { A = 0.55f }, 1f);
        }
        if (snap?.EqualLength is { } e)
        {
            var a = f.SessionPis[^1].Position;
            var b = pis[cur].Position;
            Tick((a + b) / 2, NumVector2.Normalize(b - a), 10f, Line, 3f);
            Tick((e.A + e.B) / 2, NumVector2.Normalize(e.B - e.A), 10f, Line, 3f);
            if (!pis.Zip(pis.Skip(1)).Any(p => NumVector2.Distance(p.First.Position, e.A) < 1e-3f && NumVector2.Distance(p.Second.Position, e.B) < 1e-3f)
                && ScreenOf((e.A + e.B) / 2) is { } em)
                _tags.Add(new PendingTag(em, $"↔ {e.Length:0} m", TagStyle.Snap, true, null));
        }
    }

    /// <summary>The arc between two directions at a vertex (the angle between the lines, drawn from them) and a pill
    /// just outside it: <c>∡ 97°</c>, or <c>∡ 90° · square</c> in the snap style when it's the locked angle. With
    /// <paramref name="arms"/>, short white lines run out from the vertex along both directions, for a road that has
    /// no dashed leg of its own there. Straight on, a rectangle stands on the line instead of the arc (<c>∡ 180°</c>).</summary>
    private void AngleWithPill(NumVector2 vertex, NumVector2 dirA, NumVector2 dirB, AngleLock? locked, string? suffix = null,
        bool warn = false, bool arms = false)
    {
        var a = NumVector2.Normalize(dirA);
        var b = NumVector2.Normalize(dirB);
        float deg = MathF.Acos(Math.Clamp(NumVector2.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        if (PxPerMetre(vertex) is not { } k) return;
        float r = ArcPx / k;
        bool straight = Straight(a, b);
        if (arms)
            foreach (var d in new[] { a, b }) SolidPolyline(new[] { vertex, vertex + d * r * 1.5f }, Line, ThinWidth + 0.5f);
        if (straight)
        {
            var n = SplineMath.Left(b);
            float w = r * 0.45f, h = r * 0.4f;
            SolidPolyline(new[] { vertex - b * w, vertex - b * w + n * h, vertex + b * w + n * h, vertex + b * w }, Line, ThinWidth);
        }
        else GroundArc(vertex, a, b, r, Line, ThinWidth);

        var bis = a + b;
        bis = !straight ? NumVector2.Normalize(bis) : SplineMath.Left(b);
        if (ScreenOf(vertex + bis * r * 1.7f) is not { } at) return;
        string text = locked is { } lk ? $"∡ {deg:0}° · {lk.Meaning}" : straight ? "∡ 180° · straight" : $"∡ {deg:0}°";
        if (suffix is not null) text += $" · {suffix}";
        var style = warn ? TagStyle.Warn : locked is null ? TagStyle.Plain : TagStyle.Snap;
        _tags.Add(new PendingTag(at, text, style, true, null));
    }

    /// <summary>Two directions from a vertex that carry on in one line (a 180° joint).</summary>
    private static bool Straight(NumVector2 u, NumVector2 v) => NumVector2.Dot(NumVector2.Normalize(u), NumVector2.Normalize(v)) < -0.99996f;

    /// <summary>A road's line through a point, pointed to the side of <paramref name="leg"/>: the smaller angle.</summary>
    private static NumVector2 SideOf(NumVector2 heading, NumVector2 leg)
    {
        var edge = NumVector2.Normalize(heading);
        return NumVector2.Dot(edge, leg) < 0 ? -edge : edge;
    }

    /// <summary>How many whole snap steps the current leg is, if it's (to the centimetre) a whole number.</summary>
    private static int? LegSteps(OverlayFrame f, NumVector2 cursor)
    {
        if (f.Rules is not { SnapLength: > 0 } r || f.SessionPis.Count == 0) return null;
        float len = NumVector2.Distance(f.SessionPis[^1].Position, cursor);
        int n = (int)MathF.Round(len / r.SnapLength);
        return n > 0 && MathF.Abs(n * r.SnapLength - len) < 1e-2f ? n : null;
    }

    // --- Junctions, issues, suggestions ---

    /// <summary>A dashed ring round each junction the draw makes, with its tag above right (the storyboard's
    /// <c>T-junction · 90°</c>), then the issues: an amber arc in a too-sharp junction, and each issue's tag where it
    /// is. A clamped radius is left out: the live corner's pill already says it.</summary>
    private void DrawJunctionsAndIssues(OverlayFrame f)
    {
        foreach (var j in f.Junctions)
        {
            var color = j.Warn ? Warn : Line;
            GroundCircleMetres(j.At, j.Radius, color, ThinWidth, dashed: true);
            if (ScreenOf(j.At + new NumVector2(j.Radius, -j.Radius) * 0.75f) is { } at)
                _tags.Add(new PendingTag(at + new Vector2(8, -28), j.Label, j.Warn ? TagStyle.Warn : TagStyle.Snap, false, null));
        }
        foreach (var (v, a, b) in f.SharpAngles)
        {
            if (PxPerMetre(v) is not { } k) continue;
            GroundArc(v, a, b, ArcPx * 1.3f / k, Warn, ThinWidth + 0.5f);
            GroundRing(v, 6f, Warn, 2.5f);
        }
        foreach (var issue in f.Issues)
        {
            if (issue.Code == "radius-clamped" || ScreenOf(issue.Where) is not { } at) continue;
            var style = issue.Severity == Severity.Invalid ? TagStyle.Bad : TagStyle.Warn;
            _tags.Add(new PendingTag(at + new Vector2(14, 16), issue.Message, style, false, null));
        }
    }

    /// <summary>The offered alternative: an accent dashed centre line and its tag at its corner.</summary>
    private void DrawSuggestion(Alignment a, string? label)
    {
        var pts = a.Curve.SampleEvery(2f).Select(x => x.Sample.Position).ToList();
        DashedPolyline(pts, Accent, GuideWidth, GuideDash, GuideGap);
        if (label is not null && a.Pis.Count >= 3 && ScreenOf(a.Corner(1).Mid) is { } at)
            _tags.Add(new PendingTag(at + new Vector2(14, 16), label, TagStyle.Snap, false, null));
    }

    private void DrawDeleteTarget(Curve c, float width)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var edge = c.Offset(side * width / 2f);
            if (edge.Length > 0) SolidPolyline(edge.SampleEvery(2f).Select(x => x.Sample.Position).ToList(), Bad, ThinWidth + 1f);
        }
    }

    // --- Guides and snaps ---

    private void DrawGuides(OverlayFrame f, SnapResult snap)
    {
        foreach (var g in snap.Guides)
        {
            DashedPolyline(g.Points, Line, GuideWidth, GuideDash, GuideGap);
            if (g.Kind == GuideKind.Perpendicular) SquareMark(g.Source, g.EdgeDirection, NumVector2.Normalize(g.Points[^1] - g.Points[0]), 12f);
            if (g is { Kind: GuideKind.Parallel, Along: { } along, Gap: > 0.01f }) GapBracket(g, along, snap.Position);
        }

        // A lock the corner arcs don't already show: against the start edge on a later leg, or Ctrl's steps.
        if (snap.Angle is not { } lk || f.SessionPis.Count == 0) return;
        if (lk.Against == AngleReference.Absolute)
        {
            if (ScreenOf(lk.Vertex) is { } v)
                _tags.Add(new PendingTag(v + new Vector2(18, 16), SnapEngine.AngleTag(lk), TagStyle.Snap, false, null));
            return;
        }
        bool shownAtStart = lk.Against == AngleReference.StartEdge && f.SessionPis.Count == 1;
        if (lk.Against == AngleReference.StartEdge && !shownAtStart)
        {
            DashedPolyline(new[] { lk.Vertex, lk.Vertex + lk.ReferenceDirection * 1000f }, Line with { A = 0.6f }, ThinWidth, 5f, 5f, maxPixels: 70f);
            AngleWithPill(lk.Vertex, lk.ReferenceDirection, lk.Direction, lk);
        }
    }

    private void DrawSnapMarker(SnapResult snap)
    {
        switch (snap.Kind)
        {
            case SnapKind.Node:
                GroundRing(snap.Position, 13f, Line, 3f);
                break;
            case SnapKind.GuideCrossing:
                GroundRing(snap.Position, 7f, Line, 2.5f);
                GroundRing(snap.Position, 13f, Line, 2.5f);
                break;
            case SnapKind.PerpendicularFoot:
                GroundRing(snap.Position, 10f, Line, 3f);
                break;
            case SnapKind.Edge:
            case SnapKind.GuideSingle:
                GroundRing(snap.Position, 8f, Line, 3f);
                break;
        }
    }

    private void GapBracket(GuideLine g, Curve along, NumVector2 at)
    {
        var cp = along.ClosestPoint(at);
        var n = SplineMath.Left(along.Sample(cp.S).Tangent) * g.Side;
        var a = cp.Position + n * g.EdgeHalfWidth;
        var b = a + n * g.Gap;
        if (ScreenOf(a) is not { } sa || ScreenOf(b) is not { } sb) return;
        var cap = (sb - sa).Normalized().Orthogonal() * 6f;
        foreach (var (p, q) in new[] { (sa, sb), (sa - cap, sa + cap), (sb - cap, sb + cap) }) ShadowLine(p, q, Line, ThinWidth);
    }

    private void CtrlFan(NumVector2 at, float stepDegrees)
    {
        if (ScreenOf(at) is not { } c) return;
        for (float d = 0; d < 360f; d += stepDegrees)
        {
            bool major = MathF.Abs(d % 45f) < 0.01f;
            float rad = -d * MathF.PI / 180f; // screen y points down: counter-clockwise headings go up
            DrawLine(c, c + new Vector2(MathF.Cos(rad), MathF.Sin(rad)) * (major ? 80f : 60f), Line with { A = major ? 0.55f : 0.25f }, major ? 1.5f : 1f, true);
        }
    }

    /// <summary>Mouse hints stacked next to the cursor, like CS2's: what each button does right now.</summary>
    private void Hints(OverlayFrame f)
    {
        var at = f.Mouse + new Vector2(26, -10);
        _tags.Add(new PendingTag(at, f.Suggestion is not null ? "Use turnout" : f.ClickFinishes ? "Place and finish" : "Place", TagStyle.Plain, false, "LMB"));
        if (f.SessionPis.Count == 0)
        {
            if (f.DeleteTarget is not null) _tags.Add(new PendingTag(at, "Delete", TagStyle.Plain, false, "Del"));
            return;
        }
        _tags.Add(new PendingTag(at, "Stop", TagStyle.Plain, false, "RMB"));
        if (f.SessionPis.Count >= 2)
        {
            _tags.Add(new PendingTag(at, "Finish", TagStyle.Plain, false, "Double-click"));
            _tags.Add(new PendingTag(at, "Undo leg", TagStyle.Plain, false, "Ctrl+Z"));
        }
        if (f.Preview is { } p && p.Pis.Count >= 3 && !p.Pis[^2].Hard) _tags.Add(new PendingTag(at, "Radius", TagStyle.Plain, false, "Shift+wheel"));
    }

    // --- Ground-plane primitives (they follow the perspective) ---

    /// <summary>Screen pixels per metre on the ground at a point, or null off screen.</summary>
    private float? PxPerMetre(NumVector2 at)
    {
        if (ScreenOf(at) is not { } o || ScreenOf(at + NumVector2.UnitX) is not { } x || ScreenOf(at + NumVector2.UnitY) is not { } y) return null;
        float k = (o.DistanceTo(x) + o.DistanceTo(y)) / 2f;
        return k > 1e-4f ? k : null;
    }

    private Vector2[]? GroundCircle(NumVector2 centre, float px, int n = 32)
    {
        if (PxPerMetre(centre) is not { } k) return null;
        float r = px / k;
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            if (ScreenOf(centre + SplineMath.Direction(i * MathF.Tau / n) * r) is not { } p) return null;
            pts[i] = p;
        }
        return pts;
    }

    /// <summary>A ring <paramref name="radius"/> metres round a point, solid or dashed.</summary>
    private void GroundCircleMetres(NumVector2 centre, float radius, Color color, float width, bool dashed)
    {
        const int n = 48;
        var pts = Enumerable.Range(0, n + 1).Select(i => centre + SplineMath.Direction(i * MathF.Tau / n) * radius).ToList();
        if (dashed) DashedPolyline(pts, color, width, 6f, 5f);
        else SolidPolyline(pts, color, width);
    }

    private void GroundRing(NumVector2 centre, float px, Color color, float width)
    {
        if (GroundCircle(centre, px) is not { } pts) return;
        DrawPolyline(pts, Shadow, width + 2f, true);
        DrawPolyline(pts, color, width, true);
    }

    private void GroundDisc(NumVector2 centre, float px, Color color, bool outline = false)
    {
        if (GroundCircle(centre, px) is not { } pts) return;
        // A disc seen edge-on (or projected by the headless dummy camera) can come out degenerate; skip it rather than
        // have Godot log a failed triangulation every frame.
        var poly = pts[..^1];
        if (Geometry2D.TriangulatePolygon(poly).Length == 0) return;
        if (outline) DrawPolyline(pts, Shadow, 2f, true);
        DrawColoredPolygon(poly, color);
    }

    /// <summary>An arc of radius <paramref name="r"/> metres at a vertex, from one direction to another the short way.</summary>
    private void GroundArc(NumVector2 vertex, NumVector2 a, NumVector2 b, float r, Color color, float width)
    {
        float a0 = SplineMath.Angle(a);
        float sweep = SplineMath.Wrap(SplineMath.Angle(b) - a0);
        const int n = 24;
        var pts = new List<Vector2>(n + 1);
        for (int i = 0; i <= n; i++)
            if (ScreenOf(vertex + SplineMath.Direction(a0 + sweep * i / n) * r) is { } p) pts.Add(p);
        if (pts.Count < 2) return;
        DrawPolyline(pts.ToArray(), Shadow, width + 2f, true);
        DrawPolyline(pts.ToArray(), color, width, true);
    }

    /// <summary>A right-angle mark at <paramref name="at"/> between two plan directions, <paramref name="px"/> on a side.</summary>
    private void SquareMark(NumVector2 at, NumVector2 dirA, NumVector2 dirB, float px)
    {
        if (PxPerMetre(at) is not { } k) return;
        float m = px / k;
        var a = NumVector2.Normalize(dirA) * m;
        var b = NumVector2.Normalize(dirB) * m;
        var pts = new[] { at + a, at + a + b, at + b }.Select(ScreenOf).ToList();
        if (pts.Any(p => p is null)) return;
        var arr = pts.Select(p => p!.Value).ToArray();
        DrawPolyline(arr, Shadow, ThinWidth + 2f, true);
        DrawPolyline(arr, Line, ThinWidth, true);
    }

    private void Tick(NumVector2 at, NumVector2 along, float halfPx, Color color, float width)
    {
        if (ScreenOf(at) is not { } p || ScreenDir(at, along) is not { } d) return;
        var n = d.Orthogonal() * halfPx;
        ShadowLine(p - n, p + n, color, width);
    }

    // --- Screen-space lines ---

    private void ShadowLine(Vector2 a, Vector2 b, Color color, float width)
    {
        DrawLine(a, b, Shadow, width + 2f, true);
        DrawLine(a, b, color, width, true);
    }

    private void SolidPolyline(IReadOnlyList<NumVector2> points, Color color, float width)
    {
        var pts = points.Select(ScreenOf).Where(p => p is not null).Select(p => p!.Value).ToArray();
        if (pts.Length < 2) return;
        DrawPolyline(pts, Shadow, width + 2f, true);
        DrawPolyline(pts, color, width, true);
    }

    /// <summary>A dashed line through plan points, dashed in screen pixels (the phase carries across points, so a
    /// finely sampled arc still dashes evenly), each dash with a soft shadow. <paramref name="maxPixels"/> stops it
    /// after that many pixels.</summary>
    private void DashedPolyline(IReadOnlyList<NumVector2> points, Color color, float width, float dash, float gap, float maxPixels = float.PositiveInfinity)
    {
        float phase = 0, total = 0;
        Vector2? prev = null;
        foreach (var pt in points)
        {
            if (ScreenOf(pt) is not { } cur) { prev = null; continue; }
            if (prev is { } a)
            {
                float segLen = a.DistanceTo(cur);
                if (total + segLen > maxPixels) { cur = a + (cur - a) * ((maxPixels - total) / segLen); segLen = maxPixels - total; }
                var dir = segLen > 1e-3f ? (cur - a) / segLen : Vector2.Zero;
                float t = 0;
                while (t < segLen)
                {
                    float inPhase = phase % (dash + gap);
                    bool on = inPhase < dash;
                    float step = MathF.Min((on ? dash : dash + gap) - inPhase, segLen - t);
                    if (on) ShadowLine(a + dir * t, a + dir * (t + step), color, width);
                    t += step;
                    phase += step;
                }
                total += segLen;
                if (total >= maxPixels) return;
            }
            prev = cur;
        }
    }

    // --- Tags ---

    /// <summary>A dark rounded pill with white text, placed at (or centred on) its anchor, nudged down past any tag
    /// already placed this frame and kept on screen. A <see cref="PendingTag.Key"/> is drawn first in the accent colour
    /// ("LMB Place"). <c>∡</c> and <c>↔</c> in the text are drawn as small symbols at text height.</summary>
    private void Tag(PendingTag t)
    {
        float keyWidth = t.Key is null ? 0 : _font.GetStringSize(t.Key, HorizontalAlignment.Left, -1, FontSize).X + 7;
        var size = new Vector2(TextWidth(t.Text) + keyWidth + 14, FontSize + 10);
        var box = new Rect2(t.Centered ? t.At - size / 2 : t.At, size);
        for (int guard = 0; guard < 10 && _placedTags.Exists(r => r.Grow(1).Intersects(box)); guard++)
            box.Position += new Vector2(0, box.Size.Y + 3);
        var view = GetViewportRect().Size;
        // Kept on screen (the max guards a viewport smaller than the tag, e.g. headless).
        box.Position = new Vector2(Math.Clamp(box.Position.X, 4, MathF.Max(4, view.X - box.Size.X - 4)),
            Math.Clamp(box.Position.Y, 70, MathF.Max(70, view.Y - box.Size.Y - 4)));
        _placedTags.Add(box);
        DrawStyleBox(_tagBoxes[t.Style], box);

        var textColor = t.Style switch { TagStyle.Warn => Warn, TagStyle.Bad => Bad, _ => TagText };
        var pen = box.Position + new Vector2(7, FontSize + 3);
        if (t.Key is not null)
        {
            DrawString(_font, pen, t.Key, HorizontalAlignment.Left, -1, FontSize, Accent);
            pen.X += keyWidth;
        }
        DrawText(pen, t.Text, t.Style == TagStyle.Snap ? Accent : textColor, textColor);
    }

    private float TextWidth(string text)
    {
        float w = 0;
        foreach (var (part, glyph) in Split(text))
            w += glyph is null ? _font.GetStringSize(part, HorizontalAlignment.Left, -1, FontSize).X : GlyphWidth;
        return w;
    }

    private void DrawText(Vector2 pen, string text, Color glyphColor, Color color)
    {
        foreach (var (part, glyph) in Split(text))
        {
            if (glyph is { } g)
            {
                Glyph(pen, g, glyphColor);
                pen.X += GlyphWidth;
                continue;
            }
            DrawString(_font, pen, part, HorizontalAlignment.Left, -1, FontSize, color);
            pen.X += _font.GetStringSize(part, HorizontalAlignment.Left, -1, FontSize).X;
        }
    }

    /// <summary>The text cut into plain runs and the symbols drawn by hand (∡, ↔).</summary>
    private static IEnumerable<(string Part, char? Glyph)> Split(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('∡' or '↔')) continue;
            if (i > start) yield return (text[start..i], null);
            yield return ("", text[i]);
            start = i + 1;
        }
        if (start < text.Length) yield return (text[start..], null);
    }

    private void Glyph(Vector2 baseline, char glyph, Color color)
    {
        float h = FontSize - 3;
        var o = baseline + new Vector2(1, -1);
        float w = GlyphWidth - 6;
        if (glyph == '∡')
        {
            var arm = new Vector2(MathF.Cos(-MathF.PI / 3), MathF.Sin(-MathF.PI / 3));
            DrawLine(o, o + new Vector2(h, 0), color, 1.5f, true);
            DrawLine(o, o + arm * (h + 1), color, 1.5f, true);
            DrawArc(o, h * 0.6f, -MathF.PI / 3, 0, 8, color, 1.5f, true);
        }
        else
        {
            var mid = o + new Vector2(0, -h / 2 + 1);
            var end = mid + new Vector2(w, 0);
            DrawLine(mid, end, color, 1.5f, true);
            foreach (var (tip, s) in new[] { (mid, 1f), (end, -1f) })
            {
                DrawLine(tip, tip + new Vector2(3.5f * s, -3f), color, 1.5f, true);
                DrawLine(tip, tip + new Vector2(3.5f * s, 3f), color, 1.5f, true);
            }
        }
    }

    private Vector2? ScreenOf(NumVector2 plan) => Project?.Invoke(plan);

    /// <summary>A plan direction at a plan point, as a unit screen direction.</summary>
    private Vector2? ScreenDir(NumVector2 at, NumVector2 dir)
    {
        if (dir.LengthSquared() < 1e-8f) return null;
        if (ScreenOf(at) is not { } a || ScreenOf(at + NumVector2.Normalize(dir)) is not { } b) return null;
        var d = b - a;
        return d.LengthSquared() > 1e-8f ? d.Normalized() : null;
    }
}
