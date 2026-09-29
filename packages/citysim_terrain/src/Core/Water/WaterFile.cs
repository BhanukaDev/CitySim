using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CitySim.TerrainSystem;

namespace CitySim.WaterSystem;

/// <summary>
/// A map's water as saved: settings, sources, the depth per water cell (null = no saved water) and the pollutant mass
/// per water cell in kg (null = clean) and the wet paint per water cell, 0..1 (null = none). Width × Depth is the water
/// grid it was saved on (a map saved before M6 phase 3f has a coarser one on big maps; see <see cref="WaterGrid.Resample"/>).
/// </summary>
public sealed record WaterData(WaterSettings Settings, IReadOnlyList<WaterSource> Sources, int Width, int Depth, WaterGrid? DepthGrid,
    WaterGrid? PollutionGrid = null, WaterGrid? PaintGrid = null);

/// <summary>
/// The water section of a map file (<see cref="MapFile"/> v4), little-endian:
/// i32 section version (3); f32 speed, f32 evaporation (mm/min), bool open edges, bool paused, f32 pollutant half-life
/// (min, v2), f32 paint minutes, f32 paint fade hours (v3); i32 source count, then per source i32 id, i32 kind, f32 x, z, radius, level, flow rate, max flow,
/// pollution (kg/s, v2); i32 grid width, i32 grid depth (0 × 0 = no water saved), i32 tile size (256), i32 tile count,
/// then per tile that has water: i32 tx, i32 tz, i32 length + a zlib stream of half-float depths; then (v2) the
/// pollutant the same way (i32 tile count, tiles with any pollutant as zlib'd f32 kg); then (v3) the wet paint the same
/// way (bytes, 0..255). Section versions 1 (no pollution) and 2 (no paint) still load. Engine-agnostic.
/// </summary>
public static class WaterFile
{
    private const int SectionVersion = 3;
    private const int Tile = 256;

