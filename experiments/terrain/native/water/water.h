// CitySim water simulation: shallow water on a heightfield with the "virtual pipes" model (O'Brien & Hodgins 1995,
// Mei, Decaudin & Hu 2007). Each cell holds a water depth and four outflow pipes to its neighbours; water flows by
// the difference in surface height, so it runs downhill, fills hollows, spills over rims and reacts to edits.
// Plain C ABI, called from C# through function pointers (src/Water/WaterNative.cs); structs match field for field.
// A handle owns the state; calls on one handle must not overlap (the C# side runs them on one worker thread).
// Grids are row-major (index z * width + x), in metres. No Godot types.
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
};

typedef struct CsWater CsWater;

// width x depth water cells, cell_size metres apart. Returns null on bad arguments.
CS_API CsWater* cs_water_create(int32_t width, int32_t depth, float cell_size, int32_t threads);
CS_API void cs_water_destroy(CsWater* w);

// Sets the ground under water cells x0..x1, z0..z1 (inclusive, water coordinates) from a terrain heightmap with
// `factor` terrain cells per water cell: each water cell gets the mean of the terrain vertices around it.
CS_API int32_t cs_water_set_ground(CsWater* w, const float* heights, int32_t terrain_width, int32_t terrain_depth,
                                   int32_t factor, int32_t x0, int32_t z0, int32_t x1, int32_t z1);

CS_API void cs_water_set_params(CsWater* w, const CsWaterParams* params);
CS_API int32_t cs_water_set_sources(CsWater* w, const CsWaterSource* sources, int32_t count);

// Advances `dt` seconds in substeps short enough to stay stable (CFL), at most max_substeps of them. One call is one
// tick: the tiles to step are chosen at its start, and tiles whose water has settled for a few ticks go to sleep
// (not stepped) until something nearby changes. The result depends only on the state and dt, never on timing or the
// thread count, so a fixed dt per call makes runs repeatable.
CS_API int32_t cs_water_step(CsWater* w, float dt, int32_t max_substeps, CsWaterStats* stats);

// Render/query snapshot, 4 floats per cell: display surface, depth, velocity x, velocity z. Depth is -1 for dry cells
// with no wet neighbour (surface is then 1 m under the ground, hidden) and 0 for dry cells next to water (surface is
// the highest neighbouring water surface, so the water mesh reaches into the bank and the ground cuts the shoreline).
// With all = 0 only tiles changed since the last read are written; tile_changed (tiles_x * tiles_z bytes) gets 1 for
// each written tile. Returns the number of tiles written. extra (optional, 1 float per cell) gets the pollutant
// concentration in kg/m³ (dry cells next to water copy their wettest neighbour's).
CS_API int32_t cs_water_read(CsWater* w, float* out, float* extra, uint8_t* tile_changed, int32_t all);
CS_API int32_t cs_water_tile_size(void);

// Depth per cell (width * depth floats): read for saving, write for loading or clearing (null = all dry).
CS_API void cs_water_get_depth(CsWater* w, float* depth);
// Setting depths clears the pollutant.
CS_API void cs_water_set_depth(CsWater* w, const float* depth);

// Pollutant mass per cell in kg (width * depth floats): read for saving, write for loading (null = clean).
CS_API void cs_water_get_pollution(CsWater* w, float* mass);
CS_API void cs_water_set_pollution(CsWater* w, const float* mass);

// Raises the water to at least the given surface (NaN = leave), per cell. Used to fill hollows from lake levels.
CS_API void cs_water_raise_to(CsWater* w, const float* surface);

// Removes the water a lake or river source holds: from the cells within `radius` of (x, z), every 4-connected wet cell
// whose ground is below `level` and whose surface is within 0.3 m of the source cells' surface (at most `level`). Water
// that ran on downhill (a river out of the lake) sits lower and is kept. `removed_surface` (optional, width * depth)
// gets the old surface of each drained cell and NaN elsewhere, for cs_water_raise_to to put it back. Returns the
// number of cells drained.
CS_API int32_t cs_water_drain(CsWater* w, float x, float z, float radius, float level, float* removed_surface);

// Ground marks for the terrain shader, 2 bytes per cell: distance to water deeper than 25 cm (metres × 4, so 0..255 = 0..63.75 m; every
// metre above the nearest water surface counts as 4 m, like the M5.1 shore mask), and wet paint (0..255: how long
// water has stood or run there). Returns 0 without writing when nothing changed since the last read (force = 0).
CS_API int32_t cs_water_read_ground(CsWater* w, uint8_t* out, int32_t force);

// Wet paint per cell, 0..1 (width * depth floats): read for saving, write for loading (null = none).
CS_API void cs_water_get_paint(CsWater* w, float* paint);
CS_API void cs_water_set_paint(CsWater* w, const float* paint);

// Instantly floods everything below sea level that connects to the border (sea sources). River and lake sources aren't
// flood-filled: their water runs downhill, so a flat fill at their level would drown everything below them.
CS_API void cs_water_fill_sources(CsWater* w);
