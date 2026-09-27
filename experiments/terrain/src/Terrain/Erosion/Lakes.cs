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
    public uint[]? Ground { get; }

    public LakeMap(int width, int depth, float cellSize, float[] level, int count, uint[]? ground = null)
    {
        Ground = ground;
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        Level = level;
        Count = count;
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
        int minCells = (int)MathF.Ceiling(s.MinArea / (map.CellSize * map.CellSize));
        var gp = new Native.GroundParams { CellSize = map.CellSize, RiverMinArea = s.RiverMinArea, GullyMinArea = s.GullyMinArea };
        int count = ErosionSim.WithProgress(ct, null, (Native.Progress* p) => masks is null
            ? Native.FindLakes(map.Data, map.Width, map.Depth, seaLevel ?? -1e30f, s.MinDepth, minCells, level, p)
            : Native.FindWater(map.Data, map.Width, map.Depth, seaLevel ?? -1e30f, s.MinDepth, minCells, gp, level, masks, p));
        if (count < 0) throw new InvalidOperationException($"cs_find_water failed ({count}).");
        return ct.IsCancellationRequested ? null : new LakeMap(map.Width, map.Depth, map.CellSize, level, count, masks);
    }
}
