// CitySim water simulation: shallow water on a heightfield with the "virtual pipes" model (O'Brien & Hodgins 1995,
// Mei, Decaudin & Hu 2007). Each cell holds a water depth and four outflow pipes to its neighbours; water flows by
// the difference in surface height, so it runs downhill, fills hollows, spills over rims and reacts to edits.
// Plain C ABI, called from C# through function pointers (src/Water/WaterNative.cs); structs match field for field.
// A handle owns the state; calls on one handle must not overlap (the C# side runs them on one worker thread).
// Grids are row-major (index z * width + x), in metres. No Godot types.
// Storage is sparse, in square tiles of cs_water_tile_size() cells (tile index tz * tiles_x + tx, cells inside a tile
// row-major, tile_size² floats per tile even for partial tiles at the grid edge): only tiles with water, next to it or
// under a source hold memory. Bulk data moves per tile.
#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define CS_API extern "C" __declspec(dllexport)
#else
#define CS_API extern "C" __attribute__((visibility("default")))
#endif

enum CsWaterSourceType : int32_t {
    CS_WATER_STREAM = 0, // adds `rate` m³/s, spread over the radius
    CS_WATER_LEVEL = 1,  // pulls the surface toward `level` inside the radius: water flows in or out (border rivers)
    CS_WATER_LAKE = 2,   // fills toward `level` inside the radius at up to `max_rate` m³/s, never removes water
    CS_WATER_SEA = 3,    // holds every border cell below `level` at `level` (x, z, radius unused)
};

struct CsWaterSource {
    int32_t type;          // CsWaterSourceType
    float x, z;            // centre, metres from the grid origin
    float radius;          // metres
    float rate;            // m³/s (stream)
    float level;           // m (level, lake, sea)
    float max_rate;        // m³/s (lake)
    float pollution;       // kg/s of pollutant added with the water (stream)
};

struct CsWaterParams {
    float gravity;         // m/s²
    float damping;         // 1/s: pipe flow lost per second (calms sloshing)
    float evaporation;     // m/s taken from every wet cell
    float max_speed;       // m/s: water velocity cap (keeps waterfalls and thin sheets stable)
    float level_rate;      // 1/s: how fast level and lake sources pull the surface toward their level
    int32_t open_edges;    // bits: 1 west (x = 0), 2 east, 4 north (z = 0), 8 south. Open edges let water leave the map.
    float manning;         // bed roughness (Manning's n, s/m^(1/3)): slows shallow fast water; 0 = frictionless
    float pollution_decay; // 1/s: pollutant lost per second (breaks down, settles); 0 = keeps forever
    float paint_rate;      // 1/s: how fast ground under standing or running water gets painted wet (0..1)
    float paint_fade;      // 1/s: how fast the paint fades where there's no water any more; 0 = never
};

struct CsWaterStats {
    double volume;         // m³ of water on the map
    float max_depth;       // m
    float max_speed;       // m/s
    int32_t wet_cells;     // cells deeper than 1 cm
    int32_t active_tiles;  // tiles stepped in the last cs_water_step
    int32_t substeps;      // substeps in the last cs_water_step
    float simulated;       // seconds actually simulated by the last cs_water_step (less than asked if max_substeps hit)
    int32_t sleeping_tiles; // wet tiles not stepped because their water has settled
    int32_t clamp_hits;    // cells whose outflow would have taken more than they held (should stay 0: pipes are scaled)
    double pollution;      // kg of pollutant in the water
    int32_t allocated_tiles; // tiles holding cells (the rest of the grid is dry ground and costs nothing)
    float allocated_mb;    // their memory
};

// Per-cell fields for cs_water_get_tile / cs_water_set_tile.
enum CsWaterField : int32_t {
    CS_WATER_FIELD_DEPTH = 0,     // m
    CS_WATER_FIELD_POLLUTION = 1, // kg of pollutant per cell
    CS_WATER_FIELD_PAINT = 2,     // wet paint, 0..1
};

typedef struct CsWater CsWater;

// width x depth water cells, cell_size metres apart, over a terrain heightmap (terrain_width x terrain_depth vertices,
// row-major; `factor` terrain cells per water cell: each water cell's ground is halfway between the mean and the
// lowest of the terrain vertices around it, so narrow stream beds survive). The heightmap is read, not copied: it must
// stay alive and in place until cs_water_destroy. Returns null on bad arguments.
CS_API CsWater* cs_water_create(int32_t width, int32_t depth, float cell_size, int32_t threads, const float* heights,
                                int32_t terrain_width, int32_t terrain_depth, int32_t factor);
CS_API void cs_water_destroy(CsWater* w);

