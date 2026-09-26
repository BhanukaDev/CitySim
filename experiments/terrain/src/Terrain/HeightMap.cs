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

    /// <summary>Every height, row-major (index z * Width + x). For bulk work such as saving and loading.</summary>
    public Span<float> Data => _heights;

    /// <summary>Sets every vertex to <paramref name="height"/>.</summary>
    public void Fill(float height) => Array.Fill(_heights, height);

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

    /// <summary>
    /// Softens creases with repeated 3x3 weighted blurs (1-2-1 kernel). Edges are clamped,
    /// so the map keeps its size and chunk borders stay seamless.
    /// </summary>
    public void Smooth(int passes)
    {
        if (passes <= 0) return;
        var tmp = new float[_heights.Length];
        for (int p = 0; p < passes; p++)
        {
            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float sum =
                        4f * GetHeightClamped(x, z) +
                        2f * (GetHeightClamped(x - 1, z) + GetHeightClamped(x + 1, z) +
                              GetHeightClamped(x, z - 1) + GetHeightClamped(x, z + 1)) +
                        GetHeightClamped(x - 1, z - 1) + GetHeightClamped(x + 1, z - 1) +
                        GetHeightClamped(x - 1, z + 1) + GetHeightClamped(x + 1, z + 1);
                    tmp[z * Width + x] = sum / 16f;
                }
            }
            Array.Copy(tmp, _heights, _heights.Length);
        }
    }

    /// <summary>Vertices within the square bounding a circle at a local position (world units), clamped to the map.</summary>
    public VertexRect CircleRect(float cx, float cz, float radius) =>
        VertexRect.Circle(cx, cz, radius, CellSize, Width, Depth);

    /// <summary>Copy of every height, row-major. Used as the "before" state of an edit.</summary>
    public float[] Snapshot() => (float[])_heights.Clone();

    /// <summary>Heights inside a rectangle, row-major.</summary>
    public float[] CopyRegion(VertexRect r) => CopyRegion(_heights, Width, r);

    /// <summary>Crops a rectangle out of a full-map array such as one from <see cref="Snapshot"/>.</summary>
    public static float[] CopyRegion(float[] source, int sourceWidth, VertexRect r)
    {
        var data = new float[r.Width * r.Depth];
        for (int z = 0; z < r.Depth; z++)
            Array.Copy(source, (r.MinZ + z) * sourceWidth + r.MinX, data, z * r.Width, r.Width);
        return data;
    }

    /// <summary>Writes heights previously taken with <see cref="CopyRegion(VertexRect)"/> back into the map.</summary>
    public void PasteRegion(VertexRect r, float[] data)
    {
        for (int z = 0; z < r.Depth; z++)
            Array.Copy(data, z * r.Width, _heights, (r.MinZ + z) * Width + r.MinX, r.Width);
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
