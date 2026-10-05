using CitySim.Roads.Geometry;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// How roads look: kerb, crown and marking dimensions and the materials, one <c>.tres</c> under
/// <c>content/roads/styles/</c> (a mod replaces it by id). Materials are made in Godot (<c>content/roads/materials/</c>).
/// Dimensions follow the research doc "Small Two-Lane Road: Reference Dimensions" (European markings). Metres.
/// <see cref="ToSection"/> gives the plain-C# numbers the geometry uses.
/// </summary>
[GlobalClass]
public partial class RoadStyle : Resource
{
    public const string DefaultId = "default";

    [Export] public string Id { get; set; } = DefaultId;

    [ExportGroup("Section")]
    /// <summary>Kerb face height, carriageway edge to sidewalk top (US 6 in, UK 125 mm, Germany 12 cm).</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float KerbHeight { get; set; } = 0.15f;
    /// <summary>Width of the kerb stone's top, drawn in the kerb material before the sidewalk paving starts.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float KerbTopWidth { get; set; } = 0.15f;
    /// <summary>Cross slope of the carriageway from its centre down to each kerb (0.02 = 2 %).</summary>
    [Export(PropertyHint.Range, "0,0.06,0.005")] public float Crown { get; set; } = 0.02f;
    /// <summary>Concrete gutter along each kerb, taken from the strip (none where the strip is narrower).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float GutterWidth { get; set; } = 0.5f;
    /// <summary>How far the sidewalk's outer face reaches down into the ground, so slopes don't show a gap.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float SkirtDepth { get; set; } = 0.3f;

    [ExportGroup("Markings")]
    [Export(PropertyHint.Range, "0.05,0.5,0.01")] public float LineWidth { get; set; } = 0.12f;
    /// <summary>Centre line between the two directions: dash and gap (German town standard 3 : 6).</summary>
    [Export(PropertyHint.Range, "0.5,20,0.5")] public float CentreDash { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float CentreGap { get; set; } = 6f;
    /// <summary>Lines between lanes going the same way.</summary>
    [Export(PropertyHint.Range, "0.5,20,0.5")] public float LaneDash { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float LaneGap { get; set; } = 6f;
    // Junction mouths (Vienna Convention protocol on road markings): a zebra crossing between the sidewalks, a stop line
    // behind it across the lanes coming in, and the lines between those lanes solid on the approach.
    /// <summary>Stop line across the lanes coming in (0.2 to 0.6 m).</summary>
    [Export(PropertyHint.Range, "0.1,0.6,0.01")] public float StopLineWidth { get; set; } = 0.3f;
    /// <summary>From where the road meets the junction to the zebra crossing.</summary>
    [Export(PropertyHint.Range, "0,5,0.1")] public float CrossingSetback { get; set; } = 0.5f;
    /// <summary>Length of the zebra bars, along the road (at least 2.5 m up to 60 km/h).</summary>
    [Export(PropertyHint.Range, "1,8,0.1")] public float CrossingWidth { get; set; } = 3f;
    /// <summary>Zebra bar and the gap after it, across the road (bar + gap 0.8 to 1.4 m, gap 1 to 2 bars).</summary>
    [Export(PropertyHint.Range, "0.2,1,0.05")] public float CrossingBar { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.2,1,0.05")] public float CrossingGap { get; set; } = 0.5f;
    /// <summary>From the crossing back to the stop line.</summary>
    [Export(PropertyHint.Range, "0,5,0.1")] public float StopLineGap { get; set; } = 1f;
    /// <summary>How far before the stop line the centre line and lines between lanes coming in are solid.</summary>
    [Export(PropertyHint.Range, "0,60,1")] public float SolidApproach { get; set; } = 15f;
    /// <summary>An automatic crossing closer than this along the road to another crossing is left out (the busier
    /// junction's, or one the player placed, stays).</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float CrossingMinGap { get; set; } = 25f;
    // Chevron hatching on junction asphalt no car drives over (a skewed junction's dead space), instead of a kerbed island.
    /// <summary>Width of each chevron stripe (0 = no hatching).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float HatchStripe { get; set; } = 0.5f;
    /// <summary>Gap between chevron stripes.</summary>
    [Export(PropertyHint.Range, "0.2,3,0.1")] public float HatchGap { get; set; } = 1f;
    /// <summary>Angle of the chevron arms to the island's long axis, in degrees.</summary>
    [Export(PropertyHint.Range, "20,70,1")] public float HatchAngle { get; set; } = 45f;
    /// <summary>The line round a hatched island.</summary>
    [Export(PropertyHint.Range, "0.05,0.5,0.01")] public float HatchBorder { get; set; } = 0.15f;
    /// <summary>Room kept clear either side of the lane a car takes across the junction.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float HatchClearance { get; set; } = 0.5f;
    /// <summary>Spare asphalt narrower than this is left plain.</summary>
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float HatchMinWidth { get; set; } = 2f;

    [ExportGroup("Materials")]
    [Export] public Material? Asphalt { get; set; }
    [Export] public Material? Gravel { get; set; }
    [Export] public Material? Gutter { get; set; }
    [Export] public Material? Kerb { get; set; }
    [Export] public Material? Sidewalk { get; set; }
    [Export] public Material? Paint { get; set; }
    /// <summary>Tyre wear laid over junctions along the paths cars take (blended over the asphalt).</summary>
    [Export] public Material? Wear { get; set; }

    public string Source { get; internal set; } = "Base";

    public SectionStyle ToSection() => new()
    {
        KerbHeight = KerbHeight,
        KerbTopWidth = KerbTopWidth,
        Crown = Crown,
        GutterWidth = GutterWidth,
        SkirtDepth = SkirtDepth,
        LineWidth = LineWidth,
        CentreDash = CentreDash,
        CentreGap = CentreGap,
        LaneDash = LaneDash,
        LaneGap = LaneGap,
        StopLineWidth = StopLineWidth,
        CrossingSetback = CrossingSetback,
        CrossingWidth = CrossingWidth,
        CrossingBar = CrossingBar,
        CrossingGap = CrossingGap,
        StopLineGap = StopLineGap,
        SolidApproach = SolidApproach,
        CrossingMinGap = CrossingMinGap,
        HatchStripe = HatchStripe,
        HatchGap = HatchGap,
        HatchAngle = HatchAngle,
        HatchBorder = HatchBorder,
        HatchClearance = HatchClearance,
        HatchMinWidth = HatchMinWidth,
    };

    /// <summary>The material for a surface kind (null = left to the renderer's fallback).</summary>
    public Material? MaterialOf(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Asphalt => Asphalt,
        SurfaceKind.Gravel => Gravel ?? Asphalt,
        SurfaceKind.Gutter => Gutter ?? Asphalt,
        SurfaceKind.Kerb => Kerb,
        SurfaceKind.Sidewalk => Sidewalk,
        SurfaceKind.Paint => Paint,
        SurfaceKind.Wear => Wear,
        _ => null,
    };
}
