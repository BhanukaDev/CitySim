namespace CitySim.TerrainSystem.Erosion;

/// <summary>
/// Droplet erosion plus a thermal pass (see <c>native/erosion/erosion.cpp</c>). Engine-agnostic; copied with <c>with</c>.
/// Slopes are unitless (height change per cell / cell size), so the same settings suit any cell size.
/// </summary>
public sealed record ErosionSettings
{
    public int Seed { get; init; } = 1;
    /// <summary>Droplets per map cell over the whole run: how much rain falls.</summary>
    public float Droplets { get; init; } = 1f;
    /// <summary>Steps a droplet lives (it moves one cell per step), so also how far it can carve.</summary>
    public int Lifetime { get; init; } = 80;
    /// <summary>0 = runs straight down the slope, 1 = keeps its direction (smoother, wider channels).</summary>
    public float Inertia { get; init; } = 0.1f;
    /// <summary>How much sediment a droplet can carry (per unit of speed, water and slope).</summary>
    public float Capacity { get; init; } = 4f;
    /// <summary>Share of the missing capacity taken from the ground each step.</summary>
    public float ErodeSpeed { get; init; } = 0.3f;
    /// <summary>Share of the excess sediment dropped each step.</summary>
    public float DepositSpeed { get; init; } = 0.3f;
    public float Evaporation { get; init; } = 0.02f;
    public float Gravity { get; init; } = 4f;
    /// <summary>Erosion is spread over this radius, in metres (converted to cells).</summary>
    public float Radius { get; init; } = 10f;
    /// <summary>Most a single step may take from one cell, in metres.</summary>
    public float MaxErodeDepth { get; init; } = 0.5f;
    /// <summary>Thermal passes after the droplets: slopes steeper than <see cref="TalusDegrees"/> slump.</summary>
    public int ThermalIterations { get; init; } = 20;
    /// <summary>
    /// After the rain, hollows that a channel no deeper than this (metres) can drain get one, cut from their lowest
    /// point over the rim, as a river would. Deeper basins stay closed and hold lakes. 0 = off.
    /// </summary>
    public float DrainDepth { get; init; } = 2f;
    public float TalusDegrees { get; init; } = 38f;
    public float ThermalRate { get; init; } = 0.5f;

    /// <summary>Name of the preset these settings started from (for the UI), or null.</summary>
    public string? Preset { get; init; }
}

public sealed record ErosionPreset(string Name, string Description, ErosionSettings Settings)
{
    /// <summary>The preset's settings, keeping the seed.</summary>
    public ErosionSettings ApplyTo(ErosionSettings s) => Settings with { Seed = s.Seed, Preset = Name };
}

public static class ErosionPresets
{
    public static readonly ErosionPreset[] All =
    [
        new("Light", "A light rain: softens hills and starts small gullies", new() { Droplets = 0.4f }),
        new("Medium", "Gullies on the slopes, sediment fans at their feet", new()),
        new("Heavy", "Deep, branching valleys and wide deposits", new() { Droplets = 2.5f, Lifetime = 120, Capacity = 6f }),
    ];

    public static ErosionPreset Default => All[1];

    public static ErosionPreset? Find(string name) =>
        System.Array.Find(All, p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which filled depressions count as lakes.</summary>
public sealed record LakeSettings
{
    /// <summary>Shallower depressions stay dry, in metres.</summary>
    public float MinDepth { get; init; } = 1f;
    /// <summary>Smaller depressions stay dry, in square metres.</summary>
    public float MinArea { get; init; } = 5000f;
}
