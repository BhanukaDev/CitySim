using System;

namespace CitySim.TerrainSystem;

/// <summary>
/// Engine-agnostic painted ground layers: one 32-bit control value per heightmap vertex, laid out like Terrain3D's
/// control map so it can be copied to the renderer as-is. A vertex mixes at most two painted layers (base and overlay,
/// with a blend between them) over the automatic ground by its <em>coverage</em>: where coverage is below 1 the shader
/// fills the rest with its automatic rules (rock on slopes, snow up high, ...), so an unpainted map is fully automatic.
///
/// Stored as sparse 256² tiles, allocated only where something was painted: an unpainted 8k map costs nothing.
///
/// Bits (0 = lowest), as in Terrain3D: 27-31 base layer, 22-26 overlay layer, 14-21 blend (overlay share, 0-255),
/// 2 hole, 1 navigation, 0 autoshader. Bits 6-13 hold our coverage (0-255); Terrain3D uses them for UV angle/scale,
/// which our shader doesn't. The autoshader bit is set exactly when coverage is 0.
/// </summary>
public sealed class SplatMap
{
    public const int Layers = TerrainLayers.PaintableCount;
    public const int TileSize = 256;

    /// <summary>The control value of an unpainted vertex: automatic ground, no coverage.</summary>
    public const uint Unpainted = 1u;

