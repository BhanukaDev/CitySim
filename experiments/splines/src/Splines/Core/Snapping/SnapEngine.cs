using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Resolves one frame's snapped cursor position (DESIGN.md → Snapping and guides): existing node, then existing
/// edge, then a guide crossing, then a single guide (extension, node alignment, parallel, perpendicular), then an
/// angle lock (soft 90°/45°, or Ctrl's absolute 15°/5° steps, which override the guides and the soft angle), then
/// a length step or equal length. First hit wins; <see cref="SnapQuery.Disabled"/> (Space) skips every level.
/// Pure and stateless — candidates are the caller's already-built alignments, so this needs no graph (S4) and
/// stays engine-agnostic and checkable headlessly (<c>--demo-snap</c>).
/// </summary>
public static class SnapEngine
{
    private const float SoftAngleTolerance = 6f * MathF.PI / 180f;

    public static SnapResult Evaluate(SnapQuery q)
    {
        if (q.Disabled) return SnapResult.None(q.Cursor);

        var providers = q.Rules.SnapProviders & q.EnabledProviders;

        if (providers.HasFlag(SnapProviders.Node) && TryNode(q, out var nodeHit)) return nodeHit;
        if (providers.HasFlag(SnapProviders.Edge) && TryEdge(q, out var edgeHit)) return edgeHit;

        var guides = GatherGuides(q, providers);

        if (providers.HasFlag(SnapProviders.Crossing) && guides.Count == 2 &&
            TryGuideCrossing(q, guides[0], guides[1], out var crossHit)) return crossHit;

        if (guides.Count > 0) return SingleGuideResult(q, guides);

        return q.SessionPis.Count > 0 ? AngleAndLength(q, providers) : SnapResult.None(q.Cursor);
    }

    // --- Levels 1-2: node, edge ---

    private static bool TryNode(SnapQuery q, out SnapResult result)
    {
        Vector2 best = default;
        float bestDist = float.PositiveInfinity;
        bool found = false;
        foreach (var c in q.Candidates)
        {
            if (c.Alignment.Pis.Count == 0) continue;
            float radius = MathF.Max(q.CatchDistance, c.Width / 2f);
            foreach (var pi in Ends(c.Alignment))
            {
                float d = Vector2.Distance(q.Cursor, pi);
                if (d <= radius && d < bestDist) { bestDist = d; best = pi; found = true; }
            }
        }
        result = found ? new SnapResult(best, SnapKind.Node, "node", Array.Empty<GuideLine>(), null) : default;
        return found;
    }

