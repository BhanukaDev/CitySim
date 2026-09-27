using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.TerrainSystem.Themes;

/// <summary>
/// A terrain look, made in the Godot editor: the theme's shader (with its tuned uniforms), its ground materials and the
/// materials for erosion and water features. Lives at <c>themes/&lt;id&gt;/theme.tres</c> next to its shader;
/// <see cref="ThemeBaker"/> writes the <c>baked/</c> folder (texture arrays, previews) and <c>materials.gdshaderinc</c>.
/// Each map stores the id of its theme. Nothing about grass, sand or rock is built into the game: that's all in the
/// default theme.
/// </summary>
[Tool, GlobalClass]
public partial class TerrainTheme : Resource
{
    /// <summary>Most materials a theme can have (the shader's <c>MAX_MATERIALS</c>).</summary>
    public const int MaxMaterials = 16;
    public const string FileName = "theme.tres";
    public const string IncludeName = "materials.gdshaderinc";

    /// <summary>Stable name, the theme's folder name. Maps store it.</summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";

    /// <summary>
    /// The theme's shader and its tuned uniforms. The shader includes <c>res://terrain_sdk/terrain_core.gdshaderinc</c>
    /// (see terrain_sdk/README.md). Its texture arrays and per-material uniforms are set by the game.
    /// </summary>
    [Export] public ShaderMaterial? Material { get; set; }

    /// <summary>The ground materials, in shader order. The first one is the base: ground no rule covers.</summary>
    [Export] public Godot.Collections.Array<TerrainMaterial> Materials { get; set; } = new();

    [ExportGroup("Erosion slots")]
    [Export] public ErosionSlot Deposit { get; set; } = ErosionSlot.Default(ErosionSlotKind.Deposit);
    [Export] public ErosionSlot DepositHeavy { get; set; } = ErosionSlot.Default(ErosionSlotKind.DepositHeavy);
    [Export] public ErosionSlot Scour { get; set; } = ErosionSlot.Default(ErosionSlotKind.Scour);
    [Export] public ErosionSlot ScourHeavy { get; set; } = ErosionSlot.Default(ErosionSlotKind.ScourHeavy);
    [Export] public ErosionSlot ShoreFringe { get; set; } = ErosionSlot.Default(ErosionSlotKind.ShoreFringe);
    [Export] public ErosionSlot Shore { get; set; } = ErosionSlot.Default(ErosionSlotKind.Shore);
    [Export] public ErosionSlot StreamBank { get; set; } = ErosionSlot.Default(ErosionSlotKind.StreamBank);
    [Export] public ErosionSlot StreamBed { get; set; } = ErosionSlot.Default(ErosionSlotKind.StreamBed);

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>The theme's folder (<c>res://themes/&lt;id&gt;</c>).</summary>
    public string Dir => ResourcePath.GetBaseDir();
    public string AlbedoArrayPath => Dir + "/baked/albedo_height.png";
    public string NormalArrayPath => Dir + "/baked/normal.png";
    public string IncludePath => Dir + "/" + IncludeName;
    public string PreviewPath(TerrainMaterial m) => Dir + "/baked/preview_" + m.Id + ".png";

    /// <summary>The slots in shader order.</summary>
    public ErosionSlot[] Slots => [Deposit, DepositHeavy, Scour, ScourHeavy, ShoreFringe, Shore, StreamBank, StreamBed];

    /// <summary>Material ids in shader order: what a map's painted indices mean with this theme.</summary>
    public string[] MaterialIds()
    {
        var ids = new string[Materials.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = Materials[i]?.Id ?? "";
        return ids;
    }

    /// <summary>Index of the material with this id, or -1.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < Materials.Count; i++)
            if (Materials[i]?.Id == id) return i;
        return -1;
    }

    /// <summary>Shader index of a slot's material, or -1 when the slot is off (no material, or one not in <see cref="Materials"/>).</summary>
    public int SlotMaterialIndex(ErosionSlot? slot)
    {
        if (slot?.Material is not { } m) return -1;
        int i = Materials.IndexOf(m);
        return i >= 0 ? i : IndexOf(m.Id);
    }

