using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// A height grid the ground shaping writes into: world heights at vertices <see cref="CellSize"/> apart, vertex (0, 0) at
/// plan (0, 0). The Godot side wraps a terrain edit.
/// </summary>
public interface IHeightGrid
{
    float CellSize { get; }
    int Width { get; }
    int Depth { get; }
    float this[int x, int z] { get; set; }
    /// <summary>Called once, before any write, with the inclusive vertex rectangle the writes fall in.</summary>
    void Touch(int minX, int minZ, int maxX, int maxZ);
}

/// <summary>
/// Shapes the ground to the splines' height lines (DESIGN.md → Terrain shaping), for profiles with
/// <see cref="ShapingMode.Section"/>. Under a spline and an apron one grid cell wide beside it, the ground is set to the
/// spline's height (level across). Beyond that it slopes back to the natural ground at <see cref="ProfileRules.CutSlope"/>
/// where the ground is higher and <see cref="ProfileRules.FillSlope"/> where it's lower, and stops where it meets it, so
/// on flat ground nothing slopes. A junction is a level disc at its node's height round its footprint. Each vertex
/// follows the nearest spline or junction, so redoing one road keeps every other road's ground as it was. No retaining
/// walls yet (<see cref="EdgeMode"/>).
/// </summary>
public static class GroundShaping
{
    /// <summary>Furthest a slope reaches past a corridor's edge.</summary>
    public const float MaxReach = 80f;

    /// <summary>A spline (a polyline with heights) or a junction (one point): its half width, slopes, and the box it may
    /// reshape.</summary>
    private sealed record Feature(Vector2[] Points, float[] Heights, float Half, float Cut, float Fill, float Reach, Vector2 Min, Vector2 Max)
    {
        public bool Covers(Vector2 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y;
    }

    /// <summary>Reshapes the ground round some edges and junction nodes (after they were built or changed). True if any
    /// height was written.</summary>
    public static bool Shape(IHeightGrid grid, SplineGraph g, IReadOnlyDictionary<int, JunctionFootprint> footprints,
        IEnumerable<int> edges, IEnumerable<int> nodes)
    {
        var (all, byEdge, byNode) = Features(grid, g, footprints);
        var dirty = edges.Where(byEdge.ContainsKey).Select(id => byEdge[id])
            .Concat(nodes.Where(byNode.ContainsKey).Select(id => byNode[id])).ToList();
        return Apply(grid, all, dirty);
    }

    /// <summary>Reshapes the ground round every spline and junction whose slopes reach into a plan rectangle (something
    /// else changed the ground there; the splines keep their heights and the ground is put back round them).</summary>
    public static bool ShapeArea(IHeightGrid grid, SplineGraph g, IReadOnlyDictionary<int, JunctionFootprint> footprints, Vector2 min, Vector2 max)
    {
        var (all, _, _) = Features(grid, g, footprints);
        var dirty = all.Where(f => f.Min.X <= max.X && f.Max.X >= min.X && f.Min.Y <= max.Y && f.Max.Y >= min.Y).ToList();
        return Apply(grid, all, dirty);
    }

    private static (List<Feature>, Dictionary<int, Feature>, Dictionary<int, Feature>) Features(IHeightGrid grid, SplineGraph g,
        IReadOnlyDictionary<int, JunctionFootprint> footprints)
    {
        var all = new List<Feature>();
        var byEdge = new Dictionary<int, Feature>();
        var byNode = new Dictionary<int, Feature>();
        foreach (var e in g.Edges)
        {
            if (e.Rules.Shaping != ShapingMode.Section || e.Heights is not { } line) continue;
            var curve = e.Alignment.Curve;
            int n = line.Heights.Length - 1;
            var pts = Enumerable.Range(0, n + 1).Select(i => curve.Sample(i * line.Spacing).Position).ToArray();
            all.Add(byEdge[e.Id] = Make(grid, pts, line.Heights, e.Rules.Width / 2, e.Rules));
        }
        foreach (var f in footprints.Values)
        {
            var node = g.Node(f.NodeId);
            var rules = f.Cuts.Select(c => g.Edge(c.EdgeId).Rules).Where(r => r.Shaping == ShapingMode.Section).MaxBy(r => r.Width);
            if (rules is null || node.Height is not { } h) continue;
            float r = f.Outline.Select(p => Vector2.Distance(p, f.Centre)).DefaultIfEmpty(rules.Width / 2).Max();
            all.Add(byNode[f.NodeId] = Make(grid, [node.Position], [h], r, rules));
        }
        return (all, byEdge, byNode);
    }

