using System;
using System.Collections.Generic;

namespace CitySim.WaterSystem;

/// <summary>
/// A sparse per-cell grid over the water cells (depth, pollutant, wet paint, a drained surface), in the sim's
/// <see cref="Tile"/>² tiles: a tile that holds nothing is null and costs nothing, so a 4097² grid with one river in it
/// stays small. Cells inside a tile are row-major; partial tiles at the grid edge still have Tile² values (the ones
/// past the edge are unused). Unset cells read <see cref="Empty"/>. Engine-agnostic.
/// </summary>
public sealed class WaterGrid
{
    public const int Tile = 64;
    public const int TileCells = Tile * Tile;

    public int Width { get; }
    public int Depth { get; }
    public int TilesX { get; }
    public int TilesZ { get; }
    /// <summary>What cells in missing tiles read (0, or NaN for surfaces).</summary>
    public float Empty { get; }

    private readonly float[]?[] _tiles;

    public WaterGrid(int width, int depth, float empty = 0f)
    {
        Width = width;
        Depth = depth;
        Empty = empty;
        TilesX = (width + Tile - 1) / Tile;
        TilesZ = (depth + Tile - 1) / Tile;
        _tiles = new float[TilesX * TilesZ][];
    }

    public static int Local(int x, int z) => (z % Tile) * Tile + x % Tile;
    public int TileOf(int x, int z) => z / Tile * TilesX + x / Tile;

    public float this[int x, int z]
    {
        get => _tiles[TileOf(x, z)] is { } t ? t[Local(x, z)] : Empty;
        set => GetOrAdd(TileOf(x, z))[Local(x, z)] = value;
    }

    public float[]? GetTile(int t) => _tiles[t];
    public void SetTile(int t, float[]? values) => _tiles[t] = values;

    public float[] GetOrAdd(int t)
    {
        if (_tiles[t] is { } a) return a;
        a = new float[TileCells];
        if (Empty != 0f) Array.Fill(a, Empty);
        return _tiles[t] = a;
    }

    /// <summary>Indices of the tiles that hold values.</summary>
    public IEnumerable<int> Tiles()
    {
        for (int t = 0; t < _tiles.Length; t++)
            if (_tiles[t] is not null) yield return t;
    }

    public bool IsEmpty
    {
        get
        {
            foreach (var t in _tiles)
                if (t is not null) return false;
            return true;
        }
    }

    /// <summary>Sum over all cells that hold a finite value.</summary>
    public double Sum()
    {
        double s = 0;
        for (int t = 0; t < _tiles.Length; t++)
            if (_tiles[t] is { } a)
                ForTile(t, (_, _, i) => { if (float.IsFinite(a[i])) s += a[i]; });
        return s;
    }

    /// <summary>Calls f(x, z, local index) for the cells of tile t inside the grid.</summary>
    public void ForTile(int t, Action<int, int, int> f)
    {
        int x0 = t % TilesX * Tile, z0 = t / TilesX * Tile;
        int nx = Math.Min(Tile, Width - x0), nz = Math.Min(Tile, Depth - z0);
        for (int lz = 0; lz < nz; lz++)
            for (int lx = 0; lx < nx; lx++) f(x0 + lx, z0 + lz, lz * Tile + lx);
    }

    /// <summary>Row-major Width × Depth copy (small grids: demos, tests).</summary>
    public float[] ToDense()
    {
        var dense = new float[Width * Depth];
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++) dense[z * Width + x] = this[x, z];
        return dense;
    }

    /// <summary>A grid from row-major values; tiles where every value equals <paramref name="empty"/> stay missing.</summary>
    public static WaterGrid FromDense(float[] dense, int width, int depth, float empty = 0f)
    {
        if (dense.Length != width * depth) throw new ArgumentException("Grid has the wrong size.", nameof(dense));
        var g = new WaterGrid(width, depth, empty);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                float v = dense[z * width + x];
                if (v.Equals(empty)) continue;
                g[x, z] = v;
            }
        return g;
    }

    /// <summary>
    /// This grid on another grid over the same map (a save from a coarser or finer water grid): each new cell takes the
    /// nearest old cell's value times <paramref name="scale"/> (the cell-area ratio for per-cell amounts like pollutant
    /// kg; 1 for depths and paint).
    /// </summary>
    public WaterGrid Resample(int width, int depth, float scale = 1f)
    {
        var g = new WaterGrid(width, depth, Empty);
        float sx = (Width - 1f) / Math.Max(width - 1, 1), sz = (Depth - 1f) / Math.Max(depth - 1, 1);
        for (int z = 0; z < depth; z++)
        {
            int oz = Math.Clamp((int)MathF.Round(z * sz), 0, Depth - 1);
            for (int x = 0; x < width; x++)
            {
                int ox = Math.Clamp((int)MathF.Round(x * sx), 0, Width - 1);
                if (_tiles[TileOf(ox, oz)] is not { } a) continue;
                float v = a[Local(ox, oz)];
                if (v.Equals(Empty)) continue;
                g[x, z] = v * scale;
            }
        }
        return g;
    }
}
