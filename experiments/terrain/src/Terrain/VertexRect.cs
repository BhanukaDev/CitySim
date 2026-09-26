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
}
