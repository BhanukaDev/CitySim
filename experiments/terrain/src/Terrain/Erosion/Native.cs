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
    public struct Progress
    {
        public float Value;
        public int Cancel;
    }

    private static readonly object Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged[Cdecl]<float*, int, int, float, ErosionParams*, Progress*, int> _erode;
    private static delegate* unmanaged[Cdecl]<float*, int, int, float, float, int, float*, Progress*, int> _findLakes;

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
}
