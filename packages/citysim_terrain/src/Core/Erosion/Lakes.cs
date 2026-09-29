using System;
using System.Threading;

namespace CitySim.TerrainSystem.Erosion;

/// <summary>
/// Standing water: for every heightmap vertex, the surface height of the lake over it, or NaN when dry. A lake is a
/// depression filled up to the height where it would spill over (Priority-Flood), so lakes always follow the ground.
/// The sea (see <see cref="Lakes.Find"/>) isn't a lake. Engine-agnostic.
/// </summary>
public sealed class LakeMap
{
    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }
    /// <summary>Number of lakes.</summary>
    public int Count { get; }
    /// <summary>Water surface height per vertex (row-major, z * Width + x); NaN = dry.</summary>
    public float[] Level { get; }

    /// <summary>
    /// Ground masks for texturing, one packed value per vertex (row-major), or null when not asked for. Bytes, lowest
    /// first: shore (distance to lakes, sea and rivers), gully (distance to a gully or river bed), wear (how hard running
    /// water scours the ground) and deposit (where the sediment it carries settles). Encodings: <c>cs_find_water</c> in
    /// <c>native/erosion/erosion.h</c>. Derived from the heights, like the lakes, so they follow every edit.
    /// </summary>
    public uint[]? Ground { get; private set; }

    /// <summary>
    /// Per vertex, what a window search (<see cref="Lakes.FindWindow"/>) needs from outside its window: catchment area and
    /// the gully bit, packed (<c>cs_find_water</c>'s flow_state). Null when the search didn't ask for ground masks.
    /// </summary>
    public uint[]? Flow { get; }

    public LakeMap(int width, int depth, float cellSize, float[] level, int count, uint[]? ground = null, uint[]? flow = null)
    {
        Ground = ground;
        Flow = flow;
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        Level = level;
        Count = count;
    }

    /// <summary>Lets go of <see cref="Ground"/> once it's been uploaded (268 MB at 28.7 km); window searches don't need it.</summary>
    public void DropGround() => Ground = null;

    /// <summary>
    /// Writes a window search's results inside <paramref name="inner"/> (which lies inside <c>w.Window</c>) into
    /// <see cref="Level"/> and <see cref="Flow"/>. <see cref="Count"/> stays as the last full search found it.
    /// </summary>
    public void Apply(LakeWindow w, VertexRect inner)
    {
        if (Flow is null) throw new InvalidOperationException("No flow state: this map came from a search without ground masks.");
        var win = w.Window;
        for (int z = inner.MinZ; z <= inner.MaxZ; z++)
        {
            int src = (z - win.MinZ) * win.Width + (inner.MinX - win.MinX), dst = z * Width + inner.MinX;
            w.Level.AsSpan(src, inner.Width).CopyTo(Level.AsSpan(dst, inner.Width));
            w.Flow.AsSpan(src, inner.Width).CopyTo(Flow.AsSpan(dst, inner.Width));
        }
    }

    /// <summary>Lake surface at vertex (x, z), or NaN.</summary>
    public float LevelAt(int x, int z) => x < 0 || z < 0 || x >= Width || z >= Depth ? float.NaN : Level[z * Width + x];

    /// <summary>Water depth at local position (x, z) in metres (0 when dry): nearest vertex's lake level over the ground.</summary>
    public float WaterDepth(HeightMap ground, float x, float z)
    {
        float level = LevelAt((int)MathF.Round(x / CellSize), (int)MathF.Round(z / CellSize));
        return float.IsNaN(level) ? 0f : MathF.Max(0f, level - ground.SampleHeight(x, z));
    }

    public bool IsUnderwater(HeightMap ground, float x, float z) => WaterDepth(ground, x, z) > 0f;

    /// <summary>Number of wet vertices.</summary>
    public int WetVertices()
    {
        int n = 0;
        foreach (float v in Level) if (!float.IsNaN(v)) n++;
        return n;
    }
}

