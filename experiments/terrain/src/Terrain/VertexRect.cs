using System;

namespace CitySim.TerrainSystem;

/// <summary>Inclusive rectangle of heightmap vertex indices. Empty when Max &lt; Min.</summary>
public readonly record struct VertexRect(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public static readonly VertexRect Empty = new(0, 0, -1, -1);

    public bool IsEmpty => MaxX < MinX || MaxZ < MinZ;
    public int Width => MaxX - MinX + 1;
    public int Depth => MaxZ - MinZ + 1;

    public VertexRect Union(VertexRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new VertexRect(
            Math.Min(MinX, other.MinX), Math.Min(MinZ, other.MinZ),
            Math.Max(MaxX, other.MaxX), Math.Max(MaxZ, other.MaxZ));
    }

    /// <summary>
    /// Vertices within the square bounding a circle at a local position (world units), on a grid of
    /// <paramref name="width"/>×<paramref name="depth"/> vertices spaced <paramref name="cellSize"/> apart.
    /// </summary>
    public static VertexRect Circle(float cx, float cz, float radius, float cellSize, int width, int depth)
    {
        int minX = Math.Max(0, (int)MathF.Floor((cx - radius) / cellSize));
        int minZ = Math.Max(0, (int)MathF.Floor((cz - radius) / cellSize));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling((cx + radius) / cellSize));
        int maxZ = Math.Min(depth - 1, (int)MathF.Ceiling((cz + radius) / cellSize));
        return new VertexRect(minX, minZ, maxX, maxZ);
    }
}