    private static Feature Make(IHeightGrid grid, Vector2[] pts, float[] heights, float half, ProfileRules rules)
    {
        // How far the slopes may need to run: the biggest height difference to the ground along it, plus room for
        // ground that climbs to the side.
        float diff = 0;
        for (int i = 0; i < pts.Length; i++) diff = MathF.Max(diff, MathF.Abs(heights[i] - Sample(grid, pts[i])));
        float slope = MathF.Max(rules.CutSlope, rules.FillSlope);
        float reach = half + grid.CellSize + MathF.Min(MaxReach, slope * diff + 12f);
        var min = new Vector2(pts.Min(p => p.X), pts.Min(p => p.Y)) - new Vector2(reach);
        var max = new Vector2(pts.Max(p => p.X), pts.Max(p => p.Y)) + new Vector2(reach);
        return new Feature(pts, heights, half, rules.CutSlope, rules.FillSlope, reach, min, max);
    }

    private static bool Apply(IHeightGrid grid, List<Feature> all, List<Feature> dirty)
    {
        if (dirty.Count == 0) return false;
        float cs = grid.CellSize;
        int x0 = Math.Max(0, (int)MathF.Floor(dirty.Min(f => f.Min.X) / cs)), z0 = Math.Max(0, (int)MathF.Floor(dirty.Min(f => f.Min.Y) / cs));
        int x1 = Math.Min(grid.Width - 1, (int)MathF.Ceiling(dirty.Max(f => f.Max.X) / cs));
        int z1 = Math.Min(grid.Depth - 1, (int)MathF.Ceiling(dirty.Max(f => f.Max.Y) / cs));
        if (x1 < x0 || z1 < z0) return false;

        var writes = new List<(int X, int Z, float H)>();
        var near = new List<Feature>();
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x * cs, z * cs);
                if (!dirty.Any(f => f.Covers(p))) continue;
                near.Clear();
                near.AddRange(all.Where(f => f.Covers(p)));
                Feature? best = null;
                float bestT = float.MaxValue, bestH = 0;
                foreach (var f in near)
                {
                    var (d, h) = Nearest(f, p);
                    if (d - f.Half < bestT) (best, bestT, bestH) = (f, d - f.Half, h);
                }
                if (best is null || bestT > best.Reach - best.Half) continue;
                float g0 = grid[x, z];
                float run = MathF.Max(0, bestT - cs); // the apron: one cell of level ground beside the corridor
                float target = g0 > bestH
                    ? MathF.Min(g0, bestH + (best.Cut > 0 ? run / best.Cut : float.MaxValue))
                    : MathF.Max(g0, bestH - (best.Fill > 0 ? run / best.Fill : float.MaxValue));
                if (MathF.Abs(target - g0) > 1e-4f) writes.Add((x, z, target));
            }
        if (writes.Count == 0) return false;
        grid.Touch(writes.Min(w => w.X), writes.Min(w => w.Z), writes.Max(w => w.X), writes.Max(w => w.Z));
        foreach (var (x, z, h) in writes) grid[x, z] = h;
        return true;
    }

    /// <summary>Distance from a point to a feature's centre line, and the line's height there.</summary>
    private static (float Dist, float Height) Nearest(Feature f, Vector2 p)
    {
        var pts = f.Points;
        if (pts.Length == 1) return (Vector2.Distance(p, pts[0]), f.Heights[0]);
        float best = float.MaxValue, h = f.Heights[0];
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var ab = pts[i + 1] - pts[i];
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(p - pts[i], ab) / len2, 0, 1);
            float d2 = Vector2.DistanceSquared(p, pts[i] + ab * t);
            if (d2 < best) (best, h) = (d2, f.Heights[i] + (f.Heights[i + 1] - f.Heights[i]) * t);
        }
        return (MathF.Sqrt(best), h);
    }

    /// <summary>Bilinear height at a plan point.</summary>
    private static float Sample(IHeightGrid grid, Vector2 p)
    {
        float fx = Math.Clamp(p.X / grid.CellSize, 0, grid.Width - 1), fz = Math.Clamp(p.Y / grid.CellSize, 0, grid.Depth - 1);
        int x = Math.Min((int)fx, grid.Width - 2), z = Math.Min((int)fz, grid.Depth - 2);
        float tx = fx - x, tz = fz - z;
        float a = grid[x, z] + (grid[x + 1, z] - grid[x, z]) * tx;
        float b = grid[x, z + 1] + (grid[x + 1, z + 1] - grid[x, z + 1]) * tx;
        return a + (b - a) * tz;
    }
}
