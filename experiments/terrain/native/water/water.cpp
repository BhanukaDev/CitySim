// Shallow-water simulation with the virtual pipes model; see water.h.
//
// Each step: sources add or remove water; every wet cell updates its four outflow pipes from the surface difference
// to its neighbours (scaled down so a cell never sends more than it holds). A pipe's cross-section is the cell width
// times the water depth (the classic model uses cell², which makes thin water accelerate as if it were deep: sheets on
// slopes raced at the speed cap), so water accelerates at g × slope and waves travel at sqrt(g × depth); then every cell's depth changes by inflow
// minus outflow, and its velocity comes from the averaged flows through it. Pipes lose a little flow per second
// (damping) so basins settle instead of sloshing forever, and to bed friction (Manning-like: deceleration
// g n² v² / d^1.5, close to Manning's d^(4/3) but one sqrt instead of a cbrt; applied implicitly so it's stable), so shallow water on steep slopes runs at a few m/s instead of accelerating
// without limit, while deep rivers on gentle slopes still flow.
//
// Only 64² tiles that hold water, border a wet tile or sit under a source are stepped: dry tiles have no flow, so
// skipping them changes nothing, and a mostly dry map costs little. Substeps follow the CFL limit for shallow-water
// waves (dt <= cell / (sqrt(g * depth) + speed)), so deep water takes more, shorter substeps.

#include "water.h"
#include "../common/parallel.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <vector>

namespace {

constexpr int kTile = 64;
/// Water shallower than this counts as dry for drawing and statistics (the sim itself keeps any depth).
constexpr float kWet = 0.01f;
/// Depth used for the CFL limit at least, so nearly dry maps don't take huge substeps.
constexpr float kMinCflDepth = 1.0f;
/// Longest substep in seconds, whatever the CFL limit allows.
constexpr float kMaxSubstep = 0.5f;

struct SourceCell {
    int32_t i;
    float w;
};

struct Source {
    CsWaterSource s;
    std::vector<SourceCell> cells;
    float weight_sum = 0;
};

} // namespace

struct CsWater {
    int w = 0, d = 0;
    float cell = 1;
    int tiles_x = 0, tiles_z = 0;
    std::vector<float> ground, depth, fl, fr, ft, fb, vx, vz;
    std::vector<uint8_t> active, changed, wet, source_tile;
    // Cells under a source: the map edge is closed there, so a border river feeds the map instead of draining off it.
    std::vector<uint8_t> source_cell;
    std::vector<int> list;
    std::vector<double> tile_volume;
    std::vector<float> tile_max_depth, tile_max_speed;
    std::vector<int> tile_wet_cells;
    std::vector<Source> sources;
    std::vector<int> border;  // cells within two of the border, for the sea
    CsWaterParams p{9.81f, 0.2f, 0.0f, 25.0f, 2.0f, 15, 0.03f};
    float max_depth = 0, max_speed = 0;
    int active_tiles = 0;
    std::unique_ptr<cs::ThreadPool> pool;

    int Tile(int x, int z) const { return (z / kTile) * tiles_x + x / kTile; }

