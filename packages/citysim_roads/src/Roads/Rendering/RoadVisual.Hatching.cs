using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using Godot;

namespace CitySim.Roads;

public sealed partial class RoadVisual
{
    /// <summary>Hatched islands in the last build (for the demo's checks).</summary>
    public int Hatches { get; private set; }

    /// <summary>
    /// Chevron hatching (Vienna Convention protocol, "areas not to be entered") on the parts of a junction's asphalt no
    /// car needs: the <paramref name="asphalt"/> minus every path cars take (<see cref="Tracks"/>, a lane wide plus
    /// <see cref="SectionStyle.HatchClearance"/> each side). What's left at least <see cref="SectionStyle.HatchMinWidth"/>
    /// across gets a border line and chevrons inside it along its length, pointing to its narrow end, so a skewed
    /// junction's dead space reads as road markings instead of spare asphalt. Corners between a turn and the kerb are
    /// slivers and drop out. <paramref name="holes"/>: what's left of the asphalt is cut round these (a cluster's islands).
    /// </summary>
    private void Hatching(RoadMesh rm, IEnumerable<Vector2[]> asphalt, List<TrackPath> paths, IEnumerable<Vector2[]>? holes = null)
    {
        var st = _sectionStyle;
        if (paths.Count == 0 || st.HatchStripe <= 0) return;
        var islands = asphalt.ToList();
        foreach (var path in paths)
        {
            var swept = path.Swept(path.Half + st.HatchClearance);
            if (swept.Length < 3) continue;
            islands = islands.SelectMany(i =>
            {
                var cut = Geometry2D.ClipPolygons(i, swept);
                // A path inside an island without touching its edge would leave a hole; paths run past the mouths, so
                // that's only an odd footprint. Leave the island whole then.
                return cut.Select(Geometry2D.IsPolygonClockwise).Distinct().Count() > 1 ? new[] { i } : cut.ToArray();
            }).ToList();
        }
        if (holes is not null)
        {
            // Round the islands of a cluster, and drop the slivers left between a hole and a path.
            float r = st.HatchMinWidth / 4;
            islands = MinusAll(islands, holes).SelectMany(i => Solid(Offset(i, -r)))
                .SelectMany(i => Solid(Offset(i, r))).ToList();
        }
        foreach (var island in islands)
        {
            if (MathF.Abs(SignedArea(island)) < st.HatchMinWidth * st.HatchMinWidth
                || !Geometry2D.OffsetPolygon(island, -st.HatchMinWidth / 2).Any()) continue;
            Border(rm, island, st.HatchBorder);
            foreach (var inner in Geometry2D.OffsetPolygon(island, -(st.HatchBorder - 0.01f)))
                if (inner.Length >= 3) Chevrons(rm, inner, island);
            Hatches++;
        }
    }

