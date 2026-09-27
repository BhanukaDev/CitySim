namespace CitySim.WaterSystem;

/// <summary>
/// The four kinds of water source, after the CS2 Water Features mod.
/// Stream: a constant flow rate. River: holds a level (water flows in or out); near the border it snaps onto it.
/// Lake: fills to its level at up to a maximum flow, never drains. Sea: holds the whole map border at sea level.
/// </summary>
public enum WaterSourceKind { Stream, River, Lake, Sea }

/// <summary>
/// A placed water source, in local map metres. <see cref="Level"/> is the target surface height (River, Lake, Sea);
/// <see cref="FlowRate"/> is m³/s (Stream); <see cref="MaxFlow"/> caps how fast a Lake fills (m³/s). Engine-agnostic.
/// </summary>
public sealed record WaterSource(int Id, WaterSourceKind Kind, float X, float Z, float Radius, float Level,
    float FlowRate = 0f, float MaxFlow = 0f)
{
    public static string Label(WaterSourceKind kind) => kind switch
    {
        WaterSourceKind.Stream => "Stream",
        WaterSourceKind.River => "River",
        WaterSourceKind.Lake => "Lake",
        _ => "Sea",
    };

    internal WaterNative.Source ToNative() => new()
    {
        Type = (int)(Kind switch
        {
            WaterSourceKind.Stream => WaterNative.SourceType.Stream,
            WaterSourceKind.River => WaterNative.SourceType.Level,
            WaterSourceKind.Lake => WaterNative.SourceType.Lake,
            _ => WaterNative.SourceType.Sea,
        }),
        X = X, Z = Z, Radius = Radius, Rate = FlowRate, Level = Level, MaxRate = MaxFlow,
    };
}

/// <summary>How the water behaves. Engine-agnostic.</summary>
public sealed record WaterSettings
{
    /// <summary>Simulated seconds per real second.</summary>
    public float Speed { get; init; } = 8f;
    /// <summary>Evaporation in millimetres per simulated minute: water nothing feeds dries up.</summary>
    public float EvaporationMmPerMin { get; init; } = 0.5f;
    /// <summary>Water runs off the map at its edges (otherwise the edges are walls).</summary>
    public bool OpenEdges { get; init; } = true;
    public bool Paused { get; init; }

    public static readonly float[] Speeds = [1f, 2f, 4f, 8f, 16f, 32f];
}
