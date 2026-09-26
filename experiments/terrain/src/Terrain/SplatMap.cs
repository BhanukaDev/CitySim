using System;

namespace CitySim.TerrainSystem;

/// <summary>
/// Engine-agnostic painted ground-layer weights, one set per heightmap vertex. Each vertex stores a
/// weight (0 to 1) for each of the <see cref="TerrainLayers.PaintableCount"/> paintable layers, and the
/// weights sum to at most 1. The sum is the painted "coverage": where it is below 1 the shader fills the
/// rest with its automatic rules (rock on slopes, snow up high, ...), so an all-zero map means fully automatic.
/// </summary>
public sealed class SplatMap
{
    public const int Layers = TerrainLayers.PaintableCount;

    private readonly float[] _weights;

    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }

    public SplatMap(int width, int depth, float cellSize)
    {
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        _weights = new float[width * depth * Layers];
    }

    /// <summary>The <see cref="Layers"/> weights of one vertex.</summary>
    public Span<float> At(int x, int z) => _weights.AsSpan((z * Width + x) * Layers, Layers);

    public VertexRect CircleRect(float cx, float cz, float radius) =>
        VertexRect.Circle(cx, cz, radius, CellSize, Width, Depth);

    /// <summary>Copy of every weight. Used as the "before" state of an edit.</summary>
    public float[] Snapshot() => (float[])_weights.Clone();

    /// <summary>Weights inside a rectangle, row-major, <see cref="Layers"/> per vertex.</summary>
    public float[] CopyRegion(VertexRect r) => CopyRegion(_weights, Width, r);

    /// <summary>Crops a rectangle out of a full-map array such as one from <see cref="Snapshot"/>.</summary>
    public static float[] CopyRegion(float[] source, int sourceWidth, VertexRect r)
    {
        int row = r.Width * Layers;
        var data = new float[row * r.Depth];
        for (int z = 0; z < r.Depth; z++)
            Array.Copy(source, ((r.MinZ + z) * sourceWidth + r.MinX) * Layers, data, z * row, row);
        return data;
    }

    public void PasteRegion(VertexRect r, float[] data)
    {
        int row = r.Width * Layers;
        for (int z = 0; z < r.Depth; z++)
            Array.Copy(data, z * row, _weights, ((r.MinZ + z) * Width + r.MinX) * Layers, row);
    }

    /// <summary>
    /// Writes the weights of a rectangle as bytes into two RGBA8 images of the full map size
    /// (layers 0–3 into <paramref name="rgba0"/>, 4–7 into <paramref name="rgba1"/>), for upload to the GPU.
    /// </summary>
    public void WriteRgba8(VertexRect r, byte[] rgba0, byte[] rgba1)
    {
        for (int z = r.MinZ; z <= r.MaxZ; z++)
        {
            for (int x = r.MinX; x <= r.MaxX; x++)
            {
                int src = (z * Width + x) * Layers;
                int dst = (z * Width + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    rgba0[dst + c] = ToByte(_weights[src + c]);
                    rgba1[dst + c] = ToByte(_weights[src + 4 + c]);
                }
            }
        }
    }

    private static byte ToByte(float w) => (byte)Math.Clamp((int)(w * 255f + 0.5f), 0, 255);
}
