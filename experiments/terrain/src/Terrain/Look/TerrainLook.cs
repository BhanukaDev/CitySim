using System;
using Godot;

namespace CitySim.TerrainSystem.Look;

/// <summary>
/// How the ground is textured: the ordered stack of automatic <see cref="MaterialRule"/>s, each layer's tint, and how
/// layers blend at their borders. One shared file (<see cref="DefaultPath"/>) is used by every map; the Materials
/// panel edits it live and saves it, and the inspector can edit it too. <see cref="Terrain.ApplyLook"/> sends it to
/// the shader (<c>terrain.gdshader</c>, "Rules" group).
/// </summary>
[Tool, GlobalClass]
public partial class TerrainLook : Resource
{
    public const string DefaultPath = "res://materials/terrain_look.tres";
    /// <summary>Most rules the shader evaluates (its <c>MAX_RULES</c>).</summary>
    public const int MaxRules = 24;
    /// <summary>Range ends are sent as ±this when open; the shader treats anything beyond 1e5 as open.</summary>
    private const float Open = 1e6f;

    /// <summary>Laid bottom to top over grass: each rule covers the ones before it by its coverage.</summary>
    [Export] public Godot.Collections.Array<MaterialRule> Rules { get; set; } = new();
    /// <summary>Each layer's colour, in <see cref="TerrainLayers"/> order. The texture only adds light/dark detail.</summary>
    [Export] public Color[] Tints { get; set; } = (Color[])DefaultTints.Clone();
    /// <summary>How much texture height decides layer borders (grass fills between stones, etc.).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBlend { get; set; } = 0.12f;
    /// <summary>Width of height-blended borders: lower is crisper.</summary>
    [Export(PropertyHint.Range, "0.01,0.6,0.01")] public float BlendSoftness { get; set; } = 0.45f;

    public static readonly Color[] DefaultTints =
    [
        new(0.40f, 0.62f, 0.20f), // grass
        new(0.68f, 0.66f, 0.26f), // grass_dry
        new(0.50f, 0.55f, 0.24f), // grass_dirt
        new(0.60f, 0.43f, 0.28f), // dirt
        new(0.66f, 0.58f, 0.48f), // gravel
        new(0.87f, 0.75f, 0.50f), // sand
        new(0.60f, 0.56f, 0.50f), // rock
        new(0.96f, 0.96f, 1.00f), // snow
    ];

    public Color Tint(int layer) => layer < Tints.Length ? Tints[layer] : DefaultTints[layer];

    public void SetTint(int layer, Color c)
    {
        if (Tints.Length < DefaultTints.Length)
        {
            var t = (Color[])DefaultTints.Clone();
            Array.Copy(Tints, t, Tints.Length);
            Tints = t;
        }
        Tints[layer] = c;
    }

    /// <summary>Replaces this look's contents with a deep copy of <paramref name="other"/>'s (keeps this resource's path).</summary>
    public void CopyFrom(TerrainLook other)
    {
        var rules = new Godot.Collections.Array<MaterialRule>();
        foreach (var r in other.Rules)
            if (r is not null) rules.Add(r.Copy());
        Rules = rules;
        Tints = (Color[])other.Tints.Clone();
        HeightBlend = other.HeightBlend;
        BlendSoftness = other.BlendSoftness;
    }

    /// <summary>The first rule with this <see cref="MaterialRule.Id"/>, or null.</summary>
    public MaterialRule? Find(string id)
    {
        foreach (var r in Rules)
            if (r is not null && r.Id == id) return r;
        return null;
    }

    /// <summary>The shader's rule index of <paramref name="rule"/> (disabled rules aren't sent), or -1.</summary>
    public int ShaderIndex(MaterialRule rule)
    {
        int i = 0;
        foreach (var r in Rules)
        {
            if (r is null || !r.Enabled) continue;
            if (r == rule) return i < MaxRules ? i : -1;
            i++;
        }
        return -1;
    }

    /// <summary>The enabled rules as the shader's <c>rules[]</c>: four vec4 per rule, padded to <see cref="MaxRules"/>.</summary>
    public Vector4[] Pack(out int count)
    {
        var packed = new Vector4[MaxRules * 4];
        count = 0;
        foreach (var rule in Rules)
        {
            if (rule is null || !rule.Enabled) continue;
            if (count == MaxRules) break;
            var a = rule.A ?? new RuleCondition();
            var b = rule.B ?? new RuleCondition();
            int i = count++ * 4;
            packed[i] = new Vector4(rule.Layer, rule.Strength, a.Invert ? 1 : 0, b.Invert ? 1 : 0);
            packed[i + 1] = PackCondition(a);
            packed[i + 2] = PackCondition(b);
            packed[i + 3] = new Vector4(a.Noise, (int)a.NoiseSize, b.Noise, (int)b.NoiseSize);
        }
        return packed;
    }

