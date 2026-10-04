using CitySim.TerrainSystem;

namespace CitySim.Splines.Godot;

/// <summary>
/// Adapts an open <see cref="TerrainEdit"/> to <see cref="IHeightGrid"/> for the ground shaping: world heights (the
/// map's plus the terrain's own height), map vertex indices, and <see cref="Touch"/> saving the undo copy.
/// </summary>
public sealed class TerrainHeightGrid : IHeightGrid
{
    private readonly TerrainEdit _edit;
    private readonly float _y0;

    public TerrainHeightGrid(TerrainEdit edit, Terrain terrain)
    {
        _edit = edit;
        _y0 = terrain.GlobalPosition.Y;
    }

    public float CellSize => _edit.Heights.CellSize;
    public int Width => _edit.Heights.Width;
    public int Depth => _edit.Heights.Depth;

    public float this[int x, int z]
    {
        get => _edit.Heights[x, z] + _y0;
        set => _edit.Heights[x, z] = value - _y0;
    }

    public void Touch(int minX, int minZ, int maxX, int maxZ) => _edit.Touch(new VertexRect(minX, minZ, maxX, maxZ));
}
