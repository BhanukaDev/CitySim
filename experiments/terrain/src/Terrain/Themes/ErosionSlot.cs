using Godot;

namespace CitySim.TerrainSystem.Themes;

/// <summary>The erosion and water features a theme can give a material. Same order as the shader's slots.</summary>
public enum ErosionSlotKind { Deposit, DepositHeavy, Scour, ScourHeavy, ShoreFringe, Shore, StreamBank, StreamBed }

/// <summary>Which of the shader's noise fields moves a slot's edge.</summary>
public enum SlotNoise
{
    /// <summary>~10 m.</summary>
    Fine = 0,
    /// <summary>~35 m: shore and bank patches.</summary>
    Patchy = 1,
    /// <summary>~150 m.</summary>
    Medium = 2,
    /// <summary>~700 m.</summary>
    Large = 3,
}

/// <summary>Names and meaning of each <see cref="ErosionSlotKind"/>.</summary>
public sealed record ErosionSlotInfo(ErosionSlotKind Kind, string Name, string Define, string Mask, bool Below, string Help)
{
    public static readonly ErosionSlotInfo[] All =
    [
        new(ErosionSlotKind.Deposit, "Deposits", "SLOT_DEPOSIT", "deposit", false,
            "Sediment where running water slows down: fans at the foot of slopes, deltas at lakes."),
        new(ErosionSlotKind.DepositHeavy, "Thick deposits", "SLOT_DEPOSIT_HEAVY", "deposit", false,
            "The thickest deposits, laid over Deposits."),
        new(ErosionSlotKind.Scour, "Scoured ground", "SLOT_SCOUR", "wear", false,
            "Where running water wears the ground away."),
        new(ErosionSlotKind.ScourHeavy, "Heavily scoured", "SLOT_SCOUR_HEAVY", "wear", false,
            "The hardest scoured ground, laid over Scoured ground."),
        new(ErosionSlotKind.ShoreFringe, "Shore fringe", "SLOT_SHORE_FRINGE", "shore", true,
            "A wide band beyond the shore (worn grass, reeds...)."),
        new(ErosionSlotKind.Shore, "Shore", "SLOT_SHORE", "shore", true,
            "Along lakes, the sea and rivers (beach sand, pebbles, ice...)."),
        new(ErosionSlotKind.StreamBank, "Stream banks", "SLOT_STREAM_BANK", "gully", true,
            "A fringe along gully and stream beds."),
        new(ErosionSlotKind.StreamBed, "Stream beds", "SLOT_STREAM_BED", "gully", true,
            "The beds of gullies and streams."),
    ];

    public static ErosionSlotInfo For(ErosionSlotKind kind) => All[(int)kind];
}

/// <summary>
/// One erosion or water feature of a <see cref="TerrainTheme"/>: which material it lays and where its edge is. The masks
/// come from the lake and erosion search (M5.1), which a shader can't work out by itself. No material: the feature
/// isn't drawn and costs nothing.
/// <para>Shore and stream slots cover ground closer than <see cref="Edge"/> metres; scour and deposit slots cover ground
/// whose mask is above <see cref="Edge"/>.</para>
/// </summary>
[Tool, GlobalClass]
public partial class ErosionSlot : Resource
{
    /// <summary>The material to lay. Empty: this feature is off.</summary>
    [Export] public TerrainMaterial? Material { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float Strength { get; set; } = 1f;
    /// <summary>Distance (m) for shore and stream slots; wear or deposit level for the others.</summary>
    [Export] public float Edge { get; set; } = 1f;
    /// <summary>Width of the soft edge, in the same units.</summary>
    [Export] public float Fade { get; set; } = 1f;
    /// <summary>How far noise moves the edge (roughly ±⅓ of this).</summary>
    [Export] public float Noise { get; set; }
    [Export] public SlotNoise NoiseSize { get; set; } = SlotNoise.Fine;
    /// <summary>Only on ground flatter than <see cref="MaxSlope"/>.</summary>
    [Export] public bool LimitSlope { get; set; }
    [Export(PropertyHint.Range, "0,90,0.5,suffix:°")] public float MaxSlope { get; set; } = 45f;
    [Export(PropertyHint.Range, "0.5,45,0.5,suffix:°")] public float SlopeFade { get; set; } = 8f;

    /// <summary>The tuned settings of each slot (the default theme's), so a new theme only has to pick materials.</summary>
    public static ErosionSlot Default(ErosionSlotKind kind) => kind switch
    {
        ErosionSlotKind.Deposit => Make(0.7f, 1.8f, 1f, 1f, SlotNoise.Fine),
        ErosionSlotKind.DepositHeavy => Make(0.7f, 2.8f, 1.2f, 1.5f, SlotNoise.Medium),
        ErosionSlotKind.Scour => Make(0.8f, 3f, 1f, 1f, SlotNoise.Fine),
        ErosionSlotKind.ScourHeavy => Make(0.8f, 4f, 1f, 1f, SlotNoise.Fine),
        ErosionSlotKind.ShoreFringe => Make(0.6f, 16f, 26f, 16f, SlotNoise.Patchy, 42f, 8f),
        ErosionSlotKind.Shore => Make(1f, 5f, 18f, 14f, SlotNoise.Patchy, 42f, 8f),
        ErosionSlotKind.StreamBank => Make(0.4f, 2f, 6f, 3f, SlotNoise.Fine),
        ErosionSlotKind.StreamBed => Make(0.85f, -0.5f, 4f, 2f, SlotNoise.Fine, 50f, 10f),
        _ => new ErosionSlot(),
    };

    private static ErosionSlot Make(float strength, float edge, float fade, float noise, SlotNoise size,
        float maxSlope = -1f, float slopeFade = 8f) => new()
    {
        Strength = strength, Edge = edge, Fade = fade, Noise = noise, NoiseSize = size,
        LimitSlope = maxSlope >= 0f, MaxSlope = maxSlope >= 0f ? maxSlope : 45f, SlopeFade = slopeFade,
    };

    /// <summary>The shader's <c>slot_edge</c> value (strength, edge, 1 / fade, noise).</summary>
    public Vector4 PackEdge() => new(Strength, Edge, 1f / Mathf.Max(Fade, 1e-4f), Noise);

    /// <summary>The shader's <c>slot_slope</c> value (noise field, max slope, 1 / slope fade, limit on).</summary>
    public Vector4 PackSlope() => new((int)NoiseSize, MaxSlope, 1f / Mathf.Max(SlopeFade, 1e-4f), LimitSlope ? 1 : 0);
}
