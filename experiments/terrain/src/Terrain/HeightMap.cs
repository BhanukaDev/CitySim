using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.TerrainSystem;

/// <summary>
/// Engine-agnostic grid of terrain heights. Vertices are spaced <see cref="CellSize"/> apart,
/// with vertex (0, 0) at local position (0, 0). Kept free of Godot types so it can be reused
/// by simulation code (roads, zoning, water) and moved into the main game unchanged.
/// </summary>
public sealed class HeightMap
{
    /// <summary>Side of the square vertex blocks whose min/max <see cref="GetRange"/> caches.</summary>
    private const int RangeTile = 64;

    private readonly float[] _heights;
    private readonly int _rangeTilesX;
    private readonly float[] _tileMin, _tileMax;
    private readonly bool[] _tileStale;
    private bool _anyStale = true;

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

        _rangeTilesX = (width + RangeTile - 1) / RangeTile;
        int tiles = _rangeTilesX * ((depth + RangeTile - 1) / RangeTile);
        _tileMin = new float[tiles];
        _tileMax = new float[tiles];
        _tileStale = new bool[tiles];
        Array.Fill(_tileStale, true);
    }

    public float this[int x, int z]
    {
        get => _heights[z * Width + x];
        set
        {
            _heights[z * Width + x] = value;
            _tileStale[z / RangeTile * _rangeTilesX + x / RangeTile] = true;
            _anyStale = true;
        }
    }

    /// <summary>
    /// Every height, row-major (index z * Width + x). For bulk work such as saving and loading.
    /// Call <see cref="Invalidate()"/> after writing through it.
    /// </summary>
    public Span<float> Data => _heights;

    /// <summary>One row of heights (safe to take inside a parallel loop). Call <see cref="Invalidate()"/> after writing through it.</summary>
    public Span<float> Row(int z) => _heights.AsSpan(z * Width, Width);

    /// <summary>Sets every vertex to <paramref name="height"/>.</summary>
    public void Fill(float height)
    {
        Array.Fill(_heights, height);
        Invalidate();
    }

    /// <summary>Tells the map every height may have changed (after bulk writes through <see cref="Data"/> or <see cref="Row"/>).</summary>
    public void Invalidate()
    {
        Array.Fill(_tileStale, true);
        _anyStale = true;
    }

    /// <summary>Tells the map the heights in <paramref name="r"/> may have changed.</summary>
    public void Invalidate(VertexRect r)
    {
        if (r.IsEmpty) return;
        for (int tz = r.MinZ / RangeTile; tz <= r.MaxZ / RangeTile; tz++)
            for (int tx = r.MinX / RangeTile; tx <= r.MaxX / RangeTile; tx++)
                _tileStale[tz * _rangeTilesX + tx] = true;
        _anyStale = true;
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

    /// <summary>
    /// Softens creases with repeated 3x3 weighted blurs (1-2-1 kernel). Edges are clamped,
    /// so the map keeps its size and chunk borders stay seamless. The kernel is separable, so each pass is a
    /// horizontal blur (rows in parallel) then a vertical one (column strips in parallel, one row buffer each):
    /// no full-map temporary array.
    /// </summary>
    public void Smooth(int passes, CancellationToken ct = default, Action<float>? progress = null)
    {
        if (passes <= 0) return;
        const int strip = 256;
        int strips = (Width + strip - 1) / strip;
        var options = new ParallelOptions { CancellationToken = ct };
        for (int p = 0; p < passes; p++)
        {
            Parallel.For(0, Depth, options, () => new float[Width], (z, _, row) =>
            {
                var h = Row(z);
                h.CopyTo(row);
                int last = Width - 1;
                h[0] = (3f * row[0] + row[1]) * 0.25f;
                for (int x = 1; x < last; x++)
                    h[x] = (row[x - 1] + 2f * row[x] + row[x + 1]) * 0.25f;
                h[last] = (row[last - 1] + 3f * row[last]) * 0.25f;
                return row;
            }, _ => { });
            progress?.Invoke((p + 0.5f) / passes);

            Parallel.For(0, strips, options, s =>
            {
                int x0 = s * strip, n = Math.Min(strip, Width - x0);
                // above = the unblurred row before z (clamped at the top), cur = the unblurred row z.
                float[] above = ArrayPool<float>.Shared.Rent(n), cur = ArrayPool<float>.Shared.Rent(n);
                _heights.AsSpan(x0, n).CopyTo(above);
                for (int z = 0; z < Depth; z++)
                {
                    var h = _heights.AsSpan(z * Width + x0, n);
                    h.CopyTo(cur);
                    var below = _heights.AsSpan(Math.Min(z + 1, Depth - 1) * Width + x0, n);
                    for (int i = 0; i < n; i++)
                        h[i] = (above[i] + 2f * cur[i] + below[i]) * 0.25f;
                    (above, cur) = (cur, above);
                }
                ArrayPool<float>.Shared.Return(above);
                ArrayPool<float>.Shared.Return(cur);
            });
            progress?.Invoke((p + 1f) / passes);
        }
        Invalidate();
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
    public void PasteRegion(VertexRect r, ReadOnlySpan<float> data)
    {
        for (int z = 0; z < r.Depth; z++)
            data.Slice(z * r.Width, r.Width).CopyTo(_heights.AsSpan((r.MinZ + z) * Width + r.MinX, r.Width));
        Invalidate(r);
    }

    /// <summary>
    /// Swaps the heights in <paramref name="r"/> with <paramref name="data"/> (same layout as <see cref="CopyRegion(VertexRect)"/>).
    /// Undo uses it so one buffer holds whichever state is not on the map.
    /// </summary>
    public void SwapRegion(VertexRect r, Span<float> data)
    {
        for (int z = 0; z < r.Depth; z++)
        {
            var a = _heights.AsSpan((r.MinZ + z) * Width + r.MinX, r.Width);
            var b = data.Slice(z * r.Width, r.Width);
            for (int i = 0; i < a.Length; i++) (a[i], b[i]) = (b[i], a[i]);
        }
        Invalidate(r);
    }

    /// <summary>Lowest and highest height. Cached per 64² block, so after an edit only the changed blocks are rescanned.</summary>
    public (float Min, float Max) GetRange()
    {
        if (_anyStale)
        {
            _anyStale = false;
            int tilesZ = _tileMin.Length / _rangeTilesX;
            Parallel.For(0, tilesZ, tz =>
            {
                for (int tx = 0; tx < _rangeTilesX; tx++)
                {
                    int t = tz * _rangeTilesX + tx;
                    if (!_tileStale[t]) continue;
                    _tileStale[t] = false;
                    int x0 = tx * RangeTile, z0 = tz * RangeTile;
                    int x1 = Math.Min(x0 + RangeTile, Width), z1 = Math.Min(z0 + RangeTile, Depth);
                    float min = float.MaxValue, max = float.MinValue;
                    for (int z = z0; z < z1; z++)
                        foreach (float h in _heights.AsSpan(z * Width + x0, x1 - x0))
                        {
                            if (h < min) min = h;
                            if (h > max) max = h;
                        }
                    _tileMin[t] = min;
                    _tileMax[t] = max;
                }
            });
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int t = 0; t < _tileMin.Length; t++)
        {
            if (_tileMin[t] < lo) lo = _tileMin[t];
            if (_tileMax[t] > hi) hi = _tileMax[t];
        }
        return (lo, hi);
    }
}
