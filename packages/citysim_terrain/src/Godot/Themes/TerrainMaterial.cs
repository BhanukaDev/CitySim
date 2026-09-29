using Godot;

namespace CitySim.TerrainSystem.Themes;

/// <summary>
/// One ground material of a <see cref="TerrainTheme"/>: its source textures, colour and tiling. The source textures are
/// only read by <see cref="ThemeBaker"/>, which packs every material of a theme into two texture arrays, so they are file
/// paths, not loaded textures, and the game never loads them.
/// </summary>
[Tool, GlobalClass]
public partial class TerrainMaterial : Resource
{
    /// <summary>
    /// Stable name, unique in its theme (lower_case). Painted maps store it, so painted ground survives theme changes and
    /// material reordering; the shader gets it as <c>MAT_&lt;ID&gt;</c>.
    /// </summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";

    [ExportGroup("Textures")]
    /// <summary>Colour. Only its light/dark detail is kept: the baker normalises it to grey and <see cref="Tint"/> colours it.</summary>
    [Export(PropertyHint.File, "*.png,*.jpg,*.jpeg,*.webp,*.tga,*.exr")] public string Albedo { get; set; } = "";
    /// <summary>Height/displacement for blending borders (grass between stones). Empty: made from the colour's brightness.</summary>
    [Export(PropertyHint.File, "*.png,*.jpg,*.jpeg,*.webp,*.tga,*.exr")] public string Height { get; set; } = "";
    /// <summary>Normal map, OpenGL convention (green up). Empty: flat.</summary>
    [Export(PropertyHint.File, "*.png,*.jpg,*.jpeg,*.webp,*.tga,*.exr")] public string Normal { get; set; } = "";

    [ExportGroup("Look")]
    /// <summary>The material's colour; the texture multiplies it with its detail.</summary>
    [Export] public Color Tint { get; set; } = new(0.5f, 0.5f, 0.5f);
    /// <summary>Metres per texture repeat up close.</summary>
    [Export(PropertyHint.Range, "0.5,200,0.1,suffix:m")] public float TileSize { get; set; } = 6f;
    /// <summary>Metres per repeat far away (blended in with distance to hide tiling); for triplanar, the second scale mixed in by noise.</summary>
    [Export(PropertyHint.Range, "0.5,500,0.1,suffix:m")] public float FarTileSize { get; set; } = 32f;
    /// <summary>Projects from three sides, so steep faces don't stretch (cliffs). Costs about three times as much.</summary>
    [Export] public bool Triplanar { get; set; }
    /// <summary>Multiplies the theme's detail contrast for this material.</summary>
    [Export(PropertyHint.Range, "0,3,0.01")] public float DetailContrast { get; set; } = 1f;
    /// <summary>Multiplies the theme's normal strength for this material.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float NormalStrength { get; set; } = 1f;

    /// <summary>Shown in the Paint tool.</summary>
    [Export] public bool Paintable { get; set; } = true;

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
}
