using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Roads.Geometry;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Roads;

public sealed partial class RoadVisual
{
    /// <summary>
    /// A transition (<see cref="JunctionFootprint.Continuous"/>: a road changing width or moving sideways at a node) as
    /// one piece of road, not a junction plate: the cut road's own cross-section carried on from its cut to the node,
    /// each point eased across to where the next road has it (its sidewalk, kerb and gutter move with the side they're
    /// on; the lanes between stretch or squeeze), so the surface, kerbs and sidewalks run on with no seam. The lines the
    /// next road has too (the edge lines and the centre line, and every lane line when the lane counts match) run on
    /// through it; lines that end stop at the cut. Wear follows the lane links (<see cref="Tracks"/>), and lanes no link
    /// drives over get chevrons, so a lane only shows as ending when it has nowhere to go. False when it isn't one this
    /// can draw (no cut, or no lanes): then it's drawn as a junction.
    /// </summary>
    private bool Transition(RoadMesh rm, SplineGraph g, JunctionFootprint f)
    {
        if (f.Cuts.Count != 2 || f.Cuts.FirstOrDefault(c => c.CutBack > 0.5f) is not { EdgeId: > 0 } cut) return false;
        var kept = f.Cuts.First(c => c.EdgeId != cut.EdgeId || c.AtStart != cut.AtStart);
        var (ec, ek) = (g.Edge(cut.EdgeId), g.Edge(kept.EdgeId));
        var (sc, sk) = (SectionOf(ec), SectionOf(ek));
        if (sc.Lanes.Count == 0 || sk.Lanes.Count == 0) return false;

        float len = cut.CutBack, total = ec.Alignment.Length;
        float sNode = cut.AtStart ? 0 : total, sCut = cut.AtStart ? len : total - len;
        // Across the cut road's heading (left of its curve): the kept road runs the same way along, or against it.
        float flip = cut.AtStart != kept.AtStart ? 1 : -1;
        float hw = sc.LaneWidth / 2, hk = sk.LaneWidth / 2;
        float lc = sc.Lanes.Max(l => l.Offset) + hw, rc = sc.Lanes.Min(l => l.Offset) - hw;
        float lk0 = sk.Lanes.Max(l => l.Offset) + hk, rk0 = sk.Lanes.Min(l => l.Offset) - hk;
        var (lk, rk) = flip > 0 ? (lk0, rk0) : (-rk0, -lk0);
        float oc = ec.Offset, ok = flip * ek.Offset;
        float dl = ok + lk - (oc + lc), dr = ok + rk - (oc + rc);

        // How far across (0 at the cut, 1 at the node), eased like the footprint's sides.
        float Ease(float s) => SmoothStep(0, 1, MathF.Abs(s - sCut) / len);
        // Where a point of the cut road's section is at a station, left of its alignment.
        float Across(float x, float s)
        {
            float e = Ease(s);
            if (x >= lc) return oc + x + dl * e;
            if (x <= rc) return oc + x + dr * e;
            float t = (x - rc) / (lc - rc);
            return oc + rc + dr * e + (lc + dl * e - rc - dr * e) * t;
        }

        // Each lane's centre follows its lane on the kept road (the one most in line with it going the same way, as the
        // road's own links go): lanes that merge or branch run together, lanes that carry on just move across.
        var lanesC = sc.Lanes;
        var to = lanesC.Select(l =>
        {
            var same = sk.Lanes.Where(k => (flip > 0) == (k.Forward == l.Forward)).Select(k => ok + flip * k.Offset).ToList();
            return same.Count == 0 ? Across(l.Offset, sNode) : same.MinBy(u => MathF.Abs(u - (oc + l.Offset)));
        }).ToArray();
        float Centre(int i, float e) => oc + lanesC[i].Offset + (to[i] - oc - lanesC[i].Offset) * e;
        float Width(float e) => sc.LaneWidth + (sk.LaneWidth - sc.LaneWidth) * e;

        // The columns across: the section's points outside the lanes (moved with their side), and across the lanes the
        // lanes' own spans, split halfway between neighbouring lane centres, each span worn as its lane.
        const float eps = 1e-3f;
        var cols = new List<Col>();
        foreach (var p in sc.Outline(0).Where(p => p.Offset > lc + eps)) cols.Add(new Col(ColKind.Fixed, p.Offset, p.Height, p.Surface, -1));
        var outline0 = sc.Outline(0);
        var laneSurface = outline0.Where(p => p.Lanes).Select(p => p.Surface).DefaultIfEmpty(SurfaceKind.Asphalt).First();
        cols.Add(new Col(ColKind.Left, lc, sc.CarriagewayHeight(lc, 0), laneSurface, 0));
        for (int i = 1; i < lanesC.Count; i++) cols.Add(new Col(ColKind.Between, i, sc.CarriagewayHeight(0, 0), laneSurface, i));
        var right = outline0.Where(p => MathF.Abs(p.Offset - rc) < eps).Select(p => (SectionPoint?)p).LastOrDefault()
            ?? new SectionPoint(rc, sc.CarriagewayHeight(rc, 0), SurfaceKind.Asphalt);
        cols.Add(new Col(ColKind.Right, rc, right.Height, right.Surface, -1));
        foreach (var p in outline0.Where(p => p.Offset < rc - eps)) cols.Add(new Col(ColKind.Fixed, p.Offset, p.Height, p.Surface, -1));

        // Where each column is at a station (left of the alignment), left to right.
        float[] Columns(float s)
        {
            float e = Ease(s), lo = Across(rc, s), hi = Across(lc, s);
            var us = new float[cols.Count];
            for (int k = 0; k < cols.Count; k++)
            {
                var c = cols[k];
                us[k] = c.Kind switch
                {
                    ColKind.Between => Math.Clamp((Centre((int)c.X - 1, e) + Centre((int)c.X, e)) / 2, lo, MathF.Min(hi, us[k - 1])),
                    _ => Across(c.X, s),
                };
            }
            return us;
        }
        // Across a lane's span: lanes from the left, its middle at .5, so the wheel tracks sit on the lane's centre.
        float LaneCoord(int lane, float u, float e) => lane + 0.5f - (u - Centre(lane, e)) / Width(e);

        // The surface: the cut road's flat section (a transition sits level, as its ends fade their crown for it).
        float a = MathF.Min(sCut, sNode), b = MathF.Max(sCut, sNode);
        float split = RoadMesh.SplitCode(sc.Split);
        (Vector3[] Pos, float[] U, float S, float E)? prev = null;
        foreach (float s in Stations(a, b, 1f))
        {
            var sample = ec.Alignment.Curve.Sample(s);
            _level = LevelAt(ec, s);
            var left = SplineMath.Left(sample.Tangent);
            var us = Columns(s);
            var ring = cols.Select((c, k) => Point(sample.Position + left * us[k], c.H)).ToArray();
            float e = Ease(s);
            if (prev is { } pr)
                for (int k = 0; k + 1 < cols.Count; k++)
                {
                    var c = cols[k];
                    if (ProfileNormal(new SectionPoint(us[k], c.H, c.Surface), new SectionPoint(us[k + 1], cols[k + 1].H, c.Surface), left) is not { } n) continue;
                    // Both ends of a span take its lane, so a strip next to a lane stays unworn (as on a road).
                    RoadMesh.Vertex V(Vector3 pos, float along, float u, float ee) => new(pos, new Vector2(along, u - oc),
                        new Vector2(c.Lane >= 0 ? LaneCoord(c.Lane, u, ee) : RoadMesh.NoLane, rm.Wear), new Vector3(0, 0, split));
                    rm.Quad(c.Surface, V(pr.Pos[k], pr.S, pr.U[k], pr.E), V(pr.Pos[k + 1], pr.S, pr.U[k + 1], pr.E),
                        V(ring[k + 1], s, us[k + 1], e), V(ring[k], s, us[k], e), n);
                }
            prev = (ring, us, s, e);
        }

        // Lines the kept road has at the node run on through; the rest end at the cut.
        var keptLines = sk.Lines.Select(l => (U: ok + flip * l.Offset, Line: l)).ToList();
        foreach (var line in sc.Lines)
        {
            float atNode = Across(line.Offset, sNode);
            var match = keptLines.Where(k => MathF.Abs(k.U - atNode) < 0.3f).Select(k => (SectionLine?)k.Line).FirstOrDefault();
            if (match is not { } other) continue;
            var through = line with { Dash = line.Dash > 0 && other.Dash > 0 ? line.Dash : 0 };
            void Strip(float from, float to) => Paint(rm, sc, ec, from, to, through, _ => 0, s => Across(line.Offset, s));
            if (through.Dash <= 0) Strip(a, b);
            else Dashes(a, b, through, Strip);
        }

        // Wear along the lane links, and chevrons over the lanes no link drives over.
        _level = LevelOf(g.Node(f.NodeId));
        var paths = Tracks(rm, g, f, ribbons: false); // the surface carries the lanes' own wear
        var lanesLeft = Stations(a, b, 1f).Select(s => Plan(ec, s, Across(lc, s)));
        var lanesRight = Stations(a, b, 1f).Reverse().Select(s => Plan(ec, s, Across(rc, s)));
        Hatching(rm, new[] { lanesLeft.Concat(lanesRight).ToArray() }, paths);
        return true;
    }

    private enum ColKind { Fixed, Left, Between, Right }

    /// <summary>A column across a transition's surface: a section point (<see cref="ColKind.Fixed"/>, at offset
    /// <see cref="X"/>), a lane edge, or the split between lanes X − 1 and X. <see cref="Surface"/> and <see cref="Lane"/>
    /// (−1 = none) are the span to its right.</summary>
    private readonly record struct Col(ColKind Kind, float X, float H, SurfaceKind Surface, int Lane);

    /// <summary>The plan point <paramref name="u"/> left of an edge's alignment at station <paramref name="s"/>.</summary>
    private static Vector2 Plan(GraphEdge e, float s, float u)
    {
        var sample = e.Alignment.Curve.Sample(s);
        var p = sample.Position + SplineMath.Left(sample.Tangent) * u;
        return new Vector2(p.X, p.Y);
    }

    /// <summary>Stations from <paramref name="a"/> to <paramref name="b"/>, both included, at most <paramref name="step"/> apart.</summary>
    private static IEnumerable<float> Stations(float a, float b, float step)
    {
        int n = Math.Max(1, (int)MathF.Ceiling((b - a) / step));
        for (int k = 0; k <= n; k++) yield return a + (b - a) * k / n;
    }
}