    template <class F>
    void ForTile(int t, F&& f) const {
        int x0 = (t % tiles_x) * kTile, z0 = (t / tiles_x) * kTile;
        int x1 = std::min(x0 + kTile, w), z1 = std::min(z0 + kTile, d);
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) f(x, z, z * w + x);
    }

    void MarkRect(int x0, int z0, int x1, int z1) {
        for (int tz = z0 / kTile; tz <= z1 / kTile; tz++)
            for (int tx = x0 / kTile; tx <= x1 / kTile; tx++) {
                int t = tz * tiles_x + tx;
                active[t] = changed[t] = 1;
            }
    }

    /// active = wet tiles and their 8 neighbours.
    void DilateWet() {
        std::fill(active.begin(), active.end(), 0);
        for (int tz = 0; tz < tiles_z; tz++)
            for (int tx = 0; tx < tiles_x; tx++) {
                if (!wet[tz * tiles_x + tx]) continue;
                for (int z = std::max(tz - 1, 0); z <= std::min(tz + 1, tiles_z - 1); z++)
                    for (int x = std::max(tx - 1, 0); x <= std::min(tx + 1, tiles_x - 1); x++) active[z * tiles_x + x] = 1;
            }
    }

    /// After the depths were set from outside: recount wet tiles, clear flows, redraw everything.
    void Reset() {
        std::fill(fl.begin(), fl.end(), 0.f);
        std::fill(fr.begin(), fr.end(), 0.f);
        std::fill(ft.begin(), ft.end(), 0.f);
        std::fill(fb.begin(), fb.end(), 0.f);
        std::fill(vx.begin(), vx.end(), 0.f);
        std::fill(vz.begin(), vz.end(), 0.f);
        for (int t = 0; t < tiles_x * tiles_z; t++) {
            bool any = false;
            ForTile(t, [&](int, int, int i) { any |= depth[i] > 0; });
            wet[t] = any;
        }
        DilateWet();
        std::fill(changed.begin(), changed.end(), 1);
        max_depth = 0;
        for (float v : depth) max_depth = std::max(max_depth, v);
    }

    void BuildSourceCells(Source& src) {
        src.cells.clear();
        src.weight_sum = 0;
        if (src.s.type == CS_WATER_SEA) return;
        float r = std::max(src.s.radius, cell * 0.5f);
        int x0 = std::max(0, (int)std::floor((src.s.x - r) / cell)), x1 = std::min(w - 1, (int)std::ceil((src.s.x + r) / cell));
        int z0 = std::max(0, (int)std::floor((src.s.z - r) / cell)), z1 = std::min(d - 1, (int)std::ceil((src.s.z + r) / cell));
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++) {
                float dx = x * cell - src.s.x, dz = z * cell - src.s.z;
                float q = (dx * dx + dz * dz) / (r * r);
                if (q >= 1) continue;
                float wt = (1 - q) * (1 - q);
                src.cells.push_back({z * w + x, wt});
                src.weight_sum += wt;
            }
        if (src.cells.empty()) {
            // Smaller than a cell, or centred just off the grid: use the nearest cell.
            int x = std::clamp((int)std::lround(src.s.x / cell), 0, w - 1), z = std::clamp((int)std::lround(src.s.z / cell), 0, d - 1);
            src.cells.push_back({z * w + x, 1.f});
            src.weight_sum = 1;
        }
    }

    void ApplySources(float dt) {
        const float area = cell * cell;
        for (auto& src : sources) {
            const auto& s = src.s;
            switch (s.type) {
            case CS_WATER_STREAM: {
                float k = s.rate * dt / (src.weight_sum * area);
                for (auto c : src.cells) depth[c.i] = std::max(0.f, depth[c.i] + k * c.w);
                break;
            }
            case CS_WATER_LEVEL:
                for (auto c : src.cells) {
                    float target = std::max(s.level - ground[c.i], 0.f);
                    depth[c.i] += (target - depth[c.i]) * std::min(1.f, p.level_rate * dt * c.w);
                }
                break;
            case CS_WATER_LAKE: {
                double need = 0;
                for (auto c : src.cells) {
                    float missing = s.level - ground[c.i] - depth[c.i];
                    if (missing > 0) need += missing * std::min(1.f, p.level_rate * dt * c.w);
                }
                if (need <= 0) break;
                double allowed = s.max_rate * dt / area;
                float scale = (float)std::min(1.0, allowed / need);
                for (auto c : src.cells) {
                    float missing = s.level - ground[c.i] - depth[c.i];
                    if (missing > 0) depth[c.i] += missing * std::min(1.f, p.level_rate * dt * c.w) * scale;
                }
                break;
            }
            case CS_WATER_SEA:
                for (int i : border)
                    if (ground[i] < s.level) depth[i] = s.level - ground[i];
                break;
            }
        }
    }

    void Flux(int t, float dt) {
        const float k = dt * p.gravity, damp0 = std::max(0.f, 1.f - p.damping * dt);
        const float area = cell * cell, fric = dt * p.gravity * p.manning * p.manning;
        const int open = p.open_edges;
        ForTile(t, [&](int x, int z, int i) {
            float di = depth[i];
            if (di <= 0) {
                fl[i] = fr[i] = ft[i] = fb[i] = 0;
                return;
            }
            float h = ground[i] + di;
            const int edges = source_cell[i] ? 0 : open;
            float speed = std::sqrt(vx[i] * vx[i] + vz[i] * vz[i]);
            float hd = std::max(di, 0.02f);
            float damp = speed > 0 ? damp0 / (1.f + fric * speed / (hd * std::sqrt(hd))) : damp0;
            // Outside the map, an open edge acts like dry ground at this cell's height.
            auto pipe = [&](float f, bool inside, int n, int bit) {
                float dh;
                if (inside) dh = h - ground[n] - depth[n];
                else if (edges & bit) dh = di;
                else return 0.f;
                return std::max(0.f, f * damp + k * di * dh);
            };
            float l = pipe(fl[i], x > 0, i - 1, 1);
            float r = pipe(fr[i], x < w - 1, i + 1, 2);
            float tp = pipe(ft[i], z > 0, i - w, 4);
            float b = pipe(fb[i], z < d - 1, i + w, 8);
            float sum = l + r + tp + b;
            if (sum > 0) {
                float s = std::min(1.f, di * area / (sum * dt));
                l *= s; r *= s; tp *= s; b *= s;
            }
            fl[i] = l; fr[i] = r; ft[i] = tp; fb[i] = b;
        });
    }

    void Depth(int t, float dt) {
        const float area = cell * cell, evap = p.evaporation * dt, vmax = p.max_speed;
        double vol = 0;
        float md = 0, mv = 0;
        int wetCells = 0;
        bool any = false;
        ForTile(t, [&](int x, int z, int i) {
            float inL = x > 0 ? fr[i - 1] : 0.f, inR = x < w - 1 ? fl[i + 1] : 0.f;
            float inT = z > 0 ? fb[i - w] : 0.f, inB = z < d - 1 ? ft[i + w] : 0.f;
            float out = fl[i] + fr[i] + ft[i] + fb[i];
            float d0 = depth[i];
            float d1 = d0 + dt * (inL + inR + inT + inB - out) / area;
            d1 = std::max(0.f, d1 - evap);
            float avg = 0.5f * (d0 + d1);
            float u = 0, v = 0;
            // Films thinner than kWet get no velocity: flow / a tiny depth spikes, and the CFL limit would follow it.
            if (avg > kWet) {
                u = 0.5f * (inL - fl[i] + fr[i] - inR) / (cell * avg);
                v = 0.5f * (inT - ft[i] + fb[i] - inB) / (cell * avg);
                // Flow passing through a thin cell from a deeper one reads as a huge speed; cap at Froude 3 (fast,
                // supercritical flow on steep slopes stays possible).
                float cap = std::min(vmax, 3.f * std::sqrt(p.gravity * avg));
                float s = std::sqrt(u * u + v * v);
                if (s > cap) { u *= cap / s; v *= cap / s; s = cap; }
                mv = std::max(mv, s);
            }
            depth[i] = d1;
            vx[i] = u;
            vz[i] = v;
            if (d1 > 0) {
                any = true;
                vol += d1;
                md = std::max(md, d1);
                if (d1 > kWet) wetCells++;
            }
        });
        tile_volume[t] = vol * area;
        tile_max_depth[t] = md;
        tile_max_speed[t] = mv;
        tile_wet_cells[t] = wetCells;
        wet[t] = any;
    }

    void Substep(float dt) {
        int tiles = tiles_x * tiles_z;
        list.clear();
        for (int t = 0; t < tiles; t++)
            if (active[t] || source_tile[t]) list.push_back(t);
        ApplySources(dt);
        pool->Run((int)list.size(), [&](int j) { Flux(list[j], dt); });
        pool->Run((int)list.size(), [&](int j) { Depth(list[j], dt); });
        max_depth = max_speed = 0;
        for (int t : list) {
            max_depth = std::max(max_depth, tile_max_depth[t]);
            max_speed = std::max(max_speed, tile_max_speed[t]);
            changed[t] = 1;
        }
        active_tiles = (int)list.size();
        DilateWet();
    }
};

