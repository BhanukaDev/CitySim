using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CitySim.TerrainSystem.Erosion;

/// <summary>
/// The C++ erosion library (<c>native/erosion/</c>, built by its <c>build.sh</c>). Loaded by path and called through
/// function pointers, so there's no DllImport resolver to fight over and heights are passed in place (no copies).
/// Structs match <c>erosion.h</c> field for field.
/// </summary>
internal static unsafe class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ErosionParams
    {
        public int Seed;
        public float DropletsPerCell;
        public int MaxLifetime;
        public float Inertia, Capacity, MinSlope, ErodeSpeed, DepositSpeed, EvaporateSpeed, Gravity;
        public float ErosionRadius, MaxErodeDepth;
        public int ThermalIterations;
        public float TalusDegrees, ThermalRate;
        public float BreachDepth, SeaLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GroundParams
    {
        public float CellSize, RiverMinArea, GullyMinArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Progress
    {
        public float Value;
        public int Cancel;
    }

    private static readonly object Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged[Cdecl]<float*, int, int, float, ErosionParams*, Progress*, int> _erode;
    private static delegate* unmanaged[Cdecl]<float*, int, int, float, float, int, float*, Progress*, int> _findLakes;
    private static delegate* unmanaged[Cdecl]<float*, int, int, float, float, int, GroundParams*, float*, uint*, uint*, Progress*, int> _findWater;
    private static delegate* unmanaged[Cdecl]<float*, int, int, int, int, int, int, float, float, int, GroundParams*, float*, uint*, float*, uint*, uint*, Progress*, int> _findWaterWindow;
    private static delegate* unmanaged[Cdecl]<float*, void> _lastFindMs;

    /// <summary>Folder holding the library. The Godot side sets it (a globalized <c>res://native/erosion/bin</c>).</summary>
    public static string? Directory { get; set; }

    public static string FileName =>
        OperatingSystem.IsWindows() ? "citysim_erosion.dll" :
        OperatingSystem.IsMacOS() ? "libcitysim_erosion.dylib" : "libcitysim_erosion.so";

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            string path = Path.Combine(Directory ?? AppContext.BaseDirectory, FileName);
            if (!File.Exists(path))
                throw new DllNotFoundException($"Erosion library not found at {path}. Build it with native/erosion/build.sh.");
            var lib = NativeLibrary.Load(path);
            _erode = (delegate* unmanaged[Cdecl]<float*, int, int, float, ErosionParams*, Progress*, int>)NativeLibrary.GetExport(lib, "cs_erode");
            _findLakes = (delegate* unmanaged[Cdecl]<float*, int, int, float, float, int, float*, Progress*, int>)NativeLibrary.GetExport(lib, "cs_find_lakes");
            _findWater = (delegate* unmanaged[Cdecl]<float*, int, int, float, float, int, GroundParams*, float*, uint*, uint*, Progress*, int>)NativeLibrary.GetExport(lib, "cs_find_water");
            _findWaterWindow = (delegate* unmanaged[Cdecl]<float*, int, int, int, int, int, int, float, float, int, GroundParams*, float*, uint*, float*, uint*, uint*, Progress*, int>)NativeLibrary.GetExport(lib, "cs_find_water_window");
            _lastFindMs = (delegate* unmanaged[Cdecl]<float*, void>)NativeLibrary.GetExport(lib, "cs_last_find_water_ms");
            _loaded = true;
        }
    }

    public static int Erode(Span<float> heights, int width, int depth, float cellSize, in ErosionParams p, Progress* progress)
    {
        Load();
        if (heights.Length < width * depth) throw new ArgumentException("Heights too short.", nameof(heights));
        var copy = p;
        fixed (float* h = heights)
            return _erode(h, width, depth, cellSize, &copy, progress);
    }

    public static int FindLakes(ReadOnlySpan<float> heights, int width, int depth, float seaLevel, float minDepth, int minCells,
        Span<float> waterLevel, Progress* progress)
    {
        Load();
        if (heights.Length < width * depth || waterLevel.Length < width * depth) throw new ArgumentException("Buffers too short.");
        fixed (float* h = heights)
        fixed (float* w = waterLevel)
            return _findLakes(h, width, depth, seaLevel, minDepth, minCells, w, progress);
    }

    /// <summary><c>cs_find_water</c>; <paramref name="flowState"/> may be empty (not wanted).</summary>
    public static int FindWater(ReadOnlySpan<float> heights, int width, int depth, float seaLevel, float minDepth, int minCells,
        in GroundParams ground, Span<float> waterLevel, Span<uint> groundMasks, Span<uint> flowState, Progress* progress)
    {
        Load();
        int n = width * depth;
        if (heights.Length < n || waterLevel.Length < n || groundMasks.Length < n || (!flowState.IsEmpty && flowState.Length < n))
            throw new ArgumentException("Buffers too short.");
        var g = ground;
        fixed (float* h = heights)
        fixed (float* w = waterLevel)
        fixed (uint* m = groundMasks)
        fixed (uint* f = flowState)
            return _findWater(h, width, depth, seaLevel, minDepth, minCells, &g, w, m, f, progress);
    }

    /// <summary><c>cs_find_water_window</c>: outputs are window-sized, (x1 - x0 + 1) × (z1 - z0 + 1).</summary>
    public static int FindWaterWindow(ReadOnlySpan<float> heights, int width, int depth, int x0, int z0, int x1, int z1,
        float seaLevel, float minDepth, int minCells, in GroundParams ground, ReadOnlySpan<float> level, ReadOnlySpan<uint> flow,
        Span<float> waterOut, Span<uint> groundOut, Span<uint> flowOut, Progress* progress)
    {
        Load();
        int n = width * depth, wn = (x1 - x0 + 1) * (z1 - z0 + 1);
        if (heights.Length < n || level.Length < n || flow.Length < n || waterOut.Length < wn || groundOut.Length < wn || flowOut.Length < wn)
            throw new ArgumentException("Buffers too short.");
        var g = ground;
        fixed (float* h = heights)
        fixed (float* l = level)
        fixed (uint* f = flow)
        fixed (float* wo = waterOut)
        fixed (uint* go = groundOut)
        fixed (uint* fo = flowOut)
            return _findWaterWindow(h, width, depth, x0, z0, x1, z1, seaLevel, minDepth, minCells, &g, l, f, wo, go, fo, progress);
    }

    /// <summary>Milliseconds the last lake/ground search spent in each stage: flood, lakes, flow, masks (profiling).</summary>
    public static float[] LastFindWaterMs()
    {
        Load();
        var ms = new float[4];
        fixed (float* p = ms) _lastFindMs(p);
        return ms;
    }
}
