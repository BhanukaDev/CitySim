using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// One network type's rules as a Godot resource (one <c>.tres</c> per type, made by the consumer). A thin wrapper:
/// <see cref="ToRules"/> gives the plain-C# <see cref="ProfileRules"/> the Core works with. Units: metres, degrees,
/// percent for grades.
/// </summary>
[Tool]
[GlobalClass]
public partial class SplineProfile : Resource
{
    /// <summary>Stable id, saved in graph files. Mods add profiles with new ids.</summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    /// <summary>Placeholder ribbon colour in the testbed.</summary>
    [Export] public Color Color { get; set; } = Colors.Gray;

    [ExportGroup("Plan")]
    [Export(PropertyHint.Range, "0.1,200,0.1,or_greater")] public float Width { get; set; } = 10f;
    [Export(PropertyHint.Range, "0,2000,1,or_greater")] public float DefaultRadius { get; set; } = 16f;
    [Export(PropertyHint.Range, "0,2000,1,or_greater")] public float MinRadius { get; set; } = 10f;
    [Export] public bool AllowHardCorners { get; set; }
    [Export(PropertyHint.Range, "0,500,1")] public float SpiralLength { get; set; }
    [Export(PropertyHint.Range, "0.5,100,0.5")] public float SnapLength { get; set; } = 8f;
    [Export] public SnapProviders SnapProviders { get; set; } = SnapProviders.All;
    /// <summary>Named offset sets, e.g. <c>twin:-4.5,4.5; wide:-8,8</c> (metres, + = left).</summary>
    [Export] public string ParallelPresets { get; set; } = "";

    [ExportGroup("Junctions")]
    [Export] public JunctionKind JunctionKind { get; set; } = JunctionKind.Node;
    [Export(PropertyHint.Range, "0,90,1")] public float MinJunctionAngle { get; set; } = 30f;
    [Export(PropertyHint.Range, "0,45,0.1")] public float TurnoutMaxAngle { get; set; }
    /// <summary>Profile ids or tags this joins. Empty = only itself.</summary>
    [Export] public string[] ConnectsTo { get; set; } = Array.Empty<string>();

    [ExportGroup("Vertical")]
    [Export] public VerticalMode VerticalMode { get; set; } = VerticalMode.Ground;
    /// <summary>Steepest grade in percent; negative = follows the ground with no limit.</summary>
    [Export(PropertyHint.Range, "-1,100,0.1")] public float MaxGradePercent { get; set; } = 12f;
    /// <summary>Metres along the line the ground is averaged over; 0 = exact, negative = level.</summary>
    [Export(PropertyHint.Range, "-1,2000,1")] public float GroundSmoothing { get; set; } = 20f;
    [Export] public bool SpeedFromRadius { get; set; }
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float LateralAccel { get; set; } = 2f;

    [ExportGroup("Terrain shaping")]
    [Export] public ShapingMode Shaping { get; set; } = ShapingMode.None;
    /// <summary>Half section: (offset from centre, height relative to the spline), mirrored.</summary>
    [Export] public Vector2[] Section { get; set; } = Array.Empty<Vector2>();
    /// <summary>Run per rise (2 = 1:2).</summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float CutSlope { get; set; } = 2f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float FillSlope { get; set; } = 2f;
    [Export] public EdgeMode Edge { get; set; } = EdgeMode.Slope;
    [Export(PropertyHint.Range, "0,50,0.5")] public float WallAbove { get; set; }
    [Export(PropertyHint.Range, "0,100,0.5")] public float MaxCutFill { get; set; }

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    public ProfileRules ToRules() => new()
    {
        Id = Id,
        Width = Width,
        DefaultRadius = DefaultRadius,
        MinRadius = MinRadius,
        AllowHardCorners = AllowHardCorners,
        SpiralLength = SpiralLength,
        JunctionKind = JunctionKind,
        MinJunctionAngle = MinJunctionAngle,
        TurnoutMaxAngle = TurnoutMaxAngle,
        MaxGrade = MaxGradePercent < 0 ? null : MaxGradePercent / 100f,
        SnapLength = SnapLength,
        SpeedFromRadius = SpeedFromRadius,
        LateralAccel = LateralAccel,
        ParallelPresets = ParsePresets(ParallelPresets),
        VerticalMode = VerticalMode,
        Shaping = Shaping,
        Section = Section.Select(p => new System.Numerics.Vector2(p.X, p.Y)).ToArray(),
        CutSlope = CutSlope,
        FillSlope = FillSlope,
        GroundSmoothing = GroundSmoothing < 0 ? float.PositiveInfinity : GroundSmoothing,
        Edge = Edge,
        WallAbove = WallAbove,
        MaxCutFill = MaxCutFill,
        SnapProviders = SnapProviders,
        ConnectsTo = ConnectsTo.ToArray(),
    };

    private static List<ParallelPreset> ParsePresets(string text)
    {
        var presets = new List<ParallelPreset>();
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            var offsets = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(o => float.Parse(o, CultureInfo.InvariantCulture)).ToArray();
            presets.Add(new ParallelPreset(parts[0], offsets));
        }
        return presets;
    }
}
