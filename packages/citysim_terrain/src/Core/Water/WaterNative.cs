using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CitySim.WaterSystem;

/// <summary>What the last simulation step saw (<c>CsWaterStats</c> in <c>water.h</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WaterStats
{
    /// <summary>m³ of water on the map.</summary>
    public double Volume;
    public float MaxDepth, MaxSpeed;
    /// <summary>Cells deeper than 1 cm.</summary>
    public int WetCells;
    public int ActiveTiles, Substeps;
    public float Simulated;
    /// <summary>Wet tiles not stepped because their water has settled.</summary>
    public int SleepingTiles;
    /// <summary>Cells whose outflow would have taken more water than they held (should stay 0).</summary>
    public int ClampHits;
    /// <summary>kg of pollutant in the water.</summary>
    public double Pollution;
    /// <summary>Tiles holding memory (water, next to it, or under a source); the rest of the grid costs nothing.</summary>
    public int AllocatedTiles;
    public float AllocatedMb;
}

/// <summary>
/// The C++ water library (<c>native/water/</c>, built by its <c>build.sh</c>), loaded by path and called through function
/// pointers like the erosion library. Structs match <c>water.h</c> field for field. Calls on one handle must not overlap.
/// </summary>
internal static unsafe class WaterNative
{
    public enum SourceType { Stream = 0, Level = 1, Lake = 2, Sea = 3 }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public int Type;
        public float X, Z, Radius, Rate, Level, MaxRate, Pollution;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public float Gravity, Damping, Evaporation, MaxSpeed, LevelRate;
        public int OpenEdges;
        public float Manning, PollutionDecay, PaintRate, PaintFade;
    }

    public enum Field { Depth = 0, Pollution = 1, Paint = 2 }

    private static readonly object Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged[Cdecl]<int, int, float, int, float*, int, int, int, IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, int> _setGround;
    private static delegate* unmanaged[Cdecl]<IntPtr, Params*, void> _setParams;
    private static delegate* unmanaged[Cdecl]<IntPtr, Source*, int, int> _setSources;
    private static delegate* unmanaged[Cdecl]<IntPtr, float, int, WaterStats*, int> _step;
    private static delegate* unmanaged[Cdecl]<int> _tileSize;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int> _changedTiles;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int, float*, float*, byte*, int> _readTiles;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int, int> _listTiles;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, float*, int> _getTile;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, float*, int> _setTile;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _commit;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _clear;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, float*, int> _raiseTile;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _fillSources;
    private static delegate* unmanaged[Cdecl]<IntPtr, float, float, float, float, int> _drain;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int, int> _listDrained;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, float*, int> _getDrained;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int, int> _readGround;

    /// <summary>Folder holding the library. The Godot side sets it (a globalized <c>res://native/water/bin</c>).</summary>
    public static string? Directory { get; set; }

    public static string FileName =>
        OperatingSystem.IsWindows() ? "citysim_water.dll" :
        OperatingSystem.IsMacOS() ? "libcitysim_water.dylib" : "libcitysim_water.so";

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            string path = Path.Combine(Directory ?? AppContext.BaseDirectory, FileName);
            if (!File.Exists(path))
                throw new DllNotFoundException($"Water library not found at {path}. Build it with native/water/build.sh.");
            var lib = NativeLibrary.Load(path);
            IntPtr F(string name) => NativeLibrary.GetExport(lib, name);
            _create = (delegate* unmanaged[Cdecl]<int, int, float, int, float*, int, int, int, IntPtr>)F("cs_water_create");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_destroy");
            _setGround = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, int>)F("cs_water_set_ground");
            _setParams = (delegate* unmanaged[Cdecl]<IntPtr, Params*, void>)F("cs_water_set_params");
            _setSources = (delegate* unmanaged[Cdecl]<IntPtr, Source*, int, int>)F("cs_water_set_sources");
            _step = (delegate* unmanaged[Cdecl]<IntPtr, float, int, WaterStats*, int>)F("cs_water_step");
            _tileSize = (delegate* unmanaged[Cdecl]<int>)F("cs_water_tile_size");
            _changedTiles = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int>)F("cs_water_changed_tiles");
            _readTiles = (delegate* unmanaged[Cdecl]<IntPtr, int*, int, float*, float*, byte*, int>)F("cs_water_read_tiles");
            _listTiles = (delegate* unmanaged[Cdecl]<IntPtr, int*, int, int>)F("cs_water_list_tiles");
            _getTile = (delegate* unmanaged[Cdecl]<IntPtr, int, int, float*, int>)F("cs_water_get_tile");
            _setTile = (delegate* unmanaged[Cdecl]<IntPtr, int, int, float*, int>)F("cs_water_set_tile");
            _commit = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_commit");
            _clear = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_clear");
            _raiseTile = (delegate* unmanaged[Cdecl]<IntPtr, int, float*, int>)F("cs_water_raise_tile");
            _fillSources = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_fill_sources");
            _drain = (delegate* unmanaged[Cdecl]<IntPtr, float, float, float, float, int>)F("cs_water_drain");
            _listDrained = (delegate* unmanaged[Cdecl]<IntPtr, int*, int, int>)F("cs_water_list_drained");
            _getDrained = (delegate* unmanaged[Cdecl]<IntPtr, int, float*, int>)F("cs_water_get_drained");
            _readGround = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int, int>)F("cs_water_read_ground");
            _loaded = true;
        }
    }

    /// <summary>
    /// <paramref name="heights"/> must stay pinned until <see cref="Destroy"/>: the library keeps the pointer and reads
    /// the ground from it when it allocates a tile.
    /// </summary>
    public static IntPtr Create(int width, int depth, float cellSize, int threads, float* heights, int terrainWidth,
        int terrainDepth, int factor)
    {
        Load();
        var h = _create(width, depth, cellSize, threads, heights, terrainWidth, terrainDepth, factor);
        if (h == IntPtr.Zero) throw new ArgumentException($"cs_water_create rejected {width}x{depth} at {cellSize} m.");
        return h;
    }

    public static void Destroy(IntPtr h) => _destroy(h);
    public static int TileSize { get { Load(); return _tileSize(); } }

    public static void SetGround(IntPtr h, int x0, int z0, int x1, int z1) => _setGround(h, x0, z0, x1, z1);

    public static void SetParams(IntPtr h, in Params p)
    {
        var copy = p;
        _setParams(h, &copy);
    }

    public static void SetSources(IntPtr h, ReadOnlySpan<Source> sources)
    {
        fixed (Source* p = sources) _setSources(h, p, sources.Length);
    }

    public static WaterStats Step(IntPtr h, float dt, int maxSubsteps)
    {
        WaterStats s;
        _step(h, dt, maxSubsteps, &s);
        return s;
    }

    public static int ChangedTiles(IntPtr h, Span<byte> flags, bool all) { fixed (byte* p = flags) return _changedTiles(h, p, all ? 1 : 0); }

    public static void ReadTiles(IntPtr h, ReadOnlySpan<int> tiles, Span<float> rgba, Span<float> pollution, Span<byte> visible)
    {
        fixed (int* t = tiles)
        fixed (float* o = rgba)
        fixed (float* e = pollution)
        fixed (byte* v = visible)
            _readTiles(h, t, tiles.Length, o, pollution.IsEmpty ? null : e, v);
    }

    public static int[] ListTiles(IntPtr h)
    {
        int n = _listTiles(h, null, 0);
        var tiles = new int[n];
        fixed (int* p = tiles) _listTiles(h, p, n);
        return tiles;
    }

    public static bool GetTile(IntPtr h, int tile, Field field, Span<float> into) { fixed (float* p = into) return _getTile(h, tile, (int)field, p) > 0; }
    public static void SetTile(IntPtr h, int tile, Field field, ReadOnlySpan<float> values) { fixed (float* p = values) _setTile(h, tile, (int)field, values.IsEmpty ? null : p); }
    public static void Commit(IntPtr h) => _commit(h);
    public static void Clear(IntPtr h) => _clear(h);
    public static void RaiseTile(IntPtr h, int tile, ReadOnlySpan<float> surface) { fixed (float* p = surface) _raiseTile(h, tile, p); }
    public static void FillSources(IntPtr h) => _fillSources(h);
    public static int Drain(IntPtr h, float x, float z, float radius, float level) => _drain(h, x, z, radius, level);

    public static int[] ListDrained(IntPtr h)
    {
        int n = _listDrained(h, null, 0);
        var tiles = new int[n];
        fixed (int* p = tiles) _listDrained(h, p, n);
        return tiles;
    }

    public static bool GetDrained(IntPtr h, int tile, Span<float> into) { fixed (float* p = into) return _getDrained(h, tile, p) > 0; }

    public static bool ReadGround(IntPtr h, Span<byte> into, bool force, int markFactor)
    {
        fixed (byte* p = into) return _readGround(h, p, force ? 1 : 0, markFactor) > 0;
    }
}
