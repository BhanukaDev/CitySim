using Godot;

namespace CitySim.TerrainSystem.Look;

/// <summary>What a rule condition reads at each point of the ground. Values match the shader's <c>inputs[]</c>.</summary>
public enum RuleInput
{
    None = -1,
    Height = 0,
    HeightRelative = 1,
    Slope = 2,
    Shore = 3,
    Gully = 4,
    Wear = 5,
    Deposit = 6,
    NoiseLarge = 7,
    NoiseMedium = 8,
    NoiseFine = 9,
    NoisePatchy = 10,
}

/// <summary>
/// Which of the shader's noise fields moves a condition's edges. They're sampled anyway, so edge noise costs nothing
/// extra (a free size per condition cost ~2 FPS each on an M1). Values index the shader's <c>edge_noises</c>.
/// </summary>
public enum NoiseScale
{
    /// <summary>~10 m (<c>edge_noise_scale</c>).</summary>
    Fine = 0,
    /// <summary>~35 m (<c>patchy_noise_scale</c>): shore and bank patches.</summary>
    Patchy = 1,
    /// <summary>~150 m.</summary>
    Medium = 2,
    /// <summary>~700 m (<c>macro_scale</c>).</summary>
    Large = 3,
}

/// <summary>Display name, units and a sensible slider range per <see cref="RuleInput"/>.</summary>
public sealed record RuleInputInfo(RuleInput Input, string Name, string Format, float Min, float Max, float Step, string Help)
{
    public static readonly RuleInputInfo[] All =
    [
        new(RuleInput.None, "(none)", "{0:0}", 0, 1, 1, "Not used"),
        new(RuleInput.Height, "Height", "{0:0} m", -50, 800, 1, "Height above zero, in metres"),
        new(RuleInput.HeightRelative, "Height (relative)", "{0:0%}", 0, 1, 0.01f, "Height within this map's lowest to highest point"),
        new(RuleInput.Slope, "Slope", "{0:0}°", 0, 90, 0.5f, "Steepness of the ground, in degrees"),
        new(RuleInput.Shore, "Shore Distance", "{0:0.0} m", -16, 64, 0.5f,
            "Distance to lakes, the sea and rivers; each metre above the water counts as 4. 64 m and beyond reads as 64"),
        new(RuleInput.Gully, "Gully Distance", "{0:0.0} m", -8, 24, 0.25f, "Distance to gully and stream beds (negative inside the bed)"),
        new(RuleInput.Wear, "Wear", "{0:0.0}", 0, 8, 0.1f,
            "How hard running water scours (log of stream power). On an eroded map ~10% of the ground is above 1.8, 1% above 2.9"),
        new(RuleInput.Deposit, "Deposit", "{0:0.0}", 0, 8, 0.1f,
            "Sediment settling where water slows: fans at slope feet, deltas at lakes. ~1% of the ground is above 1.2"),
        new(RuleInput.NoiseLarge, "Noise (large)", "{0:0.00}", 0, 1, 0.01f, "Low-frequency noise, patches a few hundred metres across"),
        new(RuleInput.NoiseMedium, "Noise (medium)", "{0:0.00}", 0, 1, 0.01f, "Noise with patches about 150 m across"),
        new(RuleInput.NoiseFine, "Noise (fine)", "{0:0.00}", 0, 1, 0.01f, "Fine noise, patches about 10 m across"),
        new(RuleInput.NoisePatchy, "Noise (patchy)", "{0:0.00}", 0, 1, 0.01f, "Noise with patches about 35 m across"),
    ];

    public static RuleInputInfo For(RuleInput input) => All[(int)input + 1];
}

/// <summary>
/// One condition of a <see cref="MaterialRule"/>: full coverage while the input is between <see cref="From"/> and
/// <see cref="To"/>, fading to none over <see cref="Fade"/> outside that range. Noise moves the input, so the edges wander.
/// </summary>
[Tool, GlobalClass]
public partial class RuleCondition : Resource
{
    [Export] public RuleInput Input { get; set; } = RuleInput.None;
    [Export] public float From { get; set; }
    /// <summary>No lower end: everything below <see cref="To"/> counts.</summary>
    [Export] public bool FromOpen { get; set; } = true;
    [Export] public float To { get; set; }
    /// <summary>No upper end: everything above <see cref="From"/> counts.</summary>
    [Export] public bool ToOpen { get; set; } = true;
    /// <summary>Width of the blend outside the range, in the input's units. The main softness knob.</summary>
    [Export] public float Fade { get; set; } = 1f;
    /// <summary>How far noise moves the edges (roughly ±⅓ of this, in the input's units).</summary>
    [Export] public float Noise { get; set; }
    /// <summary>Size of the noise pattern.</summary>
    [Export] public NoiseScale NoiseSize { get; set; } = NoiseScale.Fine;
    /// <summary>Covers everything outside the range instead.</summary>
    [Export] public bool Invert { get; set; }

    public static RuleCondition Above(RuleInput input, float from, float fade, float noise = 0f, NoiseScale noiseSize = NoiseScale.Fine) =>
        new() { Input = input, From = from, FromOpen = false, Fade = fade, Noise = noise, NoiseSize = noiseSize };

    public static RuleCondition Below(RuleInput input, float to, float fade, float noise = 0f, NoiseScale noiseSize = NoiseScale.Fine) =>
        new() { Input = input, To = to, ToOpen = false, Fade = fade, Noise = noise, NoiseSize = noiseSize };

    public RuleCondition Copy() => (RuleCondition)Duplicate();
}