    /// <summary>The shader's per-material uniforms: tint (linear rgb, a = triplanar) and tiling/detail.</summary>
    public (Vector4[] Tints, Vector4[] Params) PackMaterials()
    {
        var tints = new Vector4[MaxMaterials];
        var prms = new Vector4[MaxMaterials];
        for (int i = 0; i < MaxMaterials; i++)
        {
            prms[i] = new Vector4(6f, 32f, 1f, 1f);
            if (i >= Materials.Count || Materials[i] is not { } m) continue;
            var c = m.Tint.SrgbToLinear();
            tints[i] = new Vector4(c.R, c.G, c.B, m.Triplanar ? 1 : 0);
            prms[i] = new Vector4(Mathf.Max(m.TileSize, 0.01f), Mathf.Max(m.FarTileSize, 0.01f), m.DetailContrast, m.NormalStrength);
        }
        return (tints, prms);
    }

    /// <summary>The shader's per-slot uniforms (see <see cref="ErosionSlot.PackEdge"/>).</summary>
    public (Vector4[] Edge, Vector4[] Slope) PackSlots()
    {
        var slots = Slots;
        var edge = new Vector4[slots.Length];
        var slope = new Vector4[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i] ?? ErosionSlot.Default((ErosionSlotKind)i);
            edge[i] = s.PackEdge();
            slope[i] = s.PackSlope();
        }
        return (edge, slope);
    }

    /// <summary>
    /// The shader include the theme's shader reads: material and slot defines. <see cref="ThemeBaker"/> writes it; the
    /// game compares it with the file to warn about an unbaked theme.
    /// </summary>
    public string MakeInclude()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("// Generated by ThemeBaker from ").Append(FileName)
          .Append(". Don't edit: press Bake on the theme in the inspector (or run --bake-theme=").Append(Id).Append(").\n");
        sb.Append($"#define MATERIAL_COUNT {Math.Max(Materials.Count, 1)}\n");
        for (int i = 0; i < Materials.Count; i++)
            if (Materials[i] is { } m) sb.Append($"#define MAT_{DefineName(m.Id)} {i}\n");
        var slots = Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            int m = SlotMaterialIndex(slots[i]);
            if (m >= 0) sb.Append($"#define {ErosionSlotInfo.All[i].Define} {m}\n");
        }
        return sb.ToString();
    }

    public static string DefineName(string id)
    {
        var chars = id.ToUpperInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsAsciiLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    /// <summary>Problems that stop the theme from working, for the Validate button and the game's log. Empty when fine.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) problems.Add("Id is empty.");
        if (Material?.Shader is not { } shader) problems.Add("Material has no shader.");
        else if (!shader.Code.Contains("terrain_core.gdshaderinc"))
            problems.Add("The shader doesn't include res://terrain_sdk/terrain_core.gdshaderinc.");
        if (Materials.Count == 0) problems.Add("The theme has no materials.");
        if (Materials.Count > MaxMaterials) problems.Add($"More than {MaxMaterials} materials.");
        var seen = new HashSet<string>();
        foreach (var m in Materials)
        {
            if (m is null) { problems.Add("Empty entry in Materials."); continue; }
            if (string.IsNullOrWhiteSpace(m.Id)) problems.Add($"A material ('{m.DisplayName}') has no Id.");
            else if (!seen.Add(m.Id)) problems.Add($"Two materials have the id '{m.Id}'.");
            if (string.IsNullOrEmpty(m.Albedo)) problems.Add($"Material '{m.Id}' has no Albedo texture.");
        }
        var slots = Slots;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i]?.Material is { } sm && SlotMaterialIndex(slots[i]) < 0)
                problems.Add($"Slot '{ErosionSlotInfo.All[i].Name}' uses '{sm.Id}', which isn't in Materials.");
        return problems;
    }
}