    /// <summary>A painted line <paramref name="width"/> wide round the inside of <paramref name="poly"/>.</summary>
    private void Border(RoadMesh rm, Vector2[] poly, float width)
    {
        bool ccw = SignedArea(poly) > 0;
        int n = poly.Length;
        Vector2 Inward(Vector2 a, Vector2 b)
        {
            var d = (b - a).Normalized();
            return ccw ? new Vector2(-d.Y, d.X) : new Vector2(d.Y, -d.X);
        }
        var inner = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 n1 = Inward(poly[(i + n - 1) % n], poly[i]), n2 = Inward(poly[i], poly[(i + 1) % n]);
            var m = n1 + n2;
            m = m.LengthSquared() < 1e-6f ? n1 : m.Normalized();
            // Mitred, but a sharp corner's mitre is cut short rather than reaching across the island.
            inner[i] = poly[i] + m * (width / MathF.Max(0.25f, m.Dot(n1)));
        }
        float half = width / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            rm.Quad(SurfaceKind.Paint, PaintAt(poly[i], half, half), PaintAt(poly[j], half, half),
                PaintAt(inner[j], half, -half), PaintAt(inner[i], half, -half), Vector3.Up);
        }
    }

    /// <summary>
    /// Chevrons filling <paramref name="inner"/>, laid along <paramref name="island"/>'s long axis with their points
    /// toward its narrow end: each a V of two stripes at <see cref="SectionStyle.HatchAngle"/> to the axis, drawn as two
    /// arms clipped to the island (so the paint's across-the-stripe coordinate stays exact in each).
    /// </summary>
    private void Chevrons(RoadMesh rm, Vector2[] inner, Vector2[] island)
    {
        var st = _sectionStyle;
        var centre = island.Aggregate(Vector2.Zero, (s, p) => s + p) / island.Length;
        // The long axis: the main direction of the outline's points (their covariance's major eigenvector).
        float xx = 0, xy = 0, yy = 0;
        foreach (var p in island)
        {
            var d = p - centre;
            xx += d.X * d.X; xy += d.X * d.Y; yy += d.Y * d.Y;
        }
        var axis = Vector2.FromAngle(0.5f * MathF.Atan2(2 * xy, xx - yy));
        var across = new Vector2(-axis.Y, axis.X);
        (float U, float V) Local(Vector2 p) => ((p - centre).Dot(axis), (p - centre).Dot(across));

        // Point the Vs (their tips are at the low end of u) toward the narrow end.
        var local = island.Select(Local).ToList();
        float uMin = local.Min(l => l.U), uMax = local.Max(l => l.U), len = uMax - uMin;
        float Spread(Func<float, bool> near)
        {
            var vs = local.Where(l => near(l.U)).Select(l => l.V).ToList();
            return vs.Count == 0 ? 0 : vs.Max() - vs.Min();
        }
        if (Spread(u => u > uMax - len * 0.3f) < Spread(u => u < uMin + len * 0.3f))
        {
            axis = -axis;
            across = -across;
            local = island.Select(Local).ToList();
            (uMin, uMax) = (local.Min(l => l.U), local.Max(l => l.U));
        }

        float reach = local.Max(l => MathF.Abs(l.V)) + 1f;
        float angle = Mathf.DegToRad(st.HatchAngle), tan = MathF.Tan(angle), cos = MathF.Cos(angle);
        // Along the axis, a stripe st.HatchStripe wide measures this much, and so does the pitch between stripes.
        float wide = st.HatchStripe / cos, pitch = (st.HatchStripe + st.HatchGap) / cos;
        float half = st.HatchStripe / 2;
        Vector2 World(float u, float v) => centre + axis * u + across * v;
        // From the first chevron whose arms reach the island's low end.
        float first = MathF.Floor((uMin - reach * tan - wide) / pitch) * pitch;
        for (float u0 = first; u0 <= uMax; u0 += pitch)
            foreach (int side in new[] { 1, -1 })
            {
                var arm = new[]
                {
                    World(u0, 0), World(u0 + reach * tan, side * reach), World(u0 + reach * tan + wide, side * reach), World(u0 + wide, 0),
                };
                foreach (var piece in Geometry2D.IntersectPolygons(arm, inner))
                    PaintFill(rm, piece, p =>
                    {
                        var (u, v) = Local(p);
                        return (u - u0 - MathF.Abs(v) * tan - wide / 2) * cos;
                    }, half);
            }
    }

    /// <summary>A flat painted polygon on a junction; <paramref name="acrossOf"/> gives a point's metres across its stripe
    /// from the stripe's middle (the paint fades its sides from that).</summary>
    private void PaintFill(RoadMesh rm, Vector2[] poly, Func<Vector2, float> acrossOf, float half)
    {
        if (poly.Length < 3) return;
        var tris = Geometry2D.TriangulatePolygon(poly);
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            RoadMesh.Vertex V(int k) => PaintAt(poly[tris[k]], half, Math.Clamp(acrossOf(poly[tris[k]]), -half, half));
            rm.Tri(SurfaceKind.Paint, V(i), V(i + 1), V(i + 2), Vector3.Up);
        }
    }

    /// <summary>A paint vertex on a junction (UV as <see cref="Paint"/> gives it; no ends to fray).</summary>
    private RoadMesh.Vertex PaintAt(Vector2 p, float half, float across) =>
        new(Point(p, PaintLift), new Vector2(half, across), new Vector2(10, 10));
}