CS_API CsWater* cs_water_create(int32_t width, int32_t depth, float cell_size, int32_t threads) {
    if (width < 2 || depth < 2 || !(cell_size > 0) || (int64_t)width * depth > (int64_t)8193 * 8193) return nullptr;
    auto* w = new CsWater();
    w->w = width;
    w->d = depth;
    w->cell = cell_size;
    w->tiles_x = (width + kTile - 1) / kTile;
    w->tiles_z = (depth + kTile - 1) / kTile;
    size_t n = (size_t)width * depth, tiles = (size_t)w->tiles_x * w->tiles_z;
    for (auto* v : {&w->ground, &w->depth, &w->fl, &w->fr, &w->ft, &w->fb, &w->vx, &w->vz}) v->assign(n, 0.f);
    for (auto* v : {&w->active, &w->changed, &w->wet, &w->source_tile}) v->assign(tiles, 0);
    w->source_cell.assign(n, 0);
    std::fill(w->changed.begin(), w->changed.end(), 1);
    w->tile_volume.assign(tiles, 0);
    w->tile_max_depth.assign(tiles, 0);
    w->tile_max_speed.assign(tiles, 0);
    w->tile_wet_cells.assign(tiles, 0);
    for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
            if (x < 2 || z < 2 || x >= width - 2 || z >= depth - 2) w->border.push_back(z * width + x);
    w->pool = std::make_unique<cs::ThreadPool>(threads > 0 ? threads : cs::ThreadPool::DefaultThreads());
    return w;
}

