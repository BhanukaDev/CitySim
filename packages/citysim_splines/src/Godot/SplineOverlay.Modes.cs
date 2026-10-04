using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>The Freehand and Grid modes' feedback (S6); Curve mode's is the Draw preview with a bend.</summary>
public partial class SplineOverlay
{
    /// <summary>Freehand: the raw stroke dotted, the fitted line's outline, a dot where each bend's arc meets the
    /// stroke, discs at the ends, and the <c>60 samples → 5 points</c> tag at the end.</summary>
    private void DrawStroke(OverlayFrame f, IReadOnlyList<NumVector2> stroke)
    {
        DashedPolyline(stroke, Line with { A = 0.8f }, ThinWidth, 1.5f, 4f);
        if (f.Preview is not { } fit) return;
        var outline = f.Worst == Severity.Invalid ? Bad : f.Worst == Severity.Warn ? Warn : Line with { A = 0.9f };
        if (f.Rules is { Width: > 0.5f } rules)
            foreach (float side in new[] { -1f, 1f })
                SolidPolyline(fit.Curve.SampleEvery(2f).Select(x => x.Sample.Position + SplineMath.Left(x.Sample.Tangent) * (side * rules.Width / 2f)).ToList(),
                    outline, ThinWidth);
        int first = f.LeadIn ? 1 : 0, last = fit.Pis.Count - (f.LeadOut ? 2 : 1);
        for (int i = first + 1; i < last; i++)
            if (ScreenOf(fit.Corner(i).Mid) is { } d) { DrawCircle(d, 4.5f, Rim, true, -1, true); DrawCircle(d, 3.5f, Line, true, -1, true); }
        GroundDisc(fit.Pis[first].Position, 6f, Line, outline: true);
        GroundDisc(fit.Pis[last].Position, 6f, Line, outline: true);
        if (f.StrokeLabel is { } label && ScreenOf(fit.Pis[last].Position) is { } at)
            _tags.Add(new PendingTag(at + new Vector2(16, 14), label, TagStyle.Snap, false, null));
    }

    /// <summary>Grid: each block's clear area tinted (green in whole lots, amber with a part lot, red under the
    /// smallest block) and labelled in its middle (<c>64 × 48 m · 8 × 6 lots</c>, just the lots on a small block,
    /// none on a tiny one), the three clicks as discs, the outline dashed, and the tag (<c>3 × 2 blocks · …</c>) at
    /// the far corner. The roads themselves are the network's trial.</summary>
    private void DrawGrid(OverlayFrame f, GridLayout g)
    {
        for (int col = 0; col < g.Cols; col++)
            for (int row = 0; row < g.Rows; row++)
            {
                var corners = g.BlockCorners(col, row).Select(ScreenOf).ToList();
                if (corners.Any(c => c is null)) continue;
                var quad = corners.Select(c => c!.Value).ToArray();
                var (along, across) = g.BlockSize(col, row);
                bool bad = MathF.Min(along, across) < GridLayout.MinLots * g.Lot - 0.01f;
                bool whole = g.Whole(along) && g.Whole(across);
                var tint = bad ? Bad : whole ? Good : Warn;
                if (along > 0 && across > 0) DrawColoredPolygon(quad, tint with { A = 0.22f });
                float wide = quad[0].DistanceTo(quad[1]), tall = quad[0].DistanceTo(quad[3]);
                if (MathF.Min(wide, tall) < 22 || wide < 44) continue;
                var mid = (quad[0] + quad[2]) / 2;
                var style = bad ? TagStyle.Bad : whole ? TagStyle.Plain : TagStyle.Warn;
                _tags.Add(new PendingTag(mid, g.BlockLabel(col, row, f.Rules?.SnapUnitName ?? "lot", brief: wide < 230), style, true, null));
            }
        var a = g.Point(0, 0);
        var b = g.Point(g.Cols, 0);
        var c = g.Point(g.Cols, g.Rows);
        var d = g.Point(0, g.Rows);
        var outline = f.Worst == Severity.Invalid ? Bad : f.Worst == Severity.Warn ? Warn : Line;
        DashedPolyline(new[] { a, b, c, d, a }, outline, ThinWidth, 8f, 6f);
        foreach (var p in new[] { a, b, c }) GroundDisc(p, 6f, Line, outline: true);
        if (f.GridLabel is { } label && ScreenOf(c) is { } at)
            _tags.Add(new PendingTag(at + new Vector2(16, 14), label, TagStyle.Snap, false, null));
    }
}
