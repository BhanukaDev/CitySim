using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CitySim.TerrainSystem;

/// <summary>
/// Reads and writes a map (heights plus painted layers) as a <c>.csmap</c> file. Engine-agnostic.
///
/// Layout, little-endian: magic "CSMP", u16 version, then a zlib stream holding i32 width, i32 depth,
/// f32 cell size, i32 layer count, width*depth f32 heights and width*depth*layers f32 splat weights.
/// Floats are kept exactly (no quantising), so a save/load round trip is lossless.
/// </summary>
public static class MapFile
{
    public const string Extension = "csmap";
    public const ushort Version = 1;
    private static readonly byte[] Magic = "CSMP"u8.ToArray();
    private const int MaxVertices = 8193;

    public static void Save(string path, HeightMap map, SplatMap splat)
    {
        if (splat.Width != map.Width || splat.Depth != map.Depth)
            throw new ArgumentException("Splat map size doesn't match the heightmap.", nameof(splat));

        // Write to a temporary file first so a failed save never destroys the previous one.
        string tmp = path + ".tmp";
        try
        {
            using (var file = File.Create(tmp))
            {
                file.Write(Magic);
                Span<byte> version = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(version, Version);
                file.Write(version);

                // Disposing the writer closes the zlib stream, which flushes it and closes the file.
                using var w = new BinaryWriter(new ZLibStream(file, CompressionLevel.Fastest));
                w.Write(map.Width);
                w.Write(map.Depth);
                w.Write(map.CellSize);
                w.Write(SplatMap.Layers);
                w.Write(MemoryMarshal.AsBytes(map.Data));
                w.Write(MemoryMarshal.AsBytes(splat.Data));
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    /// <summary>Loads a map. Throws <see cref="InvalidDataException"/> for files that aren't valid maps.</summary>
    public static (HeightMap Map, SplatMap Splat) Load(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        file.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a CitySim map file.");
        Span<byte> versionBytes = stackalloc byte[2];
        file.ReadExactly(versionBytes);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(versionBytes);
        if (version > Version)
            throw new InvalidDataException($"Map file version {version} is newer than this build supports ({Version}).");

        using var z = new ZLibStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        int width = r.ReadInt32(), depth = r.ReadInt32();
        float cellSize = r.ReadSingle();
        int layers = r.ReadInt32();
        if (width is < 2 or > MaxVertices || depth is < 2 or > MaxVertices || !(cellSize > 0f) || layers is < 0 or > 64)
            throw new InvalidDataException("Map file header is corrupt.");

        var map = new HeightMap(width, depth, cellSize);
        z.ReadExactly(MemoryMarshal.AsBytes(map.Data));

        var splat = new SplatMap(width, depth, cellSize);
        if (layers == SplatMap.Layers)
            z.ReadExactly(MemoryMarshal.AsBytes(splat.Data));
        else
        {
            // Saved with a different layer count: keep the layers both builds share.
            var row = new float[layers];
            int keep = Math.Min(layers, SplatMap.Layers);
            for (int zi = 0; zi < depth; zi++)
                for (int x = 0; x < width; x++)
                {
                    z.ReadExactly(MemoryMarshal.AsBytes(row.AsSpan()));
                    row.AsSpan(0, keep).CopyTo(splat.At(x, zi));
                }
        }
        return (map, splat);
    }
}