CS_API void cs_water_destroy(CsWater* w) { delete w; }

CS_API int32_t cs_water_set_ground(CsWater* w, const float* heights, int32_t tw, int32_t td, int32_t factor,
                                   int32_t x0, int32_t z0, int32_t x1, int32_t z1) {
    if (!w || !heights || factor < 1 || (tw - 1) != (w->w - 1) * factor || (td - 1) != (w->d - 1) * factor) return -1;
    x0 = std::max(x0, 0); z0 = std::max(z0, 0);
    x1 = std::min(x1, w->w - 1); z1 = std::min(z1, w->d - 1);
    if (x0 > x1 || z0 > z1) return 0;
    int half = factor / 2;
    w->pool->Run(z1 - z0 + 1, [&](int j) {
        int z = z0 + j;
        for (int x = x0; x <= x1; x++) {
            if (factor == 1) {
                w->ground[z * w->w + x] = heights[(size_t)z * tw + x];
                continue;
            }
            int cx = x * factor, cz = z * factor;
            double sum = 0;
            int n = 0;
            for (int hz = std::max(cz - half, 0); hz <= std::min(cz + half, td - 1); hz++)
                for (int hx = std::max(cx - half, 0); hx <= std::min(cx + half, tw - 1); hx++) {
                    sum += heights[(size_t)hz * tw + hx];
                    n++;
                }
            w->ground[z * w->w + x] = (float)(sum / n);
        }
    });
    w->MarkRect(x0, z0, x1, z1);
    return 0;
}

CS_API void cs_water_set_params(CsWater* w, const CsWaterParams* params) {
    if (w && params) w->p = *params;
}

CS_API int32_t cs_water_set_sources(CsWater* w, const CsWaterSource* sources, int32_t count) {
    if (!w || count < 0 || (count > 0 && !sources)) return -1;
    w->sources.clear();
    std::fill(w->source_tile.begin(), w->source_tile.end(), 0);
    std::fill(w->source_cell.begin(), w->source_cell.end(), 0);
    for (int k = 0; k < count; k++) {
        Source src;
        src.s = sources[k];
        w->BuildSourceCells(src);
        if (src.s.type == CS_WATER_SEA)
            for (int i : w->border) {
                w->source_tile[w->Tile(i % w->w, i / w->w)] = 1;
                w->source_cell[i] = 1;
            }
        for (auto c : src.cells) {
            w->source_tile[w->Tile(c.i % w->w, c.i / w->w)] = 1;
            w->source_cell[c.i] = 1;
        }
        w->sources.push_back(std::move(src));
    }
    return 0;
}

