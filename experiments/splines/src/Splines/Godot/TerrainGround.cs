using CitySim.TerrainSystem;

namespace CitySim.Splines.Godot;

/// <summary>
/// Adapts <see cref="Terrain"/> to <see cref="IGround"/>: the only place plan-space (map metres) and world-space
/// vectors get converted for the Draw tool. <see cref="Raycast"/> takes a world-space camera ray (that's genuinely
/// what a screen pick is); <see cref="GetHeight"/> takes plan-space x/z and reads the map directly, with no
/// world-space round trip.
/// </summary>
public sealed class TerrainGround : IGround
{
    private readonly Terrain _terrain;

    public TerrainGround(Terrain terrain) => _terrain = terrain;

    public bool Raycast(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, out System.Numerics.Vector3 hit)
    {
        bool ok = _terrain.Raycast(ToGodot(origin), ToGodot(direction), out var godotHit);
        hit = ok ? ToNumerics(godotHit) : default;
        return ok;
    }

    public float GetHeight(System.Numerics.Vector2 planPosition) => _terrain.GetHeightAtMap(planPosition.X, planPosition.Y);

    private static global::Godot.Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    private static System.Numerics.Vector3 ToNumerics(global::Godot.Vector3 v) => new(v.X, v.Y, v.Z);
}
