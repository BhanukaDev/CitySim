using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// Resolves one frame's snapped cursor position (DESIGN.md → Snapping and guides → Priority):
/// <list type="number">
/// <item>an existing node, then the perpendicular foot from the leg's start, then an existing edge;</item>
/// <item>a direction lock: Ctrl's absolute 15°/5° steps, or a soft square/diagonal/straight-on angle against the edge
///   the draw started on or the previous leg (whichever is closer);</item>
/// <item>with a lock, a guide (or guide crossing) only picks <i>where along</i> the locked direction the point lands
///   ("extension · ∡ 90°"), else a length step or equal length does; a guide never pulls a leg off its angle;</item>
/// <item>with no lock: a guide crossing, then a single guide, then a length step or equal length.</item>
/// </list>
/// <see cref="SnapQuery.Disabled"/> (Space) skips everything. Pure and stateless: candidates are the graph's edges,
/// passed in by the caller, and it's checked headlessly by <c>--demo-snap</c>.
/// </summary>
public static class SnapEngine
{
    private const float SoftAngleTolerance = 6f * MathF.PI / 180f;
    /// <summary>The perpendicular foot is a bigger target than a plain edge point: it's the clean T players want.</summary>
    private const float FootCatchFactor = 1.5f;
    /// <summary>A lit straight guide is drawn from its source to this many catch distances past the snapped point.</summary>
    private const float DisplayMarginFactor = 5f;
    /// <summary>Parallel guides reach this many snap steps out; further away "parallel" stops meaning "alongside".</summary>
    private const int MaxParallelSteps = 10;
    /// <summary>Parallel guides are caught at this fraction of the catch distance: there's one every snap step (8 m),
    /// so at the full catch one would be lit almost everywhere near an edge when zoomed out.</summary>
    private const float ParallelCatchFactor = 0.5f;

    public static SnapResult Evaluate(SnapQuery q)
    {
        if (q.Disabled) return SnapResult.None(q.Cursor);
        var providers = q.Rules.SnapProviders & q.EnabledProviders;
        if (q.TangentLock is { } t && q.SessionPis.Count > 0 && t.LengthSquared() > SplineMath.Epsilon) return Tangent(q, providers, Vector2.Normalize(t));

        if (providers.HasFlag(SnapProviders.Node) && TryNode(q) is { } node) return node;
        if (providers.HasFlag(SnapProviders.Perpendicular) && q.SessionPis.Count > 0 && TryFoot(q) is { } foot) return foot;
        if (providers.HasFlag(SnapProviders.Edge) && TryEdge(q) is { } edge) return edge;

        var guides = GatherGuides(q, providers);

        if (q.SessionPis.Count > 0 && DirectionLock(q, providers) is { } angle)
            return Locked(q, providers, angle, guides);

        var lit = guides.Where(g => g.Dist <= q.CatchDistance).OrderBy(g => g.Dist).Take(2).Select(g => g.Guide).ToList();
        if (lit.Count == 2 && providers.HasFlag(SnapProviders.Crossing) && TryCrossing(q, lit[0], lit[1]) is { } cross)
            return cross;
        if (lit.Count > 0)
        {
            var p = ClosestPointOnPolyline(lit[0].Points, q.Cursor);
            return new SnapResult
            {
                Position = p, Kind = SnapKind.GuideSingle, Tag = lit[0].Tag, TagAt = TagAnchor(lit[0], p),
                Guides = lit.Select(g => Trim(g, ClosestPointOnPolyline(g.Points, q.Cursor), q)).ToList(),
            };
        }

        if (q.SessionPis.Count == 0) return SnapResult.None(q.Cursor);
        var last = q.SessionPis[^1].Position;
        var toCursor = q.Cursor - last;
        if (toCursor.Length() < SplineMath.Epsilon) return SnapResult.None(q.Cursor);
        var dir = Vector2.Normalize(toCursor);
        var length = MatchLength(q, providers, toCursor.Length());
        if (length.Kind == SnapKind.None) return SnapResult.None(q.Cursor);
        return new SnapResult
        {
            Position = last + dir * length.Length, Kind = length.Kind,
            LengthSteps = length.Steps, EqualLength = length.Match,
        };
    }

    // --- Level 1-2: node, perpendicular foot, edge ---

