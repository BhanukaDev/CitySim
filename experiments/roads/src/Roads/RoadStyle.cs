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

    [ExportGroup("Materials")]
    [Export] public Material? Asphalt { get; set; }
    [Export] public Material? Gravel { get; set; }
    [Export] public Material? Gutter { get; set; }
    [Export] public Material? Kerb { get; set; }
    [Export] public Material? Sidewalk { get; set; }
    [Export] public Material? Paint { get; set; }

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
        _ => null,
    };
}
