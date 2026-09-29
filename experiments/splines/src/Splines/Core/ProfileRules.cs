using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>A named set of offsets for Parallel mode (metres, + = left of travel).</summary>
public sealed record ParallelPreset(string Name, float[] Offsets);

/// <summary>
/// Every rule that differs between network types, as plain data (DESIGN.md → Profiles). The addon reads only this, never
/// a network name. Lengths in metres, angles in degrees, grades as fractions (0.12 = 12 %).
/// </summary>
public sealed record ProfileRules
{
    public string Id { get; init; } = "";

    /// <summary>Corridor width: snapping, junction cut-back, collision.</summary>
    public float Width { get; init; } = 10f;
    public float DefaultRadius { get; init; }
    /// <summary>Below this a radius is invalid unless Anarchy is on.</summary>
    public float MinRadius { get; init; }
    public bool AllowHardCorners { get; init; }
    /// <summary>Clothoid length in and out of each arc (S7).</summary>
    public float SpiralLength { get; init; }

    public JunctionKind JunctionKind { get; init; } = JunctionKind.Node;
    public float MinJunctionAngle { get; init; }
    public float TurnoutMaxAngle { get; init; }

    /// <summary>Steepest grade; null = follows the ground with no limit.</summary>
    public float? MaxGrade { get; init; }
    public float SnapLength { get; init; } = 8f;
    /// <summary>What one <see cref="SnapLength"/> step is called in tags ("5 lots"), so the wording stays data.</summary>
    public string SnapUnitName { get; init; } = "lot";

    /// <summary>Show a speed readout <c>v = √(a·R)</c> from the tightest radius.</summary>
    public bool SpeedFromRadius { get; init; }
    /// <summary>Lateral acceleration <c>a</c> for the speed readout (m/s²).</summary>
    public float LateralAccel { get; init; } = 2f;

    public IReadOnlyList<ParallelPreset> ParallelPresets { get; init; } = Array.Empty<ParallelPreset>();

    public VerticalMode VerticalMode { get; init; } = VerticalMode.Ground;
    public ShapingMode Shaping { get; init; } = ShapingMode.None;
    /// <summary>Half cross-section: (offset from centre, height relative to the spline), mirrored on both sides.</summary>
    public IReadOnlyList<Vector2> Section { get; init; } = Array.Empty<Vector2>();
    /// <summary>Cut slope as run per rise (2 = 1:2).</summary>
    public float CutSlope { get; init; } = 2f;
    /// <summary>Fill slope as run per rise (2 = 1:2).</summary>
    public float FillSlope { get; init; } = 2f;
    /// <summary>Distance along the line over which <see cref="VerticalMode.Ground"/> averages the ground; 0 = exact,
    /// infinity = level.</summary>
    public float GroundSmoothing { get; init; }
    public EdgeMode Edge { get; init; } = EdgeMode.Slope;
    /// <summary>For <see cref="EdgeMode.Auto"/>: walls where the cut or fill is higher than this.</summary>
    public float WallAbove { get; init; }
    /// <summary>Above this cut or fill depth the edge turns amber.</summary>
    public float MaxCutFill { get; init; }

    public SnapProviders SnapProviders { get; init; } = SnapProviders.All;
    /// <summary>Profile ids (or tags) this profile joins at junctions. Empty = only itself.</summary>
    public IReadOnlyList<string> ConnectsTo { get; init; } = Array.Empty<string>();
}
