namespace CitySim.Roads;

public enum SidewalkLayout { None, Both }

public enum MedianKind { None, Raised }

public enum RoadSurface { Asphalt, Gravel }

/// <summary>
/// A road type's base layout as plain data (no Godot types), for the simulation and the road addon. The base layout
/// is what the road is built with: its lanes, sidewalks and median. Upgrades only change how it looks. Metres.
/// </summary>
public sealed record RoadDef
{
    public required string Id { get; init; }
    /// <summary>Driving lanes in total, both directions together.</summary>
    public int Lanes { get; init; } = 2;
    public bool OneWay { get; init; }
    /// <summary>Two-way only: lanes in the forward direction, for asymmetric roads (3 = 2 + 1 has 2). 0 = even split.</summary>
    public int ForwardLanes { get; init; }
    public RoadSurface Surface { get; init; } = RoadSurface.Asphalt;
    public SidewalkLayout Sidewalks { get; init; } = SidewalkLayout.Both;
    public MedianKind Median { get; init; } = MedianKind.None;
    public float LaneWidth { get; init; } = 3.5f;
    public float SidewalkWidth { get; init; } = 2.5f;
    /// <summary>Each side, between the outer lane and the kerb (gutter, later parking or a bike lane).</summary>
    public float StripWidth { get; init; }
    public float MedianWidth { get; init; } = 2f;

    /// <summary>Lanes going forward (all of them on a one-way road).</summary>
    public int Forward => OneWay ? Lanes : ForwardLanes > 0 ? ForwardLanes : (Lanes + 1) / 2;
    /// <summary>Lanes going the other way (0 on a one-way road).</summary>
    public int Backward => Lanes - Forward;
    public bool Asymmetric => !OneWay && Forward != Backward;

    /// <summary>Kerb to kerb.</summary>
    public float CarriagewayWidth => Lanes * LaneWidth + 2f * StripWidth
        + (Median == MedianKind.None ? 0f : MedianWidth);

    /// <summary>Kerb to kerb plus sidewalks.</summary>
    public float Width => CarriagewayWidth
        + (Sidewalks == SidewalkLayout.Both ? 2f * SidewalkWidth : 0f);
}
