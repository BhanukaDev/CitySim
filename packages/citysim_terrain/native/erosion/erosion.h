// CitySim terrain erosion and lake finding. Plain C ABI, called from C# with [LibraryImport]
// (see src/Terrain/Erosion/Native.cs); the structs must match the C# ones field for field.
// Heights are row-major floats in metres (index z * width + x), edited in place. No Godot types.
#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define CS_API extern "C" __declspec(dllexport)
#else
#define CS_API extern "C" __attribute__((visibility("default")))
#endif

struct CsErosionParams {
    int32_t seed;
    float droplets_per_cell;   // droplets simulated per map cell over the whole run
    int32_t max_lifetime;      // steps per droplet (each step moves one cell)
    float inertia;             // 0 = follows the slope exactly, 1 = keeps its direction
    float capacity;            // sediment a droplet can carry per unit of speed, water and slope
    float min_slope;           // slope used for capacity on flat ground (per cell, in cell units)
    float erode_speed;         // share of the missing capacity taken from the ground per step
    float deposit_speed;       // share of the excess sediment dropped per step
    float evaporate_speed;     // share of water lost per step
    float gravity;
    float erosion_radius;      // cells; erosion is spread over this radius
    float max_erode_depth;     // metres a single step may take from one cell (stops pits on steep ground)
    int32_t thermal_iterations;
    float talus_degrees;       // slope above which thermal erosion moves material downhill
    float thermal_rate;        // 0..1, share of the excess moved per iteration
    float breach_depth;        // metres: hollows that need a cut no deeper than this get an outlet channel (0 = off)
    float sea_level;           // ground below this that connects to the border is sea (-1e30 = no sea)
};

struct CsGroundParams {
    float cell_size;           // metres
    float river_min_area;      // m² of catchment above which flowing water counts as a river (for the shore mask)
    float gully_min_area;      // m² of catchment above which flow marks a gully bed
};

struct CsProgress {
    float progress;            // 0..1, written by the library
    int32_t cancel;            // set to non-zero by the caller to stop early
};

// Returns 0 on success, 1 if cancelled, negative on bad arguments.
CS_API int32_t cs_erode(float* heights, int32_t width, int32_t depth, float cell_size,
                        const CsErosionParams* params, CsProgress* progress);

// Priority-Flood (Barnes et al. 2014) from the map border and from the sea (ground below sea_level connected to the
// border; pass -1e30 for no sea). Writes each lake cell's water surface height to water_level and NaN to dry cells.
// Lakes shallower than min_depth metres or smaller than min_cells are dropped. Returns the number of lakes kept,
// or negative on bad arguments.
CS_API int32_t cs_find_lakes(const float* heights, int32_t width, int32_t depth, float sea_level,
                             float min_depth, int32_t min_cells, float* water_level, CsProgress* progress);

// cs_find_lakes, plus ground masks for texturing, one packed value per cell (bytes, lowest first):
//   0 shore: distance to rivers (lakes and the sea come from the water sim), in metres, 255 = -16 (inside a river) .. 0 = 64 or more. Ground above
//     the water adds 4 m per metre it stands above it.
//   1 gully: distance to a gully or river bed, 255 = -8 (inside) .. 0 = 24 or more.
//   2 wear: stream power (sqrt(catchment m²) x slope), 32 x log2(1 + v).
//   3 deposit: sediment settling where flow slows (area x slope carried at capacity), 32 x log2(1 + v).
// Flow splits between lower neighbours by slope (multiple flow directions); on flats (lakes, filled pits) it follows the
// Priority-Flood tree out over the spill point.
// Milliseconds the last cs_find_lakes/cs_find_water call spent in each stage: flood, lakes, flow, masks (0 when a stage
// didn't run). For profiling; not thread-safe across concurrent calls.
CS_API void cs_last_find_water_ms(float* out4);

// flow_state (optional, one per cell): what cs_find_water_window needs from this search, kept by the caller: the
// catchment area (16-bit log2 x 2048, bits 0-15) and the gully bit (bit 16).
CS_API int32_t cs_find_water(const float* heights, int32_t width, int32_t depth, float sea_level, float min_depth,
                             int32_t min_cells, const CsGroundParams* ground_params, float* water_level,
                             uint32_t* ground, uint32_t* flow_state, CsProgress* progress);

// cs_find_water on the window [x0..x1] x [z0..z1] (inclusive) only, after an edit inside it. level and flow are the
// whole map's water_level and flow_state from the last search, read on and just outside the window's border: lakes
// crossing it keep their level and rivers flowing in keep their catchment. water_out, ground_out and flow_out are
// window-sized ((x1 - x0 + 1) x (z1 - z0 + 1)). Changes don't reach cells downstream of the window, and a lake cut by
// its border is kept or dropped on the part inside, so the caller runs a full search later. Returns the number of
// lakes in the window, or negative on bad arguments.
CS_API int32_t cs_find_water_window(const float* heights, int32_t width, int32_t depth, int32_t x0, int32_t z0,
                                    int32_t x1, int32_t z1, float sea_level, float min_depth, int32_t min_cells,
                                    const CsGroundParams* ground_params, const float* level, const uint32_t* flow,
                                    float* water_out, uint32_t* ground_out, uint32_t* flow_out, CsProgress* progress);