    // (input, where the rise starts (from - fade), where the fall starts (to), 1 / fade): the shader's ramps are then a
    // multiply and a clamp. Uniform smoothstep edges cost a division each, which made 14 rules ~3x slower on an M1.
    private static Vector4 PackCondition(RuleCondition c)
    {
        float fade = Math.Max(c.Fade, 1e-4f);
        return new((int)c.Input, c.FromOpen ? -Open : c.From - fade, c.ToOpen ? Open : c.To, 1f / fade);
    }

    /// <summary>
    /// The shipped look. The mountain rules match the original hand-written shader (M3/M5.1); the water rules are
    /// softer: wide, noisy sand edges with worn grass beyond them, and a dirt fringe around gully gravel. Shore distance
    /// counts each metre above the water as 4, so its fades need to be wide to cover a few metres of gentle bank. No snow
    /// line (snow is paint-only).
    /// </summary>
    public static TerrainLook CreateDefault()
    {
        const int grassDry = TerrainLayers.GrassDry, grassDirt = TerrainLayers.GrassDirt, dirt = TerrainLayers.Dirt,
            gravel = TerrainLayers.Gravel, sand = TerrainLayers.Sand, rock = TerrainLayers.Rock;
        var look = new TerrainLook();
        MaterialRule[] rules =
        [
            MaterialRule.Make("dry_patches", "Dry grass patches", grassDry, 0.5f,
                RuleCondition.Above(RuleInput.NoiseLarge, 0.8f, 0.45f, 0.15f)),
            MaterialRule.Make("dry_high", "Dry grass up high", grassDry, 0.4f,
                RuleCondition.Above(RuleInput.HeightRelative, 0.7f, 0.5f),
                RuleCondition.Above(RuleInput.NoiseLarge, 0.55f, 0.3f, 0.15f)),
            MaterialRule.Make("worn_slopes", "Worn slopes", grassDirt, 1f,
                RuleCondition.Above(RuleInput.Slope, 41f, 17f, 4f)),
            MaterialRule.Make("deposits", "Deposits", dirt, 0.7f,
                RuleCondition.Above(RuleInput.Deposit, 1.8f, 1f, 1f)),
            MaterialRule.Make("thick_deposits", "Thick deposits", gravel, 0.7f,
                RuleCondition.Above(RuleInput.Deposit, 2.8f, 1.2f, 1.5f, NoiseScale.Medium)),
            MaterialRule.Make("scoured_dirt", "Scoured dirt", dirt, 0.8f,
                RuleCondition.Above(RuleInput.Wear, 3f, 1f, 1f)),
            MaterialRule.Make("scoured_gravel", "Scoured gravel", gravel, 0.8f,
                RuleCondition.Above(RuleInput.Wear, 4f, 1f, 1f)),
            MaterialRule.Make("scree", "Scree", gravel, 0.5f,
                RuleCondition.Above(RuleInput.Slope, 44f, 7f, 4f),
                RuleCondition.Above(RuleInput.NoiseMedium, 0.65f, 0.3f)),
            MaterialRule.Make("cliffs", "Cliffs", rock, 1f,
                RuleCondition.Above(RuleInput.Slope, 49.5f, 8f, 4f)),
            MaterialRule.Make("scoured_rock", "Scoured rock", rock, 1f,
                RuleCondition.Above(RuleInput.Slope, 45.5f, 7f, 4f),
                RuleCondition.Above(RuleInput.Wear, 4f, 2f)),
            MaterialRule.Make("shore_worn", "Shore: worn grass", grassDirt, 0.6f,
                RuleCondition.Below(RuleInput.Shore, 16f, 26f, 16f, NoiseScale.Patchy),
                RuleCondition.Below(RuleInput.Slope, 42f, 8f)),
            MaterialRule.Make("shore_sand", "Shore: sand", sand, 1f,
                RuleCondition.Below(RuleInput.Shore, 5f, 18f, 14f, NoiseScale.Patchy),
                RuleCondition.Below(RuleInput.Slope, 42f, 8f)),
            MaterialRule.Make("gully_banks", "Gully banks", dirt, 0.4f,
                RuleCondition.Below(RuleInput.Gully, 2f, 6f, 3f)),
            MaterialRule.Make("gully_beds", "Gully beds", gravel, 0.85f,
                RuleCondition.Below(RuleInput.Gully, -0.5f, 4f, 2f),
                RuleCondition.Below(RuleInput.Slope, 50f, 10f)),
        ];
        foreach (var r in rules) look.Rules.Add(r);
        return look;
    }

    /// <summary>Loads the shared look, or returns the default when the file is missing or unreadable.</summary>
    public static TerrainLook LoadOrDefault(string path = DefaultPath, bool fresh = false)
    {
        if (ResourceLoader.Exists(path) &&
            ResourceLoader.Load(path, cacheMode: fresh ? ResourceLoader.CacheMode.Replace : ResourceLoader.CacheMode.Reuse) is TerrainLook look)
            return look;
        GD.PushWarning($"TerrainLook: '{path}' not found, using the default look.");
        return CreateDefault();
    }
}
