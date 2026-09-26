using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CitySim.TerrainSystem;

/// <summary>
/// Reads and writes a map (heights plus painted layers) as a <c>.csmap</c> file. Engine-agnostic.
///
/// Version 2, little-endian: magic "CSMP", u16 version, then
/// <list type="bullet">
/// <item>i32 width, i32 depth, f32 cell size, f32 min height, f32 max height, i32 tile size (256).</item>
/// <item>Heights: for each tile (row-major), i32 length + a zlib stream of u16 heights (0 = min, 65535 = max), each row
///   stored as differences from the previous vertex (mod 2^16), which compresses far better than raw heights.</item>
/// <item>Painted layers: i32 tile size (256), i32 layer count, i32 tile count, then per painted tile: i32 tx, i32 tz,
///   i32 length + a zlib stream of the tile's u32 control values (see <see cref="SplatMap"/>). Unpainted tiles are skipped.</item>
/// <item>i32 background count: 0 for now. Reserved for the coarse 70 km background map (M6 phase 3).</item>
/// </list>
/// Heights are quantised to 16 bits over the map's range (7.6 mm steps over 500 m, like CS2). Tiles are compressed and
/// decompressed in parallel. Version 1 (float heights, 8 float weights per vertex, one zlib stream) still loads.
/// </summary>
public static class MapFile
{
    public const string Extension = "csmap";
    public const ushort Version = 2;
    private static readonly byte[] Magic = "CSMP"u8.ToArray();
    private const int MaxVertices = 8193;
    private const int HeightTile = 256;