    private static SnapResult? TryNode(SnapQuery q)
    {
        SnapResult? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var c in q.Candidates)
        {
            var curve = c.Alignment.Curve;
            if (curve.Length <= 0) continue;
            float radius = MathF.Max(q.CatchDistance, c.Width / 2f);
            foreach (var end in new[] { curve.Sample(0), curve.Sample(curve.Length) })
            {
                float d = Vector2.Distance(q.Cursor, end.Position);
                if (d > radius || d >= bestDist) continue;
                bestDist = d;
                best = new SnapResult
                {
                    Position = end.Position, Kind = SnapKind.Node, Tag = "snap: node", TagAt = end.Position,
                    EdgeTangent = end.Tangent,
                };
            }
            // Each corner's point on the road (a joint between chained curves, an arc's middle) is a node to connect to.
            for (int i = 1; i < c.Alignment.Pis.Count - 1; i++)
            {
                if (c.HiddenCorners?.Contains(i) == true) continue;
                if (q.BendSliders && c.Alignment.IsArc(i))
                {
                    if (BendSnap(q, c.Alignment, i, radius, bestDist) is { } bend) (best, bestDist) = bend;
                    continue;
                }
                var p = c.Alignment.RoadPoint(i);
                float d = Vector2.Distance(q.Cursor, p);
                if (d > radius || d >= bestDist) continue;
                bestDist = d;
                best = new SnapResult
                {
                    Position = p, Kind = SnapKind.Node, Tag = "snap: node", TagAt = p,
                    EdgeTangent = curve.Sample(curve.ClosestPoint(p).S).Tangent,
                };
            }
        }
        return best;
    }

    /// <summary>
    /// An arc corner's slider (DESIGN.md → Junctions → Corner junctions): the cursor's nearest point on the track from
    /// the road point (the built radius) out to the PI (sharp), caught within <paramref name="catchRadius"/> of the
    /// track. Both ends are magnets; in between the radius steps by 0.5 m. Null when it's not closer than
    /// <paramref name="bestDist"/>.
    /// </summary>
    private static (SnapResult, float)? BendSnap(SnapQuery q, Alignment a, int i, float catchRadius, float bestDist)
    {
        var pi = a.Pis[i].Position;
        var (inward, factor) = a.BendAxis(i);
        float built = a.EffectiveRadius(i);
        float length = built * factor;
        float along = Math.Clamp(Vector2.Dot(q.Cursor - pi, inward), 0, length);
        float d = Vector2.Distance(q.Cursor, pi + inward * along);
        if (d > catchRadius || d >= bestDist) return null;

        float magnet = MathF.Min(q.CatchDistance * 0.35f, length / 8);
        float r = along <= magnet ? 0 : length - along <= magnet ? built : MathF.Min(built, MathF.Round(a.BendRadiusAt(i, along) * 2) / 2);
        var p = a.BendPoint(i, r);
        // The road's direction at the junction: along the arc's middle, or the leg coming in at a sharp corner.
        var tangent = r > 0 ? SplineMath.Left(inward) : Vector2.Normalize(pi - a.Pis[i - 1].Position);
        string tag = r == built ? "snap: node" : r == 0 ? "corner point" : $"R {built:0} → {r:0.#} m";
        return (new SnapResult
        {
            Position = p, Kind = SnapKind.Node, Tag = tag, TagAt = p, EdgeTangent = tangent,
            Bend = new BendSlide(a, i, r, p),
        }, d);
    }

    /// <summary>The foot of the perpendicular from the current leg's start onto a nearby edge: landing there gives a
    /// T-junction at exactly 90.0°. Every straight of an edge has its own foot, not only the edge's closest point to the
    /// leg's start: a road that wraps round it (a P's end coming back up to its own first leg) is square to more than
    /// one of its straights.</summary>
    private static SnapResult? TryFoot(SnapQuery q)
    {
        var anchor = q.SessionPis[^1].Position;
        SnapResult? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var c in q.Candidates)
        foreach (var guide in Feet(c.Alignment.Curve, anchor, q))
        {
            // Caught like the edge: anywhere across the road, and within the catch along it.
            var off = q.Cursor - guide.Source;
            float along = MathF.Abs(Vector2.Dot(off, guide.EdgeDirection));
            float across = MathF.Abs(SplineMath.Cross(guide.EdgeDirection, off));
            float catchAt = q.CatchDistance * FootCatchFactor;
            if (along > catchAt || across > MathF.Max(catchAt, c.Width / 2f)) continue;
            float d = off.Length();
            if (d >= bestDist) continue;
            bestDist = d;
            best = new SnapResult
            {
                Position = guide.Source, Kind = SnapKind.PerpendicularFoot, Tag = guide.Tag, TagAt = guide.Source,
                Guides = new[] { Trim(guide, guide.Source, q) }, EdgeTangent = guide.EdgeDirection,
            };
        }
        return best;
    }

    private static SnapResult? TryEdge(SnapQuery q)
    {
        SnapResult? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var c in q.Candidates)
        {
            var curve = c.Alignment.Curve;
            if (curve.Length <= 0) continue;
            var cp = curve.ClosestPoint(q.Cursor);
            float d = Vector2.Distance(cp.Position, q.Cursor);
            if (d > MathF.Max(q.CatchDistance, c.Width / 2f) || d >= bestDist) continue;
            bestDist = d;
            best = new SnapResult
            {
                Position = cp.Position, Kind = SnapKind.Edge, Tag = "snap: edge", TagAt = cp.Position,
                EdgeTangent = curve.Sample(cp.S).Tangent,
            };
        }
        return best;
    }

    // --- Direction lock ---

    private static AngleLock? DirectionLock(SnapQuery q, SnapProviders providers)
    {
        if (!providers.HasFlag(SnapProviders.Angle)) return null;
        var last = q.SessionPis[^1].Position;
        var to = q.Cursor - last;
        if (to.Length() < SplineMath.Epsilon) return null;

        if (q.CtrlSteps)
        {
            float step = (q.FineSteps ? 5f : 15f) * (MathF.PI / 180f);
            float snapped = MathF.Round(SplineMath.Angle(to) / step) * step;
            // Plan angles grow clockwise seen from above (z points south); players read headings counter-clockwise.
            float heading = -snapped * 180f / MathF.PI;
            heading = (heading % 360f + 360f) % 360f;
            return new AngleLock(last, Vector2.UnitX, SplineMath.Direction(snapped), heading, AngleReference.Absolute, "Ctrl");
        }

        AngleLock? best = null;
        float bestErr = SoftAngleTolerance;

        if (q.SessionPis.Count >= 2)
        {
            var prev = q.SessionPis[^2].Position;
            if (Vector2.Distance(last, prev) > SplineMath.Epsilon)
            {
                var along = Vector2.Normalize(last - prev);
                float turn = SplineMath.Turn(along, to);
                // Turn targets: straight on, a diagonal bend, square, a sharp diagonal. Degrees are the angle between
                // the two edges at the corner (180 − turn).
                foreach (var (target, meaning) in new[] { (0f, "straight on"), (45f, "diagonal"), (90f, "square"), (135f, "diagonal") })
                {
                    float err = MathF.Abs(MathF.Abs(turn) - target * MathF.PI / 180f);
                    if (err > bestErr) continue;
                    bestErr = err;
                    float sign = turn < 0 ? -1f : 1f;
                    best = new AngleLock(last, -along, Rotate(along, sign * target * MathF.PI / 180f), 180f - target,
                        AngleReference.Leg, meaning);
                }
            }
        }

        if (q.StartHeading is { } h && h.LengthSquared() > SplineMath.Epsilon)
        {
            // An edge is a line, so measure against whichever of its two directions is closer to the leg.
            var edge = Vector2.Normalize(h);
            if (Vector2.Dot(edge, to) < 0) edge = -edge;
            float turn = SplineMath.Turn(edge, to);
            foreach (var (target, meaning) in new[] { (45f, "diagonal"), (90f, "square") })
            {
                float err = MathF.Abs(MathF.Abs(turn) - target * MathF.PI / 180f);
                if (err > bestErr) continue;
                bestErr = err;
                float sign = turn < 0 ? -1f : 1f;
                best = new AngleLock(last, edge, Rotate(edge, sign * target * MathF.PI / 180f), target,
                    AngleReference.StartEdge, meaning);
            }
        }
        return best;
    }

    /// <summary>With the direction locked, a guide picks the point where the locked ray meets it; otherwise the length
    /// snaps along the ray.</summary>
    private static SnapResult Locked(SnapQuery q, SnapProviders providers, AngleLock angle, List<(GuideLine Guide, float Dist)> guides)
    {
        var origin = angle.Vertex;
        var kind = angle.Against == AngleReference.Absolute ? SnapKind.CtrlAngle : SnapKind.Angle;

        GuideLine? hitGuide = null;
        Vector2 hit = default;
        float hitDist = q.CatchDistance;
        foreach (var (guide, _) in guides)
            foreach (var p in RayPolylineHits(origin, angle.Direction, guide.Points))
            {
                float d = Vector2.Distance(p, q.Cursor);
                if (d >= hitDist) continue; // ties keep the earlier guide (extension before node alignment)
                hitDist = d;
                hit = p;
                hitGuide = guide;
            }
        if (hitGuide is not null)
        {
            return new SnapResult
            {
                Position = hit, Kind = SnapKind.GuideSingle, Tag = $"{hitGuide.Tag} · {AngleText(angle)}",
                TagAt = TagAnchor(hitGuide, hit), Guides = new[] { Trim(hitGuide, hit, q) }, Angle = angle,
            };
        }

        float along = MathF.Max(Vector2.Dot(q.Cursor - origin, angle.Direction), 0f);
        var length = MatchLength(q, providers, along);
        return new SnapResult
        {
            Position = origin + angle.Direction * length.Length, Kind = kind, Tag = AngleTag(angle), TagAt = origin,
            Angle = angle, LengthSteps = length.Steps, EqualLength = length.Match,
        };
    }

    /// <summary>The leg held on the tangent ray: nodes and edges are skipped (they mean nothing there), a guide
    /// crossing the ray or a length along it still snaps, and the ray is shown as a lit guide.</summary>
    private static SnapResult Tangent(SnapQuery q, SnapProviders providers, Vector2 dir)
    {
        var last = q.SessionPis[^1].Position;
        var r = Locked(q, providers, new AngleLock(last, -dir, dir, 180f, AngleReference.Leg, "tangent"), GatherGuides(q, providers));
        var ray = new GuideLine(new[] { last, last + dir * q.GuideSearchRadius }, GuideKind.Extension, "tangent") { Source = last };
        return r with { Guides = r.Guides.Prepend(Trim(ray, r.Position, q)).ToList() };
    }

    public static string AngleText(AngleLock a) =>
        a.Against == AngleReference.Absolute ? $"{a.Degrees:0.0}°" : $"∡ {a.Degrees:0}°";

    public static string AngleTag(AngleLock a) => a.Against switch
    {
        AngleReference.Absolute => $"{a.Degrees:0.0}° · Ctrl",
        _ when a.Meaning == "straight on" => $"∡ {a.Degrees:0}° · straight on",
        _ when a.Meaning == "tangent" => "tangent",
        AngleReference.StartEdge => $"∡ {a.Degrees:0}° · {a.Meaning} to edge",
        _ => $"∡ {a.Degrees:0}° · {a.Meaning} to leg",
    };

    // --- Guides ---

    private static List<(GuideLine Guide, float Dist)> GatherGuides(SnapQuery q, SnapProviders providers)
    {
        var found = new List<(GuideLine, float)>();
        foreach (var c in q.Candidates)
        {
            var curve = c.Alignment.Curve;
            if (curve.Length <= 0) continue;
            if (providers.HasFlag(SnapProviders.Extension))
            {
                if (c.OpenStart) AddExtension(found, q, curve, atStart: true);
                if (c.OpenEnd) AddExtension(found, q, curve, atStart: false);
            }
            if (providers.HasFlag(SnapProviders.Perpendicular) && q.SessionPis.Count > 0 &&
                PerpendicularGuide(curve, q.SessionPis[^1].Position, q) is { } perp)
                found.Add((perp, DistanceToPolyline(perp.Points, q.Cursor)));
            if (providers.HasFlag(SnapProviders.Parallel)) AddParallel(found, q, c);
            if (providers.HasFlag(SnapProviders.NodeAlign)) AddNodeAlign(found, q, c);
        }
        return found;
    }

    private static void AddExtension(List<(GuideLine, float)> found, SnapQuery q, Curve curve, bool atStart)
    {
        var end = atStart ? curve.Sample(0) : curve.Sample(curve.Length);
        if (Vector2.Distance(end.Position, q.Cursor) > q.GuideSearchRadius) return;
        var dir = atStart ? -end.Tangent : end.Tangent;
        if (dir.LengthSquared() < SplineMath.Epsilon) return;
        dir = Vector2.Normalize(dir);
        var guide = new GuideLine(new[] { end.Position, end.Position + dir * q.GuideSearchRadius }, GuideKind.Extension, "extension")
        {
            Source = end.Position,
        };
        found.Add((guide, DistanceToPolyline(guide.Points, q.Cursor)));
    }

    /// <summary>The perpendicular from <paramref name="anchor"/> to the curve's closest point, and to each of its
    /// straights whose foot lies inside it.</summary>
    private static IEnumerable<GuideLine> Feet(Curve curve, Vector2 anchor, SnapQuery q)
    {
        if (PerpendicularGuide(curve, anchor, q) is { } closest) yield return closest;
        foreach (var seg in curve.Segments)
        {
            if (seg is not LineSegment line || line.Length <= SplineMath.Epsilon) continue;
            float t = Vector2.Dot(anchor - line.A, line.Direction);
            if (t <= 1e-3f || t >= line.Length - 1e-3f) continue;
            var foot = line.A + line.Direction * t;
            float off = Vector2.Distance(anchor, foot);
            if (off < 0.5f || off > q.GuideSearchRadius) continue;
            yield return Square(foot, line.Direction, anchor, q);
        }
    }

    private static GuideLine Square(Vector2 foot, Vector2 tangent, Vector2 anchor, SnapQuery q)
    {
        var up = Vector2.Normalize(anchor - foot);
        return new GuideLine(new[] { foot - up * q.GuideSearchRadius, foot + up * q.GuideSearchRadius },
            GuideKind.Perpendicular, "90° to edge")
        {
            Source = foot, EdgeDirection = tangent,
        };
    }

    /// <summary>The line square to <paramref name="curve"/> through <paramref name="anchor"/> (the leg's start), with
    /// its foot on the curve as <see cref="GuideLine.Source"/>. Anchored at the leg's start, not re-derived from the
    /// cursor, or it would always pass exactly through the cursor and crowd out every other guide. Null when the
    /// anchor is on the edge or its closest point is an end (not a true perpendicular).</summary>
    private static GuideLine? PerpendicularGuide(Curve curve, Vector2 anchor, SnapQuery q)
    {
        if (curve.Length <= 0) return null;
        var cp = curve.ClosestPoint(anchor);
        if (MathF.Abs(cp.Offset) < 0.5f || MathF.Abs(cp.Offset) > q.GuideSearchRadius) return null;
        if (cp.S <= 1e-3f || cp.S >= curve.Length - 1e-3f) return null;
        return Square(cp.Position, curve.Sample(cp.S).Tangent, anchor, q);
    }

    /// <summary>Alongside an edge at a gap of whole <see cref="ProfileRules.SnapLength"/> steps (up to
    /// <see cref="MaxParallelSteps"/>), measured between the
    /// two corridors' sides. The step is picked from the cursor's offset, so any number of lots works. It follows arcs
    /// as concentric arcs because it's the edge's exact <see cref="Curve.Offset"/>.</summary>
    private static void AddParallel(List<(GuideLine, float)> found, SnapQuery q, SnapCandidate c)
    {
        var curve = c.Alignment.Curve;
        var cp = curve.ClosestPoint(q.Cursor);
        float halfWidths = c.Width / 2f + q.Rules.Width / 2f;
        if (MathF.Abs(cp.Offset) > q.GuideSearchRadius || q.Rules.SnapLength <= SplineMath.Epsilon) return;
        float side = cp.Offset >= 0 ? 1f : -1f;
        int k = Math.Max(0, (int)MathF.Round((MathF.Abs(cp.Offset) - halfWidths) / q.Rules.SnapLength));
        if (k > MaxParallelSteps) return;
        float gap = k * q.Rules.SnapLength;
        var offset = curve.Offset(side * (halfWidths + gap));
        if (offset.Length <= 0) return;
        // A fixed 2 m spacing: SampleEvery divides each segment on its own, so a short tight arc keeps its shape.
        var points = offset.SampleEvery(2f).Select(s => s.Sample.Position).ToList();
        string unit = q.Rules.SnapUnitName;
        string steps = k > 0 && !string.IsNullOrEmpty(unit) ? $" ({k} {unit}{(k == 1 ? "" : "s")})" : "";
        var guide = new GuideLine(points, GuideKind.Parallel, $"parallel · {gap:0.#} m gap{steps}")
        {
            Source = points[0], Along = curve, Side = side, EdgeHalfWidth = c.Width / 2f, Gap = gap,
        };
        found.Add((guide, DistanceToPolyline(points, q.Cursor) / ParallelCatchFactor));
    }

    private static void AddNodeAlign(List<(GuideLine, float)> found, SnapQuery q, SnapCandidate c)
    {
        var curve = c.Alignment.Curve;
        var refDir = q.SessionPis.Count >= 2 ? Vector2.Normalize(q.SessionPis[^1].Position - q.SessionPis[^2].Position) : q.StartHeading;
        string label = string.IsNullOrEmpty(c.Label) ? "node" : $"{c.Label} node";
        foreach (var (pos, edgeDir) in new[]
                 {
                     (curve.Sample(0).Position, curve.Sample(0).Tangent),
                     (curve.Sample(curve.Length).Position, -curve.Sample(curve.Length).Tangent),
                 })
        {
            if (Vector2.Distance(pos, q.Cursor) > q.GuideSearchRadius) continue;
            // The session's own start isn't a node to align with.
            if (q.SessionPis.Count > 0 && Vector2.Distance(pos, q.SessionPis[^1].Position) < SplineMath.Epsilon) continue;
            // Square to the node's edge (along the edge is the extension guide's line already), plus the current
            // leg's reference directions.
            var dirs = new List<Vector2> { SplineMath.Left(edgeDir) };
            if (refDir is { } r && r.LengthSquared() > SplineMath.Epsilon) { dirs.Add(r); dirs.Add(SplineMath.Left(r)); }
            foreach (var raw in dirs)
            {
                if (raw.LengthSquared() < SplineMath.Epsilon) continue;
                var dir = Vector2.Normalize(raw);
                var guide = new GuideLine(new[] { pos - dir * q.GuideSearchRadius, pos + dir * q.GuideSearchRadius },
                    GuideKind.NodeAlign, $"aligned · {label}")
                {
                    Source = pos,
                };
                found.Add((guide, DistanceToPolyline(guide.Points, q.Cursor)));
            }
        }
    }

    private static SnapResult? TryCrossing(SnapQuery q, GuideLine a, GuideLine b)
    {
        Vector2? best = null;
        float bestDist = q.CatchDistance;
        foreach (var p in PolylineHits(a.Points, b.Points))
        {
            float d = Vector2.Distance(p, q.Cursor);
            if (d > bestDist) continue;
            bestDist = d;
            best = p;
        }
        if (best is not { } point) return null;
        return new SnapResult
        {
            Position = point, Kind = SnapKind.GuideCrossing, Tag = $"{a.Tag} × {b.Tag}", TagAt = point,
            Guides = new[] { Trim(a, point, q), Trim(b, point, q) },
        };
    }

    /// <summary>Where a lit guide's tag goes: halfway between its source and the snapped point (the storyboard puts it
    /// over the guide), or at the point for a parallel guide.</summary>
    private static Vector2 TagAnchor(GuideLine g, Vector2 p) => g.Kind == GuideKind.Parallel ? p : (g.Source + p) / 2f;

    /// <summary>A lit straight guide, cut to run from its source to a little past the snapped point, so it reads as
    /// "this lines up with that" instead of a line across the map.</summary>
    private static GuideLine Trim(GuideLine g, Vector2 p, SnapQuery q)
    {
        if (g.Kind == GuideKind.Parallel) return g;
        var d = p - g.Source;
        float m = q.CatchDistance * DisplayMarginFactor;
        if (d.Length() < SplineMath.Epsilon)
        {
            var along = Vector2.Normalize(g.Points[^1] - g.Points[0]);
            return g with { Points = new[] { p - along * m, p + along * m } };
        }
        var u = Vector2.Normalize(d);
        var start = g.Kind == GuideKind.Perpendicular ? g.Source - u * m : g.Source;
        return g with { Points = new[] { start, p + u * m } };
    }

    // --- Length ---

    private readonly record struct LengthMatch(float Length, SnapKind Kind, int? Steps, LegMatch? Match);

    /// <summary>A leg of <paramref name="rawLen"/> metres snapped to equal length (a nearby leg, if within catch and
    /// closer than the step) or to whole <see cref="ProfileRules.SnapLength"/> steps.</summary>
    private static LengthMatch MatchLength(SnapQuery q, SnapProviders providers, float rawLen)
    {
        LegMatch? equal = null;
        float equalDist = q.CatchDistance;
        if (providers.HasFlag(SnapProviders.EqualLength))
            foreach (var leg in NearbyLegs(q))
            {
                float d = MathF.Abs(leg.Length - rawLen);
                if (d > equalDist) continue;
                equalDist = d;
                equal = leg;
            }

        int? steps = null;
        float stepDist = float.PositiveInfinity;
        if (providers.HasFlag(SnapProviders.Length) && q.Rules.SnapLength > SplineMath.Epsilon)
        {
            int n = (int)MathF.Round(rawLen / q.Rules.SnapLength);
            float d = MathF.Abs(n * q.Rules.SnapLength - rawLen);
            if (n > 0 && d <= q.CatchDistance) { steps = n; stepDist = d; }
        }

        if (equal is { } e && equalDist <= stepDist)
            return new LengthMatch(e.Length, SnapKind.EqualLength, null, e);
        if (steps is { } s)
            return new LengthMatch(s * q.Rules.SnapLength, SnapKind.Length, s, null);
        return new LengthMatch(rawLen, SnapKind.None, null, null);
    }

    /// <summary>The previous leg of this draw, and the PI-to-PI legs of nearby built alignments.</summary>
    private static IEnumerable<LegMatch> NearbyLegs(SnapQuery q)
    {
        if (q.SessionPis.Count >= 2)
        {
            var a = q.SessionPis[^2].Position;
            var b = q.SessionPis[^1].Position;
            if (Vector2.Distance(a, b) > SplineMath.Epsilon) yield return new LegMatch(a, b, Vector2.Distance(a, b));
        }
        foreach (var c in q.Candidates)
            for (int i = 0; i + 1 < c.Alignment.Pis.Count; i++)
            {
                var a = c.Alignment.Pis[i].Position;
                var b = c.Alignment.Pis[i + 1].Position;
                if (Vector2.Distance((a + b) / 2f, q.Cursor) > q.GuideSearchRadius) continue;
                float len = Vector2.Distance(a, b);
                if (len > SplineMath.Epsilon) yield return new LegMatch(a, b, len);
            }
    }

    // --- Small shared helpers ---

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float DistanceToPolyline(IReadOnlyList<Vector2> points, Vector2 p) =>
        Vector2.Distance(ClosestPointOnPolyline(points, p), p);

    private static Vector2 ClosestPointOnPolyline(IReadOnlyList<Vector2> points, Vector2 cursor)
    {
        var best = points[0];
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var ab = points[i + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > SplineMath.Epsilon ? Math.Clamp(Vector2.Dot(cursor - a, ab) / len2, 0, 1) : 0;
            var p = a + ab * t;
            float d = Vector2.DistanceSquared(p, cursor);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    /// <summary>Where the ray <c>origin + t·dir</c> (t &gt; 0) crosses a polyline.</summary>
    private static IEnumerable<Vector2> RayPolylineHits(Vector2 origin, Vector2 dir, IReadOnlyList<Vector2> points)
    {
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var ab = points[i + 1] - a;
            float den = SplineMath.Cross(dir, ab);
            if (MathF.Abs(den) < 1e-6f) continue;
            float t = SplineMath.Cross(a - origin, ab) / den;
            float u = SplineMath.Cross(a - origin, dir) / den;
            if (t > SplineMath.Epsilon && u is >= 0 and <= 1) yield return origin + dir * t;
        }
    }

    /// <summary>Where two polylines cross.</summary>
    private static IEnumerable<Vector2> PolylineHits(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        for (int i = 0; i + 1 < a.Count; i++)
        {
            var p = a[i];
            var r = a[i + 1] - p;
            for (int j = 0; j + 1 < b.Count; j++)
            {
                var s = b[j + 1] - b[j];
                float den = SplineMath.Cross(r, s);
                if (MathF.Abs(den) < 1e-6f) continue;
                float t = SplineMath.Cross(b[j] - p, s) / den;
                float u = SplineMath.Cross(b[j] - p, r) / den;
                if (t is >= 0 and <= 1 && u is >= 0 and <= 1) yield return p + r * t;
            }
        }
    }
}
