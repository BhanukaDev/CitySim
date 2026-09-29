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

    /// <summary>Grown by <paramref name="by"/> vertices on every side, clamped to a <paramref name="width"/>×<paramref name="depth"/> grid.</summary>
    public VertexRect Expand(int by, int width, int depth) => IsEmpty ? this : new VertexRect(
        Math.Max(0, MinX - by), Math.Max(0, MinZ - by), Math.Min(width - 1, MaxX + by), Math.Min(depth - 1, MaxZ + by));

    /// <summary>Vertex count (0 when empty).</summary>
    public long Area => IsEmpty ? 0 : (long)Width * Depth;

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

    /// <summary>
    /// Vertices covering the map-metre rectangle (<paramref name="x0"/>, <paramref name="z0"/>)–(<paramref name="x1"/>,
    /// <paramref name="z1"/>) (either corner order), rounded outward, clamped to a <paramref name="width"/>×<paramref name="depth"/> grid.
    /// </summary>
    public static VertexRect FromMapRect(float x0, float z0, float x1, float z1, float cellSize, int width, int depth)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) / cellSize));
        int minZ = Math.Max(0, (int)MathF.Floor(MathF.Min(z0, z1) / cellSize));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) / cellSize));
        int maxZ = Math.Min(depth - 1, (int)MathF.Ceiling(MathF.Max(z0, z1) / cellSize));
        return new VertexRect(minX, minZ, maxX, maxZ);
    }

    /// <summary>This rectangle clamped to a <paramref name="width"/>×<paramref name="depth"/> grid.</summary>
    public VertexRect Clamp(int width, int depth) => IsEmpty ? this : new VertexRect(
        Math.Max(0, MinX), Math.Max(0, MinZ), Math.Min(width - 1, MaxX), Math.Min(depth - 1, MaxZ));
}
