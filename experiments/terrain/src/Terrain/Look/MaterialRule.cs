using Godot;

namespace CitySim.TerrainSystem.Look;

/// <summary>
/// One automatic ground rule: lays <see cref="Layer"/> over the layers before it, with coverage
/// <see cref="Strength"/> × condition A × condition B.
/// </summary>
[Tool, GlobalClass]
public partial class MaterialRule : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    /// <summary>
    /// Stable key the Materials panel's simple cards use to find the shipped rules (e.g. "shore_sand"), whatever their
    /// name or position. Empty for rules added by hand.
    /// </summary>
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "Rule";
    /// <summary>Ground layer index (<see cref="TerrainLayers"/>).</summary>
    [Export(PropertyHint.Enum, "Grass,Dry Grass,Grass & Dirt,Dirt,Gravel,Sand,Rock,Snow")]
    public int Layer { get; set; } = TerrainLayers.Dirt;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Strength { get; set; } = 1f;
    [Export] public RuleCondition A { get; set; } = new();
    [Export] public RuleCondition B { get; set; } = new();

    public static MaterialRule Make(string id, string name, int layer, float strength, RuleCondition a, RuleCondition? b = null) =>
        new() { Id = id, Name = name, Layer = layer, Strength = strength, A = a, B = b ?? new RuleCondition() };

    /// <summary>A deep copy (conditions included).</summary>
    public MaterialRule Copy() => new()
    {
        Enabled = Enabled, Id = Id, Name = Name, Layer = Layer, Strength = Strength, A = (A ?? new()).Copy(), B = (B ?? new()).Copy(),
    };
}
