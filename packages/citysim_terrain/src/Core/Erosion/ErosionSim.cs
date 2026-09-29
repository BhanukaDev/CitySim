using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CitySim.TerrainSystem.Erosion;

/// <summary>Runs the native erosion on a <see cref="HeightMap"/>. Engine-agnostic; call from a worker thread.</summary>
public static unsafe class ErosionSim
{
    /// <summary>
    /// Erodes <paramref name="map"/> in place. Returns false if cancelled; the map is then half-eroded, so run on a copy
    /// when that matters. <paramref name="seaLevel"/>: ground below it that connects to the map edge is sea, which hollows
    /// may drain into (null = no sea). <paramref name="progress"/> gets 0..1 about ten times a second, from a timer thread.
    /// </summary>
    public static bool Run(HeightMap map, ErosionSettings s, float? seaLevel = null, CancellationToken ct = default, Action<float>? progress = null)
    {
        var p = new Native.ErosionParams
        {
            Seed = s.Seed,
            DropletsPerCell = s.Droplets,
            MaxLifetime = s.Lifetime,
            Inertia = s.Inertia,
            Capacity = s.Capacity,
            MinSlope = 0.01f,
            ErodeSpeed = s.ErodeSpeed,
            DepositSpeed = s.DepositSpeed,
            EvaporateSpeed = s.Evaporation,
            Gravity = s.Gravity,
            ErosionRadius = MathF.Max(s.Radius / map.CellSize, 0.5f),
            MaxErodeDepth = s.MaxErodeDepth,
            ThermalIterations = s.ThermalIterations,
            TalusDegrees = s.TalusDegrees,
            ThermalRate = s.ThermalRate,
            BreachDepth = s.DrainDepth,
            SeaLevel = seaLevel ?? -1e30f,
        };
        int result = WithProgress(ct, progress, (Native.Progress* prog) => Native.Erode(map.Data, map.Width, map.Depth, map.CellSize, p, prog));
        map.Invalidate();
        if (result < 0) throw new InvalidOperationException($"cs_erode failed ({result}).");
        return result == 0;
    }

    internal delegate int ProgressCall(Native.Progress* p);

    /// <summary>
    /// Runs <paramref name="call"/> with a progress struct in unmanaged memory: cancelling sets its flag, and a timer
    /// reports its value while the call runs.
    /// </summary>
    internal static int WithProgress(CancellationToken ct, Action<float>? progress, ProgressCall call)
    {
        var prog = (Native.Progress*)NativeMemory.AllocZeroed((nuint)sizeof(Native.Progress));
        var timer = progress is null ? null : new Timer(_ => progress(Volatile.Read(ref prog->Value)), null, 100, 100);
        try
        {
            using var reg = ct.Register(() => Volatile.Write(ref prog->Cancel, 1));
            return call(prog);
        }
        finally
        {
            if (timer is not null)
            {
                // Wait for a callback that's already running, so it never reads freed memory.
                using var stopped = new ManualResetEvent(false);
                if (timer.Dispose(stopped)) stopped.WaitOne();
            }
            NativeMemory.Free(prog);
        }
    }
}