// The terrain heights under water cells x0..x1, z0..z1 (inclusive, water coordinates) changed: re-read them.
CS_API int32_t cs_water_set_ground(CsWater* w, int32_t x0, int32_t z0, int32_t x1, int32_t z1);

CS_API void cs_water_set_params(CsWater* w, const CsWaterParams* params);
CS_API int32_t cs_water_set_sources(CsWater* w, const CsWaterSource* sources, int32_t count);

// Advances `dt` seconds in substeps short enough to stay stable (CFL), at most max_substeps of them. One call is one
// tick: the tiles to step are chosen at its start, and tiles whose water has settled for a few ticks go to sleep
// (not stepped) until something nearby changes. The result depends only on the state and dt, never on timing or the
// thread count, so a fixed dt per call makes runs repeatable.
CS_API int32_t cs_water_step(CsWater* w, float dt, int32_t max_substeps, CsWaterStats* stats);
CS_API int32_t cs_water_tile_size(void);

// Tiles to redraw: flags (tiles_x * tiles_z bytes) gets 1 for every tile changed since the last call (every tile with
// all = 1). Returns how many.
CS_API int32_t cs_water_changed_tiles(CsWater* w, uint8_t* flags, int32_t all);
// Render/query snapshot of `count` tiles: per tile tile_size² cells × 4 floats in out (display surface, depth,
// velocity x, velocity z) and optionally tile_size² floats in extra (pollutant concentration, kg/m³). Depth is -1 for
// dry cells with no wet neighbour (hidden) and 0 for dry cells next to water (surface is the highest neighbouring
// water surface, so the water mesh reaches into the bank and the ground cuts the shoreline; their concentration is
// that neighbour's). visible[k] = 0 when nothing in tile k shows.
CS_API int32_t cs_water_read_tiles(CsWater* w, const int32_t* tiles, int32_t count, float* out, float* extra, uint8_t* visible);

// The allocated tiles (up to capacity written); returns how many there are.
CS_API int32_t cs_water_list_tiles(CsWater* w, int32_t* out, int32_t capacity);
// Copies a field of tile t (tile_size² floats). Returns 0 when the tile isn't allocated (all zero), 1 when copied.
CS_API int32_t cs_water_get_tile(CsWater* w, int32_t t, int32_t field, float* out);
// Writes a field of tile t (allocating it; null = zeros). Call cs_water_commit after a batch of writes.
CS_API int32_t cs_water_set_tile(CsWater* w, int32_t t, int32_t field, const float* in);
// After cs_water_set_tile / cs_water_raise_tile: clears flows, recounts, wakes wet tiles, redraws everything.
CS_API void cs_water_commit(CsWater* w);
// Removes all water and pollutant (the wet paint stays).
CS_API void cs_water_clear(CsWater* w);
// Raises tile t's water to at least the given surface (tile_size² floats, NaN = leave). Returns 1 if any water was
// added. Call cs_water_commit after a batch.
CS_API int32_t cs_water_raise_tile(CsWater* w, int32_t t, const float* surface);

// Removes the water a lake or river source holds: from the cells within `radius` of (x, z), every 4-connected wet cell
// whose ground is below `level` and whose surface is within 0.3 m of the source cells' surface (at most `level`). Water
// that ran on downhill (a river out of the lake) sits lower and is kept. Returns the number of cells drained; the old
// surfaces stay available (cs_water_list_drained / cs_water_get_drained, NaN = untouched) until the next drain, for
// cs_water_raise_tile to put them back.
CS_API int32_t cs_water_drain(CsWater* w, float x, float z, float radius, float level);
CS_API int32_t cs_water_list_drained(CsWater* w, int32_t* out, int32_t capacity);
CS_API int32_t cs_water_get_drained(CsWater* w, int32_t t, float* out);

// Ground marks for the terrain shader on a grid mark_factor times coarser than the water's (((width - 1) / mark_factor
// + 1) x ((depth - 1) / mark_factor + 1) cells), 2 bytes per cell: distance to water deeper than 25 cm (metres × 4, so
// 0..255 = 0..63.75 m; every metre above the nearest water surface counts as 4 m, like the M5.1 shore mask), and wet
// paint (0..255: how long water has stood or run there; the most in the cell's block). Returns 0 without writing
// when nothing changed since the last read (force = 0).
CS_API int32_t cs_water_read_ground(CsWater* w, uint8_t* out, int32_t force, int32_t mark_factor);

// Instantly floods everything below sea level that connects to the border (sea sources). River and lake sources aren't
// flood-filled: their water runs downhill, so a flat fill at their level would drown everything below them.
CS_API void cs_water_fill_sources(CsWater* w);
