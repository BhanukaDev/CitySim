using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines;
using CitySim.Splines.Godot;

namespace CitySim.Roads;

/// <summary>
/// The splines addon's <see cref="SplineProfile"/> for each road type, built at start-up from the road's layout (the
/// addon only knows profiles, never roads). Drawing rules come from the splines testbed's street profile; any road
/// joins any other. Elevation (max grade, smoothing, side slopes) comes from the road type, and every road shapes
/// the ground.
/// </summary>
public static class RoadProfiles
{
    /// <summary>Kerb radius at a junction corner, at the kerb itself (NACTO: 3–4.5 m on local streets).</summary>
    public const float KerbRadius = 4f;
    public const float MinKerbRadius = 1f;
    public const float MaxKerbRadius = 20f;

    public static Dictionary<string, SplineProfile> For(IEnumerable<RoadType> roads)
    {
        var list = roads.ToList();
        string[] all = list.Select(r => r.Id).ToArray();
        return list.ToDictionary(r => r.Id, r => From(r, all));
    }

    public static SplineProfile From(RoadType road, string[] connectsTo)
    {
        var d = road.ToDef();
        // The addon measures a corner's radius at the corridor's edge (the back of the sidewalk); the kerb is a
        // sidewalk further out from the corner's centre.
        float back = d.Sidewalks == SidewalkLayout.Both ? d.SidewalkWidth : 0f;
        float Corner(float r) => MathF.Max(0.5f, r - back);
        return new SplineProfile
        {
            Id = road.Id,
            DisplayName = road.Label,
            Width = d.Width,
            DefaultRadius = MathF.Max(16f, d.Width),
            MinRadius = MathF.Max(10f, d.Width * 0.6f),
            AllowHardCorners = true,
            SnapLength = 8f,
            SnapUnitName = "lot",
            MinJunctionAngle = 30f,
            KerbRadius = Corner(KerbRadius),
            MinKerbRadius = Corner(MinKerbRadius),
            MaxKerbRadius = Corner(MaxKerbRadius),
            MaxGradePercent = road.MaxGrade,
            GroundSmoothing = road.GroundSmoothing,
            Shaping = ShapingMode.Section,
            CutSlope = road.SideSlope,
            FillSlope = road.SideSlope,
            MaxCut = road.MaxCut,
            MaxFill = road.MaxFill,
            ConnectsTo = connectsTo,
        };
    }
}
