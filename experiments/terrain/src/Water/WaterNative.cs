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
        public float X, Z, Radius, Rate, Level, MaxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public float Gravity, Damping, Evaporation, MaxSpeed, LevelRate;
        public int OpenEdges;
        public float Manning;
    }

    private static readonly object Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged[Cdecl]<int, int, float, int, IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, int, int, int, int, int, int, int, int> _setGround;
    private static delegate* unmanaged[Cdecl]<IntPtr, Params*, void> _setParams;
    private static delegate* unmanaged[Cdecl]<IntPtr, Source*, int, int> _setSources;
    private static delegate* unmanaged[Cdecl]<IntPtr, float, int, WaterStats*, int> _step;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, byte*, int, int> _read;
    private static delegate* unmanaged[Cdecl]<int> _tileSize;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, void> _getDepth;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, void> _setDepth;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, void> _raiseTo;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _fillSources;

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
            _create = (delegate* unmanaged[Cdecl]<int, int, float, int, IntPtr>)F("cs_water_create");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_destroy");
            _setGround = (delegate* unmanaged[Cdecl]<IntPtr, float*, int, int, int, int, int, int, int, int>)F("cs_water_set_ground");
            _setParams = (delegate* unmanaged[Cdecl]<IntPtr, Params*, void>)F("cs_water_set_params");
            _setSources = (delegate* unmanaged[Cdecl]<IntPtr, Source*, int, int>)F("cs_water_set_sources");
            _step = (delegate* unmanaged[Cdecl]<IntPtr, float, int, WaterStats*, int>)F("cs_water_step");
            _read = (delegate* unmanaged[Cdecl]<IntPtr, float*, byte*, int, int>)F("cs_water_read");
            _tileSize = (delegate* unmanaged[Cdecl]<int>)F("cs_water_tile_size");
            _getDepth = (delegate* unmanaged[Cdecl]<IntPtr, float*, void>)F("cs_water_get_depth");
            _setDepth = (delegate* unmanaged[Cdecl]<IntPtr, float*, void>)F("cs_water_set_depth");
            _raiseTo = (delegate* unmanaged[Cdecl]<IntPtr, float*, void>)F("cs_water_raise_to");
            _fillSources = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("cs_water_fill_sources");
            _loaded = true;
        }
    }

    public static IntPtr Create(int width, int depth, float cellSize, int threads = 0)
    {
        Load();
        var h = _create(width, depth, cellSize, threads);
        if (h == IntPtr.Zero) throw new ArgumentException($"cs_water_create rejected {width}x{depth} at {cellSize} m.");
        return h;
    }

    public static void Destroy(IntPtr h) => _destroy(h);
    public static int TileSize { get { Load(); return _tileSize(); } }

    public static void SetGround(IntPtr h, ReadOnlySpan<float> heights, int terrainWidth, int terrainDepth, int factor,
        int x0, int z0, int x1, int z1)
    {
        fixed (float* p = heights)
            if (_setGround(h, p, terrainWidth, terrainDepth, factor, x0, z0, x1, z1) != 0)
                throw new ArgumentException("cs_water_set_ground: terrain size doesn't match the water grid.");
    }

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

    public static int Read(IntPtr h, Span<float> rgba, Span<byte> tileChanged, bool all)
    {
        fixed (float* o = rgba)
        fixed (byte* t = tileChanged)
            return _read(h, o, t, all ? 1 : 0);
    }

    public static void GetDepth(IntPtr h, Span<float> depth) { fixed (float* p = depth) _getDepth(h, p); }
    public static void SetDepth(IntPtr h, ReadOnlySpan<float> depth) { fixed (float* p = depth) _setDepth(h, depth.IsEmpty ? null : p); }
    public static void RaiseTo(IntPtr h, ReadOnlySpan<float> surface) { fixed (float* p = surface) _raiseTo(h, p); }
    public static void FillSources(IntPtr h) => _fillSources(h);
}
