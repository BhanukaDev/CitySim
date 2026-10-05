using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using CitySim.Content;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// A road the player can build: one <c>.tres</c> per road under <c>content/roads/types/</c> (or a mod's folder). Holds
/// the base layout only (lanes, sidewalks, median). A thin wrapper: <see cref="ToDef"/> gives the plain-C#
/// <see cref="RoadDef"/>.
/// </summary>
[GlobalClass]
public partial class RoadType : BuildItem
{
    [ExportGroup("Layout")]
    /// <summary>Driving lanes in total, both directions together.</summary>
    [Export(PropertyHint.Range, "1,12,1")] public int Lanes { get; set; } = 2;
    [Export] public bool OneWay { get; set; }
    /// <summary>Two-way only: lanes in the forward direction, for asymmetric roads (3 lanes as 2 + 1 → 2). 0 = even split.</summary>
    [Export(PropertyHint.Range, "0,11,1")] public int ForwardLanes { get; set; }
    [Export] public RoadSurface Surface { get; set; } = RoadSurface.Asphalt;
    [Export] public SidewalkLayout Sidewalks { get; set; } = SidewalkLayout.Both;
    [Export] public MedianKind Median { get; set; } = MedianKind.None;

    [ExportGroup("Widths")]
    [Export(PropertyHint.Range, "2,6,0.25")] public float LaneWidth { get; set; } = 3.5f;
    /// <summary>Each side, between the outer lane and the kerb: a gutter and room a later upgrade turns into parking or a
    /// bike lane without changing the road's width. 0 = lanes run to the kerb.</summary>
    [Export(PropertyHint.Range, "0,4,0.25")] public float StripWidth { get; set; }
    [Export(PropertyHint.Range, "1,8,0.25")] public float SidewalkWidth { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0.5,20,0.25")] public float MedianWidth { get; set; } = 2f;

    // How the road sits on the land. Set by whoever makes the road (base game or mod); the player can't change them.
    [ExportGroup("Elevation")]
    /// <summary>Steepest the road may climb, in percent. Ends placed further apart in height than this allows are red.</summary>
    [Export(PropertyHint.Range, "1,30,0.5,suffix:%")] public float MaxGrade { get; set; } = 10f;
    /// <summary>Metres along the road the ground is averaged over for its height: short hugs the hills, long runs
    /// straight through them on cuts and embankments.</summary>
    [Export(PropertyHint.Range, "0,500,1,suffix:m")] public float GroundSmoothing { get; set; } = 30f;
    /// <summary>The bank between the road and the natural ground, as run per rise (2.5 = 1:2.5, about 22°).</summary>
    [Export(PropertyHint.Range, "0.5,6,0.1")] public float SideSlope { get; set; } = 2.5f;
    /// <summary>Metres over which the road eases from a level junction to its grade (a vertical curve), so a junction on
    /// a hillside has no hard break. Longer is smoother but cuts or raises the road more near the junction.</summary>
    [Export(PropertyHint.Range, "0,100,1,suffix:m")] public float JunctionCurve { get; set; } = 20f;
    /// <summary>Deepest the road may cut into the ground under it, in metres; deeper is red.</summary>
    [Export(PropertyHint.Range, "0,30,0.5,suffix:m")] public float MaxCut { get; set; } = 4f;
    /// <summary>Highest the road may be raised above the ground under it, in metres; higher is red.</summary>
    [Export(PropertyHint.Range, "0,30,0.5,suffix:m")] public float MaxFill { get; set; } = 4f;

    public RoadDef ToDef() => new()
    {
        Id = Id,
        Lanes = Lanes,
        OneWay = OneWay,
        ForwardLanes = ForwardLanes,
        Surface = Surface,
        Sidewalks = Sidewalks,
        Median = Median,
        LaneWidth = LaneWidth,
        SidewalkWidth = SidewalkWidth,
        StripWidth = StripWidth,
        MedianWidth = MedianWidth,
    };

    public override string Badge => $"{Lanes} {(OneWay ? "→" : "⇄")}";
    public override string Summary => WidthText(ToDef());

    private static string WidthText(RoadDef d) => d.Width.ToString("0.#", CultureInfo.InvariantCulture) + " m";

    public override IEnumerable<(string Label, string Value)> Details()
    {
        var d = ToDef();
        yield return ("Lanes", OneWay ? $"{Lanes} · one-way" : d.Asymmetric ? $"{Lanes} · two-way ({d.Forward} + {d.Backward})" : $"{Lanes} · two-way");
        yield return ("Surface", Surface == RoadSurface.Gravel ? "Gravel" : "Asphalt");
        yield return ("Sidewalks", Sidewalks == SidewalkLayout.Both ? "Both sides" : "None");
        yield return ("Median", Median == MedianKind.Raised ? "Raised" : "None");
        yield return ("Width", WidthText(d));
        yield return ("Max grade", MaxGrade.ToString("0.#", CultureInfo.InvariantCulture) + "%");
    }

    protected override Control CreateThumbnail() => new RoadThumbnail { Road = ToDef() };
}