    private readonly uint[]?[] _tiles;
    private readonly int _tilesX;

    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }

    public SplatMap(int width, int depth, float cellSize)
    {
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        _tilesX = (width + TileSize - 1) / TileSize;
        _tiles = new uint[]?[_tilesX * ((depth + TileSize - 1) / TileSize)];
    }

    /// <summary>The whole map as a rectangle.</summary>
    public VertexRect All => new(0, 0, Width - 1, Depth - 1);

    /// <summary>Number of 256² tiles holding paint (each 256 KB).</summary>
    public int AllocatedTiles
    {
        get
        {
            int n = 0;
            foreach (var t in _tiles) if (t is not null) n++;
            return n;
        }
    }

    public int TilesX => _tilesX;
    public int TilesZ => _tiles.Length / _tilesX;

    /// <summary>A tile's control values (<see cref="TileSize"/>², row-major), or null if nothing was ever painted there.</summary>
    public uint[]? GetTile(int tx, int tz) => _tiles[tz * _tilesX + tx];

    /// <summary>Replaces a tile (null = unpainted). The array must be <see cref="TileSize"/>² long.</summary>
    public void SetTile(int tx, int tz, uint[]? data)
    {
        if (data is not null && data.Length != TileSize * TileSize)
            throw new ArgumentException("Tile has the wrong size.", nameof(data));
        _tiles[tz * _tilesX + tx] = data;
    }

    public uint Get(int x, int z) =>
        _tiles[z / TileSize * _tilesX + x / TileSize] is { } t ? t[z % TileSize * TileSize + x % TileSize] : Unpainted;

    public void Set(int x, int z, uint value)
    {
        int i = z / TileSize * _tilesX + x / TileSize;
        var t = _tiles[i];
        if (t is null)
        {
            if (value == Unpainted) return;
            t = _tiles[i] = NewTile();
        }
        t[z % TileSize * TileSize + x % TileSize] = value;
    }

    private static uint[] NewTile()
    {
        var t = new uint[TileSize * TileSize];
        Array.Fill(t, Unpainted);
        return t;
    }

    public VertexRect CircleRect(float cx, float cz, float radius) =>
        VertexRect.Circle(cx, cz, radius, CellSize, Width, Depth);

    /// <summary>Every control value, row-major. Dense (4 bytes per vertex): for tests and small maps.</summary>
    public uint[] Snapshot() => CopyRegion(All);

    /// <summary>Control values inside a rectangle, row-major.</summary>
    public uint[] CopyRegion(VertexRect r)
    {
        var data = new uint[r.Width * r.Depth];
        for (int z = 0; z < r.Depth; z++)
            for (int x = 0; x < r.Width; x++)
                data[z * r.Width + x] = Get(r.MinX + x, r.MinZ + z);
        return data;
    }

    /// <summary>Writes values taken with <see cref="CopyRegion"/> back. Unpainted values don't allocate tiles.</summary>
    public void PasteRegion(VertexRect r, ReadOnlySpan<uint> data)
    {
        for (int z = 0; z < r.Depth; z++)
            for (int x = 0; x < r.Width; x++)
                Set(r.MinX + x, r.MinZ + z, data[z * r.Width + x]);
    }

    /// <summary>Swaps the values in <paramref name="r"/> with <paramref name="data"/> (undo keeps one buffer per edit).</summary>
    public void SwapRegion(VertexRect r, Span<uint> data)
    {
        for (int z = 0; z < r.Depth; z++)
            for (int x = 0; x < r.Width; x++)
            {
                ref uint d = ref data[z * r.Width + x];
                uint old = Get(r.MinX + x, r.MinZ + z);
                Set(r.MinX + x, r.MinZ + z, d);
                d = old;
            }
    }

    // --- Control value encoding ---

    public static int Base(uint c) => (int)(c >> 27 & 0x1F);
    public static int Overlay(uint c) => (int)(c >> 22 & 0x1F);
    /// <summary>Overlay share of the painted part, 0-255.</summary>
    public static int Blend(uint c) => (int)(c >> 14 & 0xFF);
    /// <summary>Painted share of the vertex, 0-255 (0 = automatic ground).</summary>
    public static int Coverage(uint c) => (int)(c >> 6 & 0xFF);

    /// <summary>A control value; coverage 0 gives <see cref="Unpainted"/>.</summary>
    public static uint Encode(int baseLayer, int overlay, int blend, int coverage)
    {
        if (coverage <= 0) return Unpainted;
        return (uint)(baseLayer & 0x1F) << 27 | (uint)(overlay & 0x1F) << 22 |
               (uint)Math.Clamp(blend, 0, 255) << 14 | (uint)Math.Min(coverage, 255) << 6;
    }

    /// <summary>The weight of every layer at one vertex (they sum to the coverage, 0 to 1).</summary>
    public static void Decode(uint c, Span<float> weights)
    {
        weights.Clear();
        int cov = Coverage(c);
        if (cov == 0) return;
        float coverage = cov / 255f, blend = Blend(c) / 255f;
        weights[Base(c)] += coverage * (1f - blend);
        weights[Overlay(c)] += coverage * blend;
    }

    /// <summary>
    /// The closest control value to arbitrary layer weights (summing to at most 1): the two heaviest layers are kept,
    /// the rest goes back to automatic ground. Used to read maps saved with per-layer weights.
    /// </summary>
    public static uint FromWeights(ReadOnlySpan<float> weights)
    {
        int a = -1, b = -1;
        for (int i = 0; i < weights.Length && i < Layers; i++)
        {
            if (weights[i] <= 0f) continue;
            if (a < 0 || weights[i] > weights[a]) { b = a; a = i; }
            else if (b < 0 || weights[i] > weights[b]) b = i;
        }
        if (a < 0) return Unpainted;
        float wa = weights[a], wb = b < 0 ? 0f : weights[b];
        float cov = Math.Min(wa + wb, 1f);
        return Encode(a, b < 0 ? a : b, (int)MathF.Round(wb / (wa + wb) * 255f), (int)MathF.Round(cov * 255f));
    }

    /// <summary>
    /// Writes the layer weights of a rectangle as bytes into two RGBA8 images of the full map size
    /// (layers 0–3 into <paramref name="rgba0"/>, 4–7 into <paramref name="rgba1"/>), for upload to the GPU.
    /// </summary>
    public void WriteRgba8(VertexRect r, byte[] rgba0, byte[] rgba1)
    {
        Span<float> w = stackalloc float[Layers];
        for (int z = r.MinZ; z <= r.MaxZ; z++)
        {
            for (int x = r.MinX; x <= r.MaxX; x++)
            {
                Decode(Get(x, z), w);
                int dst = (z * Width + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    rgba0[dst + c] = ToByte(w[c]);
                    rgba1[dst + c] = ToByte(w[4 + c]);
                }
            }
        }
    }

    private static byte ToByte(float w) => (byte)Math.Clamp((int)(w * 255f + 0.5f), 0, 255);
}