    public static void Save(string path, HeightMap map, SplatMap splat)
    {
        if (splat.Width != map.Width || splat.Depth != map.Depth)
            throw new ArgumentException("Splat map size doesn't match the heightmap.", nameof(splat));

        var (min, max) = map.GetRange();
        byte[][] heightTiles = CompressHeights(map, min, max);
        var painted = new System.Collections.Generic.List<(int Tx, int Tz, uint[] Data)>();
        for (int tz = 0; tz < splat.TilesZ; tz++)
            for (int tx = 0; tx < splat.TilesX; tx++)
                if (splat.GetTile(tx, tz) is { } t) painted.Add((tx, tz, t));
        var controlTiles = new byte[painted.Count][];
        Parallel.For(0, painted.Count, i => controlTiles[i] = Deflate(MemoryMarshal.AsBytes(painted[i].Data.AsSpan())));

        // Write to a temporary file first so a failed save never destroys the previous one.
        string tmp = path + ".tmp";
        try
        {
            using (var file = File.Create(tmp))
            using (var w = new BinaryWriter(file))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(map.Width);
                w.Write(map.Depth);
                w.Write(map.CellSize);
                w.Write(min);
                w.Write(max);
                w.Write(HeightTile);
                foreach (var t in heightTiles)
                {
                    w.Write(t.Length);
                    w.Write(t);
                }
                w.Write(SplatMap.TileSize);
                w.Write(SplatMap.Layers);
                w.Write(painted.Count);
                for (int i = 0; i < painted.Count; i++)
                {
                    w.Write(painted[i].Tx);
                    w.Write(painted[i].Tz);
                    w.Write(controlTiles[i].Length);
                    w.Write(controlTiles[i]);
                }
                w.Write(0); // no background map yet
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
        try
        {
            return version == 1 ? LoadV1(file) : LoadV2(file);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Map file is truncated.");
        }
        catch (AggregateException e) when (e.InnerException is InvalidDataException inner)
        {
            throw new InvalidDataException(inner.Message);
        }
    }

    private static (HeightMap, SplatMap) LoadV2(Stream file)
    {
        using var r = new BinaryReader(file);
        int width = r.ReadInt32(), depth = r.ReadInt32();
        float cellSize = r.ReadSingle(), min = r.ReadSingle(), max = r.ReadSingle();
        int tile = r.ReadInt32();
        CheckHeader(width, depth, cellSize);
        if (tile is < 16 or > 4096 || !float.IsFinite(min) || !float.IsFinite(max) || max < min)
            throw new InvalidDataException("Map file header is corrupt.");

        int tilesX = (width + tile - 1) / tile, tilesZ = (depth + tile - 1) / tile;
        var blobs = new byte[tilesX * tilesZ][];
        for (int i = 0; i < blobs.Length; i++) blobs[i] = ReadBlob(r);
        var map = new HeightMap(width, depth, cellSize);
        float step = (max - min) / 65535f;
        Parallel.For(0, blobs.Length, i =>
        {
            int tx = i % tilesX, tz = i / tilesX;
            int x0 = tx * tile, z0 = tz * tile;
            int w = Math.Min(tile, width - x0), d = Math.Min(tile, depth - z0);
            var q = new ushort[w * d];
            Inflate(blobs[i], MemoryMarshal.AsBytes(q.AsSpan()));
            for (int z = 0; z < d; z++)
            {
                var row = map.Row(z0 + z).Slice(x0, w);
                ushort prev = 0;
                for (int x = 0; x < w; x++)
                {
                    prev = (ushort)(prev + q[z * w + x]);
                    row[x] = min + prev * step;
                }
            }
        });
        map.Invalidate();

        int controlTile = r.ReadInt32(), layers = r.ReadInt32(), count = r.ReadInt32();
        if (controlTile != SplatMap.TileSize || layers is < 1 or > 32)
            throw new InvalidDataException("Map file paint section is corrupt.");
        var splat = new SplatMap(width, depth, cellSize);
        var painted = new (int Tx, int Tz, byte[] Blob)[count];
        for (int i = 0; i < count; i++)
        {
            int tx = r.ReadInt32(), tz = r.ReadInt32();
            if (tx < 0 || tz < 0 || tx >= splat.TilesX || tz >= splat.TilesZ)
                throw new InvalidDataException("Map file paint section is corrupt.");
            painted[i] = (tx, tz, ReadBlob(r));
        }
        Parallel.For(0, count, i =>
        {
            var data = new uint[SplatMap.TileSize * SplatMap.TileSize];
            Inflate(painted[i].Blob, MemoryMarshal.AsBytes(data.AsSpan()));
            // A layer this build doesn't have becomes automatic ground.
            if (layers > SplatMap.Layers)
                for (int j = 0; j < data.Length; j++)
                    if (SplatMap.Base(data[j]) >= SplatMap.Layers || SplatMap.Overlay(data[j]) >= SplatMap.Layers)
                        data[j] = SplatMap.Unpainted;
            lock (splat) splat.SetTile(painted[i].Tx, painted[i].Tz, data);
        });
        // Background maps (none are written yet) would follow here.
        return (map, splat);
    }

    private static (HeightMap, SplatMap) LoadV1(Stream file)
    {
        using var z = new ZLibStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        int width = r.ReadInt32(), depth = r.ReadInt32();
        float cellSize = r.ReadSingle();
        int layers = r.ReadInt32();
        CheckHeader(width, depth, cellSize);
        if (layers is < 0 or > 64)
            throw new InvalidDataException("Map file header is corrupt.");

        var map = new HeightMap(width, depth, cellSize);
        z.ReadExactly(MemoryMarshal.AsBytes(map.Data));
        map.Invalidate();

        // Per-vertex float weights → control values (the two heaviest layers are kept).
        var splat = new SplatMap(width, depth, cellSize);
        var row = new float[layers * width];
        for (int zi = 0; zi < depth; zi++)
        {
            z.ReadExactly(MemoryMarshal.AsBytes(row.AsSpan()));
            for (int x = 0; x < width; x++)
                splat.Set(x, zi, SplatMap.FromWeights(row.AsSpan(x * layers, Math.Min(layers, SplatMap.Layers))));
        }
        return (map, splat);
    }

    private static void CheckHeader(int width, int depth, float cellSize)
    {
        if (width is < 2 or > MaxVertices || depth is < 2 or > MaxVertices || !(cellSize > 0f))
            throw new InvalidDataException("Map file header is corrupt.");
    }

    private static byte[][] CompressHeights(HeightMap map, float min, float max)
    {
        int tilesX = (map.Width + HeightTile - 1) / HeightTile, tilesZ = (map.Depth + HeightTile - 1) / HeightTile;
        var blobs = new byte[tilesX * tilesZ][];
        float scale = max - min > 1e-6f ? 65535f / (max - min) : 0f;
        Parallel.For(0, blobs.Length, i =>
        {
            int x0 = i % tilesX * HeightTile, z0 = i / tilesX * HeightTile;
            int w = Math.Min(HeightTile, map.Width - x0), d = Math.Min(HeightTile, map.Depth - z0);
            var q = new ushort[w * d];
            for (int z = 0; z < d; z++)
            {
                var row = map.Row(z0 + z).Slice(x0, w);
                ushort prev = 0;
                for (int x = 0; x < w; x++)
                {
                    var v = (ushort)Math.Clamp((int)MathF.Round((row[x] - min) * scale), 0, 65535);
                    q[z * w + x] = (ushort)(v - prev);
                    prev = v;
                }
            }
            blobs[i] = Deflate(MemoryMarshal.AsBytes(q.AsSpan()));
        });
        return blobs;
    }

    private static byte[] ReadBlob(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length is < 0 or > 64 << 20) throw new InvalidDataException("Map file tile is corrupt.");
        return r.ReadBytes(length);
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest)) z.Write(data);
        return ms.ToArray();
    }

    private static void Inflate(byte[] blob, Span<byte> into)
    {
        using var z = new ZLibStream(new MemoryStream(blob), CompressionMode.Decompress);
        try
        {
            z.ReadExactly(into);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            throw new InvalidDataException("Map file tile is corrupt.");
        }
    }
}