CS_API int32_t cs_water_step(CsWater* w, float dt, int32_t max_substeps, CsWaterStats* stats) {
    if (!w || !(dt >= 0)) return -1;
    float remaining = dt;
    int n = 0;
    while (remaining > 1e-5f && n < std::max(max_substeps, 1)) {
        float c = std::sqrt(w->p.gravity * std::max(w->max_depth, kMinCflDepth)) + w->max_speed;
        float h = std::min({remaining, 0.5f * w->cell / c, kMaxSubstep});
        w->Substep(h);
        remaining -= h;
        n++;
    }
    if (stats) {
        double vol = 0;
        int wetCells = 0;
        for (int t = 0; t < w->tiles_x * w->tiles_z; t++)
            if (w->wet[t]) {
                vol += w->tile_volume[t];
                wetCells += w->tile_wet_cells[t];
            }
        stats->volume = vol;
        stats->max_depth = w->max_depth;
        stats->max_speed = w->max_speed;
        stats->wet_cells = wetCells;
        stats->active_tiles = w->active_tiles;
        stats->substeps = n;
        stats->simulated = dt - std::max(remaining, 0.f);
    }
    return 0;
}

CS_API int32_t cs_water_tile_size(void) { return kTile; }

CS_API int32_t cs_water_read(CsWater* w, float* out, uint8_t* tile_changed, int32_t all) {
    if (!w || !out) return -1;
    int tiles = w->tiles_x * w->tiles_z;
    std::vector<int> todo;
    for (int t = 0; t < tiles; t++) {
        bool take = all || w->changed[t];
        if (tile_changed) tile_changed[t] = take;
        if (take) todo.push_back(t);
        w->changed[t] = 0;
    }
    const int W = w->w, D = w->d;
    w->pool->Run((int)todo.size(), [&](int j) {
        w->ForTile(todo[j], [&](int x, int z, int i) {
            float* o = out + (size_t)i * 4;
            float g = w->ground[i], dep = w->depth[i];
            if (dep > kWet) {
                o[0] = g + dep; o[1] = dep; o[2] = w->vx[i]; o[3] = w->vz[i];
                return;
            }
            float best = -std::numeric_limits<float>::infinity();
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = x + dx, nz = z + dz;
                    if ((dx | dz) == 0 || nx < 0 || nz < 0 || nx >= W || nz >= D) continue;
                    int n = nz * W + nx;
                    if (w->depth[n] > kWet) best = std::max(best, w->ground[n] + w->depth[n]);
                }
            if (best > -std::numeric_limits<float>::infinity()) {
                o[0] = std::min(best, g); o[1] = 0; o[2] = o[3] = 0;
            } else {
                o[0] = g - 1; o[1] = -1; o[2] = o[3] = 0;
            }
        });
    });
    return (int32_t)todo.size();
}

CS_API void cs_water_get_depth(CsWater* w, float* depth) {
    if (w && depth) std::copy(w->depth.begin(), w->depth.end(), depth);
}

CS_API void cs_water_set_depth(CsWater* w, const float* depth) {
    if (!w) return;
    if (depth) for (size_t i = 0; i < w->depth.size(); i++) w->depth[i] = std::isfinite(depth[i]) ? std::max(depth[i], 0.f) : 0.f;
    else std::fill(w->depth.begin(), w->depth.end(), 0.f);
    w->Reset();
}

CS_API void cs_water_raise_to(CsWater* w, const float* surface) {
    if (!w || !surface) return;
    for (size_t i = 0; i < w->depth.size(); i++)
        if (std::isfinite(surface[i])) w->depth[i] = std::max(w->depth[i], surface[i] - w->ground[i]);
    w->Reset();
}

CS_API void cs_water_fill_sources(CsWater* w) {
    if (!w) return;
    const int W = w->w, D = w->d;
    std::vector<uint8_t> seen(w->depth.size());
    std::vector<int> stack;
    for (auto& src : w->sources) {
        if (src.s.type != CS_WATER_SEA) continue;
        float level = src.s.level;
        std::fill(seen.begin(), seen.end(), 0);
        stack.clear();
        auto push = [&](int i) {
            if (seen[i] || w->ground[i] >= level) return;
            seen[i] = 1;
            stack.push_back(i);
        };
        for (int i : w->border) push(i);
        while (!stack.empty()) {
            int i = stack.back();
            stack.pop_back();
            w->depth[i] = std::max(w->depth[i], level - w->ground[i]);
            int x = i % W, z = i / W;
            if (x > 0) push(i - 1);
            if (x < W - 1) push(i + 1);
            if (z > 0) push(i - W);
            if (z < D - 1) push(i + W);
        }
    }
    w->Reset();
}
