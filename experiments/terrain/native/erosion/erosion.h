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
//   0 shore: distance to lakes, the sea and rivers, in metres, 255 = -16 (inside a river) .. 0 = 64 or more. Ground above
//     the water adds 4 m per metre it stands above it.
//   1 gully: distance to a gully or river bed, 255 = -8 (inside) .. 0 = 24 or more.
//   2 wear: stream power (sqrt(catchment m²) x slope), 32 x log2(1 + v).
//   3 deposit: sediment settling where flow slows (area x slope carried at capacity), 32 x log2(1 + v).
// Flow splits between lower neighbours by slope (multiple flow directions); on flats (lakes, filled pits) it follows the
// Priority-Flood tree out over the spill point.
CS_API int32_t cs_find_water(const float* heights, int32_t width, int32_t depth, float sea_level, float min_depth,
                             int32_t min_cells, const CsGroundParams* ground_params, float* water_level,
                             uint32_t* ground, CsProgress* progress);