    public static void Write(BinaryWriter w, WaterData data)
    {
        w.Write(SectionVersion);
        w.Write(data.Settings.Speed);
        w.Write(data.Settings.EvaporationMmPerMin);
        w.Write(data.Settings.OpenEdges);
        w.Write(data.Settings.Paused);
        w.Write(data.Settings.PollutionHalfLifeMin);
        w.Write(data.Settings.PaintMinutes);
        w.Write(data.Settings.PaintFadeHours);
        w.Write(data.Sources.Count);
        foreach (var s in data.Sources)
        {
            w.Write(s.Id);
            w.Write((int)s.Kind);
            w.Write(s.X); w.Write(s.Z); w.Write(s.Radius); w.Write(s.Level); w.Write(s.FlowRate); w.Write(s.MaxFlow);
            w.Write(s.Pollution);
        }
        if (data.DepthGrid is not { } grid)
        {
            w.Write(0); w.Write(0); w.Write(Tile); w.Write(0);
            w.Write(0);
            w.Write(0);
            return;
        }
        w.Write(data.Width);
        w.Write(data.Depth);
        w.Write(Tile);
        WriteTiles(w, grid, v => (Half)v);
        if (data.PollutionGrid is { } pollution && Same(pollution, grid)) WriteTiles(w, pollution, v => v);
        else w.Write(0);
        if (data.PaintGrid is { } paint && Same(paint, grid))
            WriteTiles(w, paint, v => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f));
        else w.Write(0);
    }

    private static bool Same(WaterGrid a, WaterGrid b) => a.Width == b.Width && a.Depth == b.Depth;

    /// <summary>Writes the 256² tiles of a grid that hold anything above 0, each as a zlib stream of T.</summary>
    private static void WriteTiles<T>(BinaryWriter w, WaterGrid grid, Func<float, T> convert) where T : unmanaged
    {
        int width = grid.Width, depth = grid.Depth;
        int tilesX = (width + Tile - 1) / Tile, tilesZ = (depth + Tile - 1) / Tile, per = Tile / WaterGrid.Tile;
        var tiles = new List<(int, int, byte[])>();
        for (int tz = 0; tz < tilesZ; tz++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                // Skip file tiles whose grid tiles are all missing without touching their cells.
                bool held = false;
                for (int gz = tz * per; gz < Math.Min((tz + 1) * per, grid.TilesZ) && !held; gz++)
                    for (int gx = tx * per; gx < Math.Min((tx + 1) * per, grid.TilesX) && !held; gx++)
                        held = grid.GetTile(gz * grid.TilesX + gx) is not null;
                if (!held) continue;
                int x0 = tx * Tile, z0 = tz * Tile, tw = Math.Min(Tile, width - x0), td = Math.Min(Tile, depth - z0);
                var values = new T[tw * td];
                bool any = false;
                for (int z = 0; z < td; z++)
                    for (int x = 0; x < tw; x++)
                    {
                        float v = grid[x0 + x, z0 + z];
                        if (v > 0f) any = true;
                        values[z * tw + x] = convert(v);
                    }
                if (any) tiles.Add((tx, tz, MapFile.Deflate(MemoryMarshal.AsBytes(values.AsSpan()))));
            }
        w.Write(tiles.Count);
        foreach (var (tx, tz, blob) in tiles)
        {
            w.Write(tx);
            w.Write(tz);
            w.Write(blob.Length);
            w.Write(blob);
        }
    }

    public static WaterData Read(BinaryReader r)
    {
        int version = r.ReadInt32();
        if (version is < 1 or > SectionVersion) throw new InvalidDataException($"Map file water section version {version} isn't supported.");
        var settings = new WaterSettings
        {
            Speed = r.ReadSingle(), EvaporationMmPerMin = r.ReadSingle(), OpenEdges = r.ReadBoolean(), Paused = r.ReadBoolean(),
        };
        if (version >= 2) settings = settings with { PollutionHalfLifeMin = r.ReadSingle() };
        if (version >= 3) settings = settings with { PaintMinutes = r.ReadSingle(), PaintFadeHours = r.ReadSingle() };
        int count = r.ReadInt32();
        if (count is < 0 or > 100_000) throw new InvalidDataException("Map file water section is corrupt.");
        var sources = new List<WaterSource>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32(), kind = r.ReadInt32();
            float x = r.ReadSingle(), z = r.ReadSingle(), radius = r.ReadSingle(), level = r.ReadSingle(),
                flow = r.ReadSingle(), maxFlow = r.ReadSingle();
            float pollution = version >= 2 ? r.ReadSingle() : 0f;
            if (kind is < 0 or > (int)WaterSourceKind.Sea) throw new InvalidDataException("Map file water source is corrupt.");
            sources.Add(new WaterSource(id, (WaterSourceKind)kind, x, z, radius, level, flow, maxFlow, pollution));
        }
        int width = r.ReadInt32(), depth = r.ReadInt32(), tile = r.ReadInt32();
        if (width is < 0 or > 8193 || depth is < 0 or > 8193 || tile != Tile)
            throw new InvalidDataException("Map file water grid is corrupt.");
        WaterGrid? grid = width > 0 && depth > 0 ? new WaterGrid(width, depth) : null;
        ReadTiles<Half>(r, grid, v => (float)v);
        WaterGrid? pollutionGrid = null;
        if (version >= 2)
        {
            pollutionGrid = grid is null ? null : new WaterGrid(width, depth);
            if (!ReadTiles<float>(r, pollutionGrid, v => v)) pollutionGrid = null;
        }
        WaterGrid? paintGrid = null;
        if (version >= 3)
        {
            paintGrid = grid is null ? null : new WaterGrid(width, depth);
            if (!ReadTiles<byte>(r, paintGrid, v => v / 255f)) paintGrid = null;
        }
        return new WaterData(settings, sources, width, depth, grid, pollutionGrid, paintGrid);
    }

    /// <summary>Reads what <see cref="WriteTiles"/> wrote into <paramref name="grid"/>; false when there were no tiles.</summary>
    private static bool ReadTiles<T>(BinaryReader r, WaterGrid? grid, Func<T, float> convert) where T : unmanaged
    {
        int width = grid?.Width ?? 0, depth = grid?.Depth ?? 0;
        int tiles = r.ReadInt32();
        if (tiles < 0) throw new InvalidDataException("Map file water grid is corrupt.");
        int tilesX = (width + Tile - 1) / Tile, tilesZ = (depth + Tile - 1) / Tile;
        for (int i = 0; i < tiles; i++)
        {
            int tx = r.ReadInt32(), tz = r.ReadInt32(), length = r.ReadInt32();
            if (grid is null || tx < 0 || tz < 0 || tx >= tilesX || tz >= tilesZ || length is < 0 or > 64 << 20)
                throw new InvalidDataException("Map file water tile is corrupt.");
            int x0 = tx * Tile, z0 = tz * Tile, tw = Math.Min(Tile, width - x0), td = Math.Min(Tile, depth - z0);
            var values = new T[tw * td];
            MapFile.Inflate(r.ReadBytes(length), MemoryMarshal.AsBytes(values.AsSpan()));
            for (int z = 0; z < td; z++)
                for (int x = 0; x < tw; x++)
                {
                    float v = convert(values[z * tw + x]);
                    if (v > 0f) grid[x0 + x, z0 + z] = v;
                }
        }
        return tiles > 0;
    }
}