/// <summary>A window search's results (<see cref="Lakes.FindWindow"/>): window-sized, row-major over <see cref="Window"/>.</summary>
public sealed record LakeWindow(VertexRect Window, float[] Level, uint[] Ground, uint[] Flow, int Count);

public static class Lakes
{
    /// <summary>
    /// Finds the lakes on <paramref name="map"/>. Ground below <paramref name="seaLevel"/> that connects to the map edge
    /// is sea, not lake (pass null when the map has no sea). With <paramref name="ground"/> it also works out the
    /// <see cref="LakeMap.Ground"/> masks (rivers, gullies, wear, deposits). Returns null if cancelled.
    /// </summary>
    public static unsafe LakeMap? Find(HeightMap map, LakeSettings s, float? seaLevel, CancellationToken ct = default, bool ground = false)
    {
        var level = new float[map.Width * map.Depth];
        var masks = ground ? new uint[level.Length] : null;
        var flow = ground ? new uint[level.Length] : null;
        int minCells = MinCells(map, s);
        var gp = Ground(map, s);
        int count = ErosionSim.WithProgress(ct, null, (Native.Progress* p) => masks is null
            ? Native.FindLakes(map.Data, map.Width, map.Depth, seaLevel ?? -1e30f, s.MinDepth, minCells, level, p)
            : Native.FindWater(map.Data, map.Width, map.Depth, seaLevel ?? -1e30f, s.MinDepth, minCells, gp, level, masks, flow, p));
        if (count < 0) throw new InvalidOperationException($"cs_find_water failed ({count}).");
        return ct.IsCancellationRequested ? null : new LakeMap(map.Width, map.Depth, map.CellSize, level, count, masks, flow);
    }

    /// <summary>
    /// Finds lakes and ground masks again inside <paramref name="window"/> only, after an edit there, using what
    /// <paramref name="last"/> (a search with ground masks on this map) knows about the rest: lakes crossing the window's
    /// border keep their level, rivers flowing in keep their catchment. Near the window's border the masks can be off
    /// (blur and distances see nothing outside), so callers keep only an inner part of it. Changes don't reach anything
    /// downstream of the window; a full <see cref="Find"/> later puts that right. Returns null if cancelled.
    /// </summary>
    public static unsafe LakeWindow? FindWindow(HeightMap map, LakeMap last, VertexRect window, LakeSettings s, float? seaLevel,
        CancellationToken ct = default)
    {
        if (last.Flow is null) throw new ArgumentException("The last search has no flow state (it had no ground masks).", nameof(last));
        if (last.Width != map.Width || last.Depth != map.Depth) throw new ArgumentException("The last search is for another map size.", nameof(last));
        int n = window.Width * window.Depth;
        var level = new float[n];
        var masks = new uint[n];
        var flow = new uint[n];
        var gp = Ground(map, s);
        int count = ErosionSim.WithProgress(ct, null, (Native.Progress* p) => Native.FindWaterWindow(map.Data, map.Width, map.Depth,
            window.MinX, window.MinZ, window.MaxX, window.MaxZ, seaLevel ?? -1e30f, s.MinDepth, MinCells(map, s), gp,
            last.Level, last.Flow, level, masks, flow, p));
        if (count < 0) throw new InvalidOperationException($"cs_find_water_window failed ({count}).");
        return ct.IsCancellationRequested ? null : new LakeWindow(window, level, masks, flow, count);
    }

    private static int MinCells(HeightMap map, LakeSettings s) => (int)MathF.Ceiling(s.MinArea / (map.CellSize * map.CellSize));

    private static Native.GroundParams Ground(HeightMap map, LakeSettings s) =>
        new() { CellSize = map.CellSize, RiverMinArea = s.RiverMinArea, GullyMinArea = s.GullyMinArea };

    /// <summary>Milliseconds the last <see cref="Find"/> spent in each stage: flood, lakes, flow, masks (profiling).</summary>
    public static float[] LastStageMs() => Native.LastFindWaterMs();
}
