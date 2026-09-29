using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>One cross-section slice of a flat ribbon: left/right edge positions in plan space.</summary>
public readonly record struct RibbonSlice(Vector2 Left, Vector2 Right);

/// <summary>Flat-ribbon cross-section geometry for a <see cref="Curve"/> (DESIGN.md's placeholder flat-ribbon
/// visual). Plan space only; the Godot side drapes each edge onto the ground and builds a mesh from it.</summary>
public static class RibbonGeometry
{
    /// <summary>Left/right edges of a ribbon <paramref name="width"/> metres wide along <paramref name="curve"/>,
    /// sampled every <paramref name="spacing"/> metres (segment ends always included, via <see cref="Curve.SampleEvery"/>).</summary>
    public static IReadOnlyList<RibbonSlice> BuildSlices(Curve curve, float width, float spacing = 5f)
    {
        float half = width / 2f;
        var result = new List<RibbonSlice>();
        foreach (var (_, sample) in curve.SampleEvery(spacing))
        {
            var left = SplineMath.Left(sample.Tangent) * half;
            result.Add(new RibbonSlice(sample.Position + left, sample.Position - left));
        }
        return result;
    }
}