    private static bool TryEdge(SnapQuery q, out SnapResult result)
    {
        CurvePoint? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var c in q.Candidates)
        {
            if (c.Alignment.Curve.Length <= 0) continue;
            var cp = c.Alignment.Curve.ClosestPoint(q.Cursor);
            float d = Vector2.Distance(cp.Position, q.Cursor);
            float radius = MathF.Max(q.CatchDistance, c.Width / 2f);
            if (d <= radius && d < bestDist) { bestDist = d; best = cp; }
        }
        if (best is { } b) { result = new SnapResult(b.Position, SnapKind.Edge, "edge", Array.Empty<GuideLine>(), null); return true; }
        result = default;
        return false;
    }

    /// <summary>The two ends of an alignment, stood in for graph nodes until S4.</summary>
    private static IEnumerable<Vector2> Ends(Alignment a)
    {
        yield return a.Pis[0].Position;
        if (a.Pis.Count > 1) yield return a.Pis[^1].Position;
    }

    // --- Levels 3-4: guide crossing, single guide ---

    private static List<GuideLine> GatherGuides(SnapQuery q, SnapProviders providers)
    {
        var found = new List<(GuideLine Guide, float Dist)>();

        foreach (var c in q.Candidates)
        {
            if (c.Alignment.Curve.Length <= 0) continue;
            if (providers.HasFlag(SnapProviders.Extension))
            {
                TryAddExtension(found, q, c.Alignment.Curve, atStart: true);
                TryAddExtension(found, q, c.Alignment.Curve, atStart: false);
            }
            if (providers.HasFlag(SnapProviders.Perpendicular) && q.SessionPis.Count > 0)
                TryAddPerpendicular(found, q, c.Alignment.Curve, q.SessionPis[^1].Position);
            if (providers.HasFlag(SnapProviders.Parallel)) TryAddParallel(found, q, c);
            if (providers.HasFlag(SnapProviders.NodeAlign)) TryAddNodeAlign(found, q, c);
        }

        found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var result = new List<GuideLine>();
        foreach (var (guide, dist) in found)
        {
            if (dist > q.CatchDistance) break;
            result.Add(guide);
            if (result.Count == 2) break;
        }
        return result;
    }

    private static void TryAddExtension(List<(GuideLine, float)> found, SnapQuery q, Curve curve, bool atStart)
    {
        var end = atStart ? curve.Sample(0) : curve.Sample(curve.Length);
        if (Vector2.Distance(end.Position, q.Cursor) > q.GuideSearchRadius) return;
        var dir = atStart ? -end.Tangent : end.Tangent;
        if (dir.LengthSquared() < SplineMath.Epsilon) return;
        dir = Vector2.Normalize(dir);
        float dist = DistanceToRay(end.Position, dir, q.Cursor);
        var guide = new GuideLine(new[] { end.Position, end.Position + dir * q.GuideSearchRadius }, GuideKind.Extension, "extension");
        found.Add((guide, dist));
    }

    /// <summary>A candidate heading for the current leg, square to a nearby edge, anchored at the leg's start
    /// point (like a "perpendicular from here" osnap) — not re-derived from the live cursor, or the guide would
    /// always pass exactly through it (distance always zero) and crowd out every other guide.</summary>
    private static void TryAddPerpendicular(List<(GuideLine, float)> found, SnapQuery q, Curve curve, Vector2 anchor)
    {
        var cp = curve.ClosestPoint(q.Cursor); // which part of a (possibly curved) edge is "nearby"
        if (MathF.Abs(cp.Offset) > q.GuideSearchRadius) return;
        var dir = SplineMath.Left(curve.Sample(cp.S).Tangent);
        if (dir.LengthSquared() < SplineMath.Epsilon) return;
        dir = Vector2.Normalize(dir);
        float dist = DistanceToLine(anchor, dir, q.Cursor);
        var guide = new GuideLine(
            new[] { anchor - dir * q.GuideSearchRadius, anchor + dir * q.GuideSearchRadius },
            GuideKind.Perpendicular, "90° to edge");
        found.Add((guide, dist));
    }

    private static void TryAddParallel(List<(GuideLine, float)> found, SnapQuery q, SnapCandidate c)
    {
        float halfWidths = c.Width / 2f + q.Rules.Width / 2f;
        for (int side = -1; side <= 1; side += 2)
        {
            for (int k = 0; k <= 2; k++)
            {
                float gap = k * q.Rules.SnapLength;
                float d = side * (halfWidths + gap);
                var offset = c.Alignment.Curve.Offset(d);
                if (offset.Length <= 0) continue;
                var cp = offset.ClosestPoint(q.Cursor);
                float dist = Vector2.Distance(cp.Position, q.Cursor);
                if (dist > q.GuideSearchRadius) continue;
                // A fixed, tight spacing (not derived from the whole curve's length): SampleEvery divides each
                // segment independently, so a short, sharply-curved arc segment needs its own fine spacing to stay
                // visually and numerically concentric — a spacing derived from the total length starves it.
                var points = offset.SampleEvery(2f).Select(s => s.Sample.Position).ToList();
                var guide = new GuideLine(points, GuideKind.Parallel, $"parallel · {gap:0.#} m gap");
                found.Add((guide, dist));
            }
        }
    }

    private static void TryAddNodeAlign(List<(GuideLine, float)> found, SnapQuery q, SnapCandidate c)
    {
        if (c.Alignment.Curve.Length <= 0) return;
        var refDir = ReferenceHeading(q);
        var startTangent = c.Alignment.Curve.Sample(0).Tangent;
        var endTangent = c.Alignment.Curve.Sample(c.Alignment.Curve.Length).Tangent;
        var nodes = new[]
        {
            (Pos: c.Alignment.Curve.Sample(0).Position, EdgeDir: startTangent),
            (Pos: c.Alignment.Curve.Sample(c.Alignment.Curve.Length).Position, EdgeDir: -endTangent),
        };
        string label = string.IsNullOrEmpty(c.Label) ? "node" : $"{c.Label} node";
        foreach (var (pos, edgeDir) in nodes)
        {
            if (Vector2.Distance(pos, q.Cursor) > q.GuideSearchRadius) continue;
            var dirs = new List<Vector2> { edgeDir, SplineMath.Left(edgeDir) };
            if (refDir is { } r) { dirs.Add(r); dirs.Add(SplineMath.Left(r)); }
            foreach (var raw in dirs)
            {
                if (raw.LengthSquared() < SplineMath.Epsilon) continue;
                var dir = Vector2.Normalize(raw);
                float dist = DistanceToLine(pos, dir, q.Cursor);
                var guide = new GuideLine(
                    new[] { pos - dir * q.GuideSearchRadius, pos + dir * q.GuideSearchRadius },
                    GuideKind.NodeAlign, $"aligned · {label}");
                found.Add((guide, dist));
            }
        }
    }

    private static bool TryGuideCrossing(SnapQuery q, GuideLine a, GuideLine b, out SnapResult result)
    {
        var (pa, da) = LineOf(a);
        var (pb, db) = LineOf(b);
        float den = SplineMath.Cross(da, db);
        if (MathF.Abs(den) < 1e-6f) { result = default; return false; }
        float t = SplineMath.Cross(pb - pa, db) / den;
        var point = pa + da * t;
        if (Vector2.Distance(point, q.Cursor) > q.CatchDistance) { result = default; return false; }
        result = new SnapResult(point, SnapKind.GuideCrossing, $"{a.Tag} × {b.Tag}", new[] { a, b }, null);
        return true;
    }

    private static SnapResult SingleGuideResult(SnapQuery q, List<GuideLine> guides)
    {
        var top = guides[0];
        return new SnapResult(ClosestPointOnGuide(top, q.Cursor), SnapKind.GuideSingle, top.Tag, guides, null);
    }

    // --- Levels 5-7: angle, length ---

    private static SnapResult AngleAndLength(SnapQuery q, SnapProviders providers)
    {
        var last = q.SessionPis[^1].Position;
        var toCursor = q.Cursor - last;
        float rawLen = toCursor.Length();
        if (rawLen < SplineMath.Epsilon) return SnapResult.None(q.Cursor);

        Vector2? lockedDir = null;
        var angleKind = SnapKind.None;
        string angleTag = "";

        if (q.CtrlSteps && providers.HasFlag(SnapProviders.Angle))
        {
            float step = (q.FineSteps ? 5f : 15f) * (MathF.PI / 180f);
            float snapped = MathF.Round(SplineMath.Angle(toCursor) / step) * step;
            lockedDir = SplineMath.Direction(snapped);
            angleKind = SnapKind.CtrlAngle;
            angleTag = $"{snapped * 180f / MathF.PI:0.#}°";
        }
        else if (providers.HasFlag(SnapProviders.Angle) && ReferenceHeading(q) is { } refDir)
        {
            float turn = SplineMath.Turn(refDir, toCursor);
            float abs = MathF.Abs(turn);
            foreach (float target in new[] { MathF.PI / 2f, MathF.PI / 4f })
            {
                if (MathF.Abs(abs - target) > SoftAngleTolerance) continue;
                lockedDir = Rotate(refDir, MathF.Sign(turn) * target);
                angleKind = SnapKind.Angle;
                angleTag = $"{target * 180f / MathF.PI:0}°";
                break;
            }
        }

        var dir = lockedDir ?? Vector2.Normalize(toCursor);
        var length = ApplyLengthSnap(q, providers, rawLen);

        if (angleKind == SnapKind.None && length.Kind == SnapKind.None)
            return SnapResult.None(q.Cursor) with { EqualLengthTickStation = length.Tick };

        var kind = angleKind != SnapKind.None ? angleKind : length.Kind;
        string tag = angleKind != SnapKind.None
            ? (length.Kind != SnapKind.None ? $"{angleTag} · {length.Tag}" : angleTag)
            : length.Tag;

        return new SnapResult(last + dir * length.Length, kind, tag, Array.Empty<GuideLine>(), length.Tick);
    }

    private readonly record struct LengthMatch(float Length, string Tag, SnapKind Kind, float? Tick);

    private static LengthMatch ApplyLengthSnap(SnapQuery q, SnapProviders providers, float rawLen)
    {
        float? stepLen = null;
        float stepDist = float.PositiveInfinity;
        if (providers.HasFlag(SnapProviders.Length) && q.Rules.SnapLength > SplineMath.Epsilon)
        {
            float candidate = MathF.Round(rawLen / q.Rules.SnapLength) * q.Rules.SnapLength;
            stepLen = candidate;
            stepDist = MathF.Abs(candidate - rawLen);
        }

        float? equalLen = null;
        float equalDist = float.PositiveInfinity;
        if (providers.HasFlag(SnapProviders.EqualLength))
        {
            foreach (float candidate in EqualLengthCandidates(q))
            {
                float d = MathF.Abs(candidate - rawLen);
                if (d < equalDist) { equalDist = d; equalLen = candidate; }
            }
        }

        float? tick = equalLen is not null && equalDist <= q.CatchDistance ? equalLen : null;

        bool equalWins = equalLen is not null && equalDist <= q.CatchDistance && equalDist <= stepDist;
        if (equalWins) return new LengthMatch(equalLen!.Value, $"= {equalLen.Value:0.#} m", SnapKind.EqualLength, tick);

        bool stepWins = stepLen is not null && stepDist <= q.CatchDistance;
        if (stepWins)
        {
            int n = (int)MathF.Round(stepLen!.Value / q.Rules.SnapLength);
            return new LengthMatch(stepLen.Value, $"{stepLen.Value:0.#} m · {n} × {q.Rules.SnapLength:0.#} m", SnapKind.Length, tick);
        }

        return new LengthMatch(rawLen, "", SnapKind.None, tick);
    }

    private static IEnumerable<float> EqualLengthCandidates(SnapQuery q)
    {
        if (q.SessionPis.Count >= 2)
        {
            float prevLeg = Vector2.Distance(q.SessionPis[^1].Position, q.SessionPis[^2].Position);
            if (prevLeg > SplineMath.Epsilon) yield return prevLeg;
        }
        foreach (var c in q.Candidates)
            if (c.Alignment.Curve.Length > SplineMath.Epsilon) yield return c.Alignment.Curve.Length;
    }

    private static Vector2? ReferenceHeading(SnapQuery q)
    {
        if (q.SessionPis.Count >= 2)
        {
            var d = q.SessionPis[^1].Position - q.SessionPis[^2].Position;
            return d.LengthSquared() > SplineMath.Epsilon * SplineMath.Epsilon ? Vector2.Normalize(d) : null;
        }
        return q.StartHeading is { } h && h.LengthSquared() > SplineMath.Epsilon * SplineMath.Epsilon
            ? Vector2.Normalize(h) : null;
    }

    // --- Small shared helpers ---

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float DistanceToRay(Vector2 origin, Vector2 dir, Vector2 p)
    {
        float t = Vector2.Dot(p - origin, dir);
        if (t < 0) return Vector2.Distance(origin, p);
        return Vector2.Distance(origin + dir * t, p);
    }

    private static float DistanceToLine(Vector2 origin, Vector2 dir, Vector2 p) =>
        Vector2.Distance(origin + dir * Vector2.Dot(p - origin, dir), p);

    private static Vector2 ClosestPointOnGuide(GuideLine guide, Vector2 cursor)
    {
        var best = guide.Points[0];
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i + 1 < guide.Points.Count; i++)
        {
            var a = guide.Points[i];
            var ab = guide.Points[i + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > SplineMath.Epsilon ? Math.Clamp(Vector2.Dot(cursor - a, ab) / len2, 0, 1) : 0;
            var p = a + ab * t;
            float d = Vector2.DistanceSquared(p, cursor);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    private static (Vector2 Origin, Vector2 Dir) LineOf(GuideLine guide)
    {
        var a = guide.Points[0];
        var b = guide.Points[^1];
        return (a, Vector2.Normalize(b - a));
    }
}
