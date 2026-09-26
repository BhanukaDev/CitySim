using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CitySim.TerrainSystem;

/// <summary>
/// A 16-bit greyscale heightmap as used by World Machine, Gaea and DEM tools: 0 is the lowest point and
/// 65535 the highest, with the real height range kept separately. Reads and writes 16-bit PNG and RAW
/// (<c>.r16</c>/<c>.raw</c>: square, little-endian u16, no header). Engine-agnostic: Godot's
/// <c>Image</c> drops PNGs to 8 bits, so the PNG codec is done here.
///
/// Row 0 is the top of the image, which maps to z = 0 (north); column 0 maps to x = 0 (west).
/// </summary>
public sealed class HeightmapImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major samples (index row * Width + column), 0 to 65535.</summary>
    public ushort[] Pixels { get; }

    /// <summary>Source bit depth (8 or 16). 8-bit images are stretched to 16 bits on load.</summary>
    public int BitDepth { get; init; } = 16;

    /// <summary>Height range stored by <see cref="WritePng"/> in a <c>tEXt</c> chunk, if the file had one.</summary>
    public (float Min, float Max)? Range { get; init; }

    public HeightmapImage(int width, int height, ushort[] pixels)
    {
        if (width < 2 || height < 2) throw new InvalidDataException("Heightmap needs at least 2x2 pixels.");
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count doesn't match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public static bool IsRaw(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".r16" or ".raw";

    /// <summary>Reads a PNG or RAW file (by extension). Throws <see cref="InvalidDataException"/> for unsupported files.</summary>
    public static HeightmapImage Read(string path) => IsRaw(path) ? ReadRaw(path) : ReadPng(path);

    /// <summary>Writes PNG or RAW by extension. <paramref name="range"/> is stored in PNGs only.</summary>
    public void Write(string path, (float Min, float Max)? range)
    {
        if (IsRaw(path)) WriteRaw(path);
        else WritePng(path, range);
    }

    // --- Conversion to and from a HeightMap ---

    /// <summary>
    /// Builds a square map of <paramref name="vertices"/>² from the image stretched over it, mapping 0..65535 to
    /// <paramref name="minHeight"/>..<paramref name="maxHeight"/> metres. See <see cref="Generation.HeightmapSampler"/>
    /// for rotated, scaled or tiled placement.
    /// </summary>
    public HeightMap ToHeightMap(int vertices, float cellSize, float minHeight, float maxHeight)
    {
        var sampler = new Generation.HeightmapSampler(this, new Generation.ImagePlacement { Lowest = minHeight, Highest = maxHeight }, vertices);
        var map = new HeightMap(vertices, vertices, cellSize);
        for (int z = 0; z < vertices; z++)
            for (int x = 0; x < vertices; x++)
                map[x, z] = sampler.Sample(x / (vertices - 1f), z / (vertices - 1f));
        return map;
    }

    /// <summary>One pixel per map vertex, with the map's lowest point at 0 and its highest at 65535.</summary>
    public static HeightmapImage FromHeightMap(HeightMap map, out (float Min, float Max) range)
    {
        var (min, max) = map.GetRange();
        if (max - min < 1e-3f) max = min + 1f; // flat map: avoid dividing by zero
        range = (min, max);
        var pixels = new ushort[map.Width * map.Depth];
        float scale = 65535f / (max - min);
        var data = map.Data;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)Math.Clamp(MathF.Round((data[i] - min) * scale), 0f, 65535f);
        return new HeightmapImage(map.Width, map.Depth, pixels);
    }

    // --- RAW ---

    private static HeightmapImage ReadRaw(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int side = (int)Math.Round(Math.Sqrt(bytes.Length / 2.0));
        if (bytes.Length % 2 != 0 || side * side * 2 != bytes.Length)
            throw new InvalidDataException("RAW heightmaps must be square 16-bit files (size = side² × 2 bytes).");
        var pixels = new ushort[side * side];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new HeightmapImage(side, side, pixels);
    }

    private void WriteRaw(string path)
    {
        if (Width != Height) throw new InvalidOperationException("RAW heightmaps must be square.");
        var bytes = new byte[Pixels.Length * 2];
        for (int i = 0; i < Pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), Pixels[i]);
        File.WriteAllBytes(path, bytes);
    }

    // --- PNG ---

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const string RangeKeyword = "CitySim";
    private const int MaxSide = 16384;

    /// <summary>
    /// Decodes greyscale, grey+alpha, RGB and RGBA PNGs at 8 or 16 bits (the first channel is the height).
    /// Palette, sub-8-bit and interlaced images are rejected.
    /// </summary>
    private static HeightmapImage ReadPng(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[8];
        file.ReadExactly(sig);
        if (!sig.SequenceEqual(PngSignature)) throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        (float, float)? range = null;
        var idat = new MemoryStream();
        Span<byte> head = stackalloc byte[8];
        while (true)
        {
            file.ReadExactly(head);
            int length = BinaryPrimitives.ReadInt32BigEndian(head);
            string type = Encoding.ASCII.GetString(head[4..]);
            if (length < 0) throw new InvalidDataException("PNG chunk is corrupt.");
            byte[] data = new byte[length];
            file.ReadExactly(data);
            file.ReadExactly(head[..4]); // CRC, not checked: zlib's own checksum catches corrupt image data

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                bitDepth = data[8];
                colorType = data[9];
                if (data[12] != 0) throw new InvalidDataException("Interlaced PNGs aren't supported. Re-save without interlacing.");
                if (colorType is 3 || bitDepth is not (8 or 16))
                    throw new InvalidDataException($"Unsupported PNG ({bitDepth}-bit, colour type {colorType}). Use 16-bit greyscale.");
                if (width is < 2 or > MaxSide || height is < 2 or > MaxSide)
                    throw new InvalidDataException($"PNG size {width}x{height} is out of range.");
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "tEXt") range = ParseRange(data) ?? range;
            else if (type == "IEND") break;
        }
        if (width == 0) throw new InvalidDataException("PNG has no header.");

        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Unsupported PNG colour type {colorType}.") };
        int bpp = channels * bitDepth / 8;
        int stride = width * bpp;
        byte[] raw = new byte[height * (stride + 1)];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        Unfilter(raw, height, stride, bpp);

        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * bpp;
                pixels[y * width + x] = bitDepth == 16 ? (ushort)(raw[i] << 8 | raw[i + 1]) : (ushort)(raw[i] * 257);
            }
        }
        return new HeightmapImage(width, height, pixels) { BitDepth = bitDepth, Range = range };
    }

    /// <summary>Reverses PNG row filters in place. Each row starts with its filter type byte.</summary>
    private static void Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            int prev = row - stride - 1; // start of the previous row's filter byte
            byte filter = raw[row];
            for (int i = 1; i <= stride; i++)
            {
                int a = i > bpp ? raw[row + i - bpp] : 0;
                int b = y > 0 ? raw[prev + i] : 0;
                int c = y > 0 && i > bpp ? raw[prev + i - bpp] : 0;
                raw[row + i] += (byte)(filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG row filter {filter} is invalid."),
                });
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Writes a 16-bit greyscale PNG. The height range, if given, goes in a <c>tEXt</c> chunk so a re-import can restore it.</summary>
    private void WritePng(string path, (float Min, float Max)? range)
    {
        // Sub filter on every row: heightmaps are smooth, so neighbour differences compress well.
        int stride = Width * 2;
        byte[] raw = new byte[Height * (stride + 1)];
        for (int y = 0; y < Height; y++)
        {
            int row = y * (stride + 1);
            raw[row] = 1;
            for (int x = 0; x < Width; x++)
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(row + 1 + x * 2), Pixels[y * Width + x]);
            for (int i = stride; i > 2; i--)
                raw[row + i] -= raw[row + i - 2];
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        using var file = File.Create(path);
        file.Write(PngSignature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), Height);
        ihdr[8] = 16; // bit depth; colour type 0 (grey), compression/filter/interlace 0
        WriteChunk(file, "IHDR", ihdr);
        if (range is var (min, max))
            WriteChunk(file, "tEXt", Encoding.Latin1.GetBytes(
                RangeKeyword + "\0" + string.Create(CultureInfo.InvariantCulture, $"min={min:R};max={max:R}")));
        WriteChunk(file, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(file, "IEND", []);
    }

    private static (float, float)? ParseRange(byte[] text)
    {
        string s = Encoding.Latin1.GetString(text);
        if (!s.StartsWith(RangeKeyword + "\0")) return null;
        float? min = null, max = null;
        foreach (string part in s[(RangeKeyword.Length + 1)..].Split(';'))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
            if (kv[0] == "min") min = v;
            else if (kv[0] == "max") max = v;
        }
        return min is { } lo && max is { } hi ? (lo, hi) : null;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        Encoding.ASCII.GetBytes(type, buf[4..]);
        s.Write(buf);
        s.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, buf[4..]), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf[..4]);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
