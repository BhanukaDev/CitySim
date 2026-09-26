using System;
using System.Numerics;

namespace CitySim.TerrainSystem;

/// <summary>
/// Engine-agnostic grid of terrain heights. Vertices are spaced <see cref="CellSize"/> apart,
/// with vertex (0, 0) at local position (0, 0). Kept free of Godot types so it can be reused
/// by simulation code (roads, zoning, water) and moved into the main game unchanged.
/// </summary>
public sealed class HeightMap
{
    private readonly float[] _heights;

    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }

    public float SizeX => (Width - 1) * CellSize;
    public float SizeZ => (Depth - 1) * CellSize;

    public HeightMap(int width, int depth, float cellSize)
    {
        if (width < 2 || depth < 2)
            throw new ArgumentOutOfRangeException(nameof(width), "Heightmap needs at least 2x2 vertices.");
        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        Width = width;
        Depth = depth;
        CellSize = cellSize;
        _heights = new float[width * depth];
    }

    public float this[int x, int z]
    {
        get => _heights[z * Width + x];
        set => _heights[z * Width + x] = value;
    }

    public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

    /// <summary>Height at a vertex, with coordinates clamped to the grid edges.</summary>
    public float GetHeightClamped(int x, int z)
    {
        x = Math.Clamp(x, 0, Width - 1);
        z = Math.Clamp(z, 0, Depth - 1);
        return _heights[z * Width + x];
    }

    /// <summary>Bilinearly interpolated height at a local position (world units, clamped to the map).</summary>
    public float SampleHeight(float x, float z)
    {
        float fx = Math.Clamp(x / CellSize, 0f, Width - 1);
        float fz = Math.Clamp(z / CellSize, 0f, Depth - 1);
        int x0 = Math.Min((int)fx, Width - 2);
        int z0 = Math.Min((int)fz, Depth - 2);
        float tx = fx - x0;
        float tz = fz - z0;

        float h00 = this[x0, z0];
        float h10 = this[x0 + 1, z0];
        float h01 = this[x0, z0 + 1];
        float h11 = this[x0 + 1, z0 + 1];
        float a = h00 + (h10 - h00) * tx;
        float b = h01 + (h11 - h01) * tx;
        return a + (b - a) * tz;
    }

    /// <summary>Surface normal at a vertex using central differences (seamless across chunk borders).</summary>
    public Vector3 GetNormal(int x, int z)
    {
        float hl = GetHeightClamped(x - 1, z);
        float hr = GetHeightClamped(x + 1, z);
        float hd = GetHeightClamped(x, z - 1);
        float hu = GetHeightClamped(x, z + 1);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * CellSize, hd - hu));
    }

    /// <summary>Surface normal at a local position (world units).</summary>
    public Vector3 SampleNormal(float x, float z)
    {
        float d = CellSize;
        float hl = SampleHeight(x - d, z);
        float hr = SampleHeight(x + d, z);
        float hd = SampleHeight(x, z - d);
        float hu = SampleHeight(x, z + d);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * d, hd - hu));
    }

    /// <summary>Slope angle in degrees at a local position (0 = flat, 90 = vertical).</summary>
    public float SampleSlopeDegrees(float x, float z)
    {
        float ny = Math.Clamp(SampleNormal(x, z).Y, -1f, 1f);
        return MathF.Acos(ny) * (180f / MathF.PI);
    }

    public (float Min, float Max) GetRange()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in _heights)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }
        return (min, max);
    }
}
