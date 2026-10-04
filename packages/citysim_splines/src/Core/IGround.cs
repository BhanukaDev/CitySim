using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// The terrain, as Core needs it (DESIGN.md → Hooks): picking and height sampling only. No Godot types — the Godot
/// side adapts <c>citysim_terrain</c>. <see cref="Raycast"/> is world space (a camera ray); <see cref="GetHeight"/>
/// is plan space (map metres, <c>Vector2(x, z)</c>, the same convention as <see cref="Pi"/>).
/// </summary>
public interface IGround
{
    bool Raycast(Vector3 origin, Vector3 direction, out Vector3 hit);
    float GetHeight(Vector2 planPosition);
}
