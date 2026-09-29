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
    public SnapResult? Snap { get; init; }
    public NumVector2? StartHeading { get; init; }
    public ProfileRules? Rules { get; init; }
    public IReadOnlyList<NumVector2> BuiltEnds { get; init; } = Array.Empty<NumVector2>();
    /// <summary>Where the mouse is on screen (the hint stack sits next to it).</summary>
    public Vector2 Mouse { get; init; }
    /// <summary>Ctrl steps are on: the fan of step spokes is drawn around the leg's start.</summary>
    public float CtrlStepDegrees { get; init; }
    public bool HardRefused { get; init; }
    /// <summary>A tag that stays a moment after an action ("Total 334 m" after a finish).</summary>
    public (NumVector2 At, string Text)? Flash { get; init; }
}

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
        QueueRedraw();
    }

    public override void _Draw()
    {
        _placedTags.Clear();
        _tags.Clear();
        if (_frame is not { } f || Project is null) return;

        foreach (var end in f.BuiltEnds) GroundDisc(end, 4f, Line with { A = 0.75f });

        var snap = f.Snap;
        bool drawing = f.Preview is not null && f.SessionPis.Count > 0;
        if (drawing && f.CtrlStepDegrees > 0) CtrlFan(f.SessionPis[^1].Position, f.CtrlStepDegrees);
        if (drawing) DrawPreview(f, f.Preview!, snap);
        if (snap is not null) DrawGuides(f, snap);
        if (snap is not null) DrawSnapMarker(snap);

        if (snap is { Tag.Length: > 0, Kind: not (SnapKind.Angle or SnapKind.CtrlAngle) } s && ScreenOf(s.TagAt) is { } tagAt)
            _tags.Add(new PendingTag(tagAt + new Vector2(16, -30), s.Tag, TagStyle.Snap, false, null));
        if (f.Flash is { } flash && ScreenOf(flash.At) is { } fa)
            _tags.Add(new PendingTag(fa + new Vector2(14, 14), flash.Text, TagStyle.Plain, false, null));
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

        // The ribbon's outline along both edges (amber when a corner didn't fit).
        if (f.Rules is { Width: > 0.5f } rules)
        {
            var outline = preview.AnyClamped ? Warn : Line with { A = 0.9f };
            foreach (float side in new[] { -1f, 1f })
            {
                var edge = preview.Curve.Offset(side * rules.Width / 2f);
                if (edge.Length > 0) SolidPolyline(edge.SampleEvery(2f).Select(s => s.Sample.Position).ToList(), outline, ThinWidth);
            }
        }

        // The legs: thick white dashes from point to point (the tangent polygon the corners round off).
        DashedPolyline(pis.Select(p => p.Position).ToList(), Line, LegWidth, LegDash, LegGap);

        // Corners: the angle arc between the two legs and its pill; the radius knob, or a square at a hard corner.
        int live = pis.Count - 2;
        for (int i = 1; i < pis.Count - 1; i++)
        {
            var at = pis[i].Position;
            var u = pis[i - 1].Position - at;
            var v = pis[i + 1].Position - at;
            if (u.Length() < SplineMath.Epsilon || v.Length() < SplineMath.Epsilon) continue;
            var lk = snap?.Angle is { Against: AngleReference.Leg } a && NumVector2.Distance(a.Vertex, at) < 1e-3f ? a : (AngleLock?)null;
            var c = preview.Corner(i);
            // The live corner's pill also carries its radius (amber when it didn't fit).
            string? radius = i != live || pis[i].Hard || c.Radius <= 0 ? null
                : c.Clamped ? $"R {c.Radius:0} m (wants {c.Wanted:0})" : $"R {c.Radius:0} m";
            AngleWithPill(at, u, v, lk, radius, c.Clamped && i == live);

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
            var ring = c.Clamped ? Warn : Line;
            GroundDisc(c.Mid, 6f, TagBg);
            GroundRing(c.Mid, 6f, ring, 2.5f);
        }

        // The first leg against the edge the draw started on.
        if (f.StartHeading is { } h && h.LengthSquared() > SplineMath.Epsilon && pis.Count >= 2)
        {
            var first = pis[1].Position - pis[0].Position;
            if (first.Length() > SplineMath.Epsilon)
            {
                var edge = NumVector2.Normalize(h);
                if (NumVector2.Dot(edge, first) < 0) edge = -edge;
                var lk = snap?.Angle is { Against: AngleReference.StartEdge } a && NumVector2.Distance(a.Vertex, pis[0].Position) < 1e-3f ? a : (AngleLock?)null;
                AngleWithPill(pis[0].Position, edge, first, lk);
            }
        }

        // Nodes: a disc at the start and at the cursor end, a small dot at each corner point.
        GroundDisc(pis[0].Position, 6f, Line, outline: true);
        GroundDisc(pis[^1].Position, 6f, Line, outline: true);
        for (int i = 1; i < pis.Count - 1; i++)
            if (ScreenOf(pis[i].Position) is { } d) { DrawCircle(d, 3.5f, Shadow, true, -1, true); DrawCircle(d, 2.5f, Line, true, -1, true); }

        // Length pills in the middle of every leg; the current one says when it's whole steps or equal to another leg.
        int? steps = LegSteps(f, preview);
        for (int j = 0; j + 1 < pis.Count; j++)
        {
            var a = pis[j].Position;
            var b = pis[j + 1].Position;
            float len = NumVector2.Distance(a, b);
            if (len < 0.5f || ScreenOf((a + b) / 2) is not { } m) continue;
            bool current = j == pis.Count - 2;
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
            var dir = NumVector2.Normalize(pis[^1].Position - a);
            for (int k = 1; k < count; k++) Tick(a + dir * (k * rl.SnapLength), dir, 4f, Line with { A = 0.55f }, 1f);
        }
        if (snap?.EqualLength is { } e)
        {
            var a = f.SessionPis[^1].Position;
            var b = pis[^1].Position;
            Tick((a + b) / 2, NumVector2.Normalize(b - a), 10f, Line, 3f);
            Tick((e.A + e.B) / 2, NumVector2.Normalize(e.B - e.A), 10f, Line, 3f);
            if (!pis.Zip(pis.Skip(1)).Any(p => NumVector2.Distance(p.First.Position, e.A) < 1e-3f && NumVector2.Distance(p.Second.Position, e.B) < 1e-3f)
                && ScreenOf((e.A + e.B) / 2) is { } em)
                _tags.Add(new PendingTag(em, $"↔ {e.Length:0} m", TagStyle.Snap, true, null));
        }
    }

    /// <summary>The arc between two directions at a vertex (the angle between the lines, drawn from them) and a pill
    /// just outside it: <c>∡ 97°</c>, or <c>∡ 90° · square</c> in the snap style when it's the locked angle.</summary>
    private void AngleWithPill(NumVector2 vertex, NumVector2 dirA, NumVector2 dirB, AngleLock? locked, string? suffix = null, bool warn = false)
    {
        var a = NumVector2.Normalize(dirA);
        var b = NumVector2.Normalize(dirB);
        float deg = MathF.Acos(Math.Clamp(NumVector2.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        if (PxPerMetre(vertex) is not { } k) return;
        float r = ArcPx / k;
        GroundArc(vertex, a, b, r, Line, ThinWidth);

        var bis = a + b;
        bis = bis.LengthSquared() > 1e-4f ? NumVector2.Normalize(bis) : SplineMath.Left(b);
        if (ScreenOf(vertex + bis * r * 1.7f) is not { } at) return;
        string text = locked is { } lk ? $"∡ {deg:0}° · {lk.Meaning}" : $"∡ {deg:0}°";
        if (suffix is not null) text += $" · {suffix}";
        var style = warn ? TagStyle.Warn : locked is null ? TagStyle.Plain : TagStyle.Snap;
        _tags.Add(new PendingTag(at, text, style, true, null));
    }

    /// <summary>How many whole snap steps the current leg is, if it's (to the centimetre) a whole number.</summary>
    private static int? LegSteps(OverlayFrame f, Alignment preview)
    {
        if (f.Rules is not { SnapLength: > 0 } r || f.SessionPis.Count == 0) return null;
        float len = NumVector2.Distance(f.SessionPis[^1].Position, preview.Pis[^1].Position);
        int n = (int)MathF.Round(len / r.SnapLength);
        return n > 0 && MathF.Abs(n * r.SnapLength - len) < 1e-2f ? n : null;
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
        _tags.Add(new PendingTag(at, "Place", TagStyle.Plain, false, "LMB"));
        if (f.SessionPis.Count == 0) return;
        _tags.Add(new PendingTag(at, "Undo", TagStyle.Plain, false, "RMB"));
        if (f.SessionPis.Count >= 2) _tags.Add(new PendingTag(at, "Finish", TagStyle.Plain, false, "Double-click"));
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

    private void GroundRing(NumVector2 centre, float px, Color color, float width)
    {
        if (GroundCircle(centre, px) is not { } pts) return;
        DrawPolyline(pts, Shadow, width + 2f, true);
        DrawPolyline(pts, color, width, true);
    }

    private void GroundDisc(NumVector2 centre, float px, Color color, bool outline = false)
    {
        if (GroundCircle(centre, px) is not { } pts) return;
        if (outline) DrawPolyline(pts, Shadow, 2f, true);
        DrawColoredPolygon(pts[..^1], color);
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
        box.Position = new Vector2(Math.Clamp(box.Position.X, 4, view.X - box.Size.X - 4), Math.Clamp(box.Position.Y, 70, view.Y - box.Size.Y - 4));
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
