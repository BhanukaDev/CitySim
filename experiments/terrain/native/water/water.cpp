// Shallow-water simulation with the virtual pipes model; see water.h.
//
// Each step: sources add or remove water; every wet cell updates its four outflow pipes from the surface difference
// to its neighbours (scaled down so a cell never sends more than it holds). A pipe's cross-section is the cell width
// times the water depth (the classic model uses cell², which makes thin water accelerate as if it were deep: sheets on
// slopes raced at the speed cap), so water accelerates at g × slope and waves travel at sqrt(g × depth); then every cell's depth changes by inflow
// minus outflow, and its velocity comes from the averaged flows through it. Pipes lose a little flow per second
// (damping) so basins settle instead of sloshing forever, and to bed friction (Manning-like: deceleration
// g n² v² / d^1.5, close to Manning's d^(4/3) but one sqrt instead of a cbrt; applied implicitly so it's stable), so shallow water on steep slopes runs at a few m/s instead of accelerating
// without limit, while deep rivers on gentle slopes still flow. Shallow water gets a higher n (kSheetManning), which
// keeps thin streams below the Froude number where they'd break into roll waves.
//
// Work is done per 64² tile. A call to cs_water_step is one tick: at its start the tiles to step are chosen (awake
// tiles, stream tiles, and a ring of one tile around them); inside it, substeps follow the CFL limit for
// shallow-water waves (dt <= cell / (sqrt(g * depth) + speed)), split evenly over what's left. At its end, a tile
// whose depth barely changed and whose water barely moves for a few ticks falls asleep: its flows are zeroed and it
// isn't stepped, and a stepped tile's pipes into it are closed, so no water crosses into a tile that isn't stepped.
// It wakes when a ground edit or a source touches it or when it's in the ring and its water changes. Evaporation
// and pollutant decay on sleeping tiles are caught up in one go when they wake (or every kSleepFlush seconds).
//
// A pollutant is carried as mass per cell and moves with the same pipe flows as the water (each pipe carries its
// cell's concentration), so its mass is conserved up to decay, open edges, and cells drying out.
//
// Wet paint: every cell remembers how long water has stood or run on it (0..1, reached after 1 / paint_rate seconds),
// and forgets it slowly once dry. Dry cells next to water paint at half the rate, so the mark reaches the waterline.
// Tiles that aren't stepped are painted in one go every kSleepFlush seconds (their water doesn't change meanwhile).
// cs_water_read_ground turns it, with the distance to the water, into the bytes the terrain shader textures with.
//
// Determinism: flows are computed from the previous phase's arrays only, and every tile writes only its own cells,
// so results don't depend on the thread count or scheduling; totals are summed in tile order.

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
/// A tile is calm for a tick when its depth changed less than this (m, summed over substeps, evaporation not
/// counted) and no cell carries more than kCalmFlow (m²/s: depth × speed, so a steady river stays awake but a thin
/// film still trickling down a slope doesn't); after kCalmTicks calm ticks it sleeps.
constexpr float kCalmDepth = 0.0005f;
constexpr float kCalmFlow = 0.002f;
constexpr int kCalmTicks = 4;
/// A source that changes a sleeping cell by more than this (m) wakes its tile.
constexpr float kWakeDepth = 0.0001f;
/// Sleeping tiles catch up on evaporation and decay at least this often (simulated seconds), so they show it.
constexpr float kSleepFlush = 60.0f;
/// Water at least this deep paints the ground (thinner films on slopes don't).
constexpr float kPaintDepth = 0.05f;
/// Water at least this deep counts for the ground distance (shores): lakes, the sea and rivers, not thin streams.
constexpr float kShoreDepth = 0.25f;
/// Shallow water is rougher than p.manning: grass and bed texture are large next to a few cm of water, as with
/// overland/sheet flow in hydrology (n ~0.1-0.4 there). Without it thin streams on slopes run above Froude ~1.5, where
/// Manning-friction flow is unstable and breaks into roll waves (a staircase of pools and bores). Full at
/// kSheetFullDepth, fading to p.manning by kSheetFadeDepth.
constexpr float kSheetManning = 0.15f;
constexpr float kSheetFullDepth = 0.05f;
constexpr float kSheetFadeDepth = 0.3f;

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
    std::vector<float> ground, depth, fl, fr, ft, fb, vx, vz, pol, conc, paint;
    // Per tile: awake (wants stepping), stepped this tick, calm tick count, changed since the last read, holds water,
    // holds a stream source (always awake).
    std::vector<uint8_t> awake, stepped, calm, changed, wet, stream_tile;
    // Cells under a source: the map edge is closed there, so a border river feeds the map instead of draining off it.
    std::vector<uint8_t> source_cell;
    std::vector<int> list;
    std::vector<double> tile_volume, tile_pol;
    std::vector<float> tile_max_depth, tile_max_speed, tile_max_flow, tile_change, slept, paint_time;
    // Per tile: holds any wet paint.
    std::vector<uint8_t> painted;
    // The water or the paint changed since the last cs_water_read_ground.
    bool ground_dirty = true;
    std::vector<int> tile_wet_cells, tile_clamp;
    std::vector<Source> sources;
    std::vector<int> border;  // cells within two of the border, for the sea
    CsWaterParams p{9.81f, 0.2f, 0.0f, 25.0f, 2.0f, 15, 0.03f, 0.0f, 0.0f, 0.0f};
    float max_depth = 0, max_speed = 0;
    /// False while there's no pollutant anywhere and no source adds any: the pollutant passes are skipped.
    bool carry = false;
    std::unique_ptr<cs::ThreadPool> pool;

    int Tile(int x, int z) const { return (z / kTile) * tiles_x + x / kTile; }
    int TileOf(int i) const { return Tile(i % w, i / w); }

    template <class F>
    void ForTile(int t, F&& f) const {
        int x0 = (t % tiles_x) * kTile, z0 = (t / tiles_x) * kTile;
        int x1 = std::min(x0 + kTile, w), z1 = std::min(z0 + kTile, d);
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) f(x, z, z * w + x);
    }

    void Wake(int t) {
        awake[t] = changed[t] = 1;
        calm[t] = 0;
    }

    void MarkRect(int x0, int z0, int x1, int z1) {
        for (int tz = z0 / kTile; tz <= z1 / kTile; tz++)
            for (int tx = x0 / kTile; tx <= x1 / kTile; tx++) Wake(tz * tiles_x + tx);
    }

    /// Recomputes a tile's totals from its cells (after changes made outside a substep).
    void RecountTile(int t) {
        const float area = cell * cell;
        double vol = 0, mass = 0;
        float md = 0, mv = 0;
        int wetCells = 0;
        bool any = false;
        ForTile(t, [&](int, int, int i) {
            float di = depth[i];
            if (di <= 0) return;
            any = true;
            vol += di;
            mass += pol[i];
            md = std::max(md, di);
            mv = std::max(mv, std::sqrt(vx[i] * vx[i] + vz[i] * vz[i]));
            if (di > kWet) wetCells++;
        });
        tile_volume[t] = vol * area;
        tile_pol[t] = mass;
        tile_max_depth[t] = md;
        tile_max_speed[t] = mv;
        tile_wet_cells[t] = wetCells;
        wet[t] = any;
    }

    void ZeroFlows(int t) {
        ForTile(t, [&](int, int, int i) { fl[i] = fr[i] = ft[i] = fb[i] = vx[i] = vz[i] = 0.f; });
        tile_max_speed[t] = 0;
    }

    /// After the depths were set from outside: clear flows, recount, wake every wet tile, redraw everything.
    void Reset() {
        for (auto* v : {&fl, &fr, &ft, &fb, &vx, &vz}) std::fill(v->begin(), v->end(), 0.f);
        for (size_t i = 0; i < depth.size(); i++)
            if (depth[i] <= 0) pol[i] = 0;
        for (int t = 0; t < tiles_x * tiles_z; t++) {
            RecountTile(t);
            awake[t] = wet[t];
            calm[t] = 0;
            slept[t] = 0;
        }
        std::fill(changed.begin(), changed.end(), 1);
        ground_dirty = true;
    }

    /// Paints (or fades) a tile's cells for `time` seconds of their current water.
    void Paint(int t, float time) {
        const float gain = p.paint_rate * time, fade = p.paint_fade * time;
        bool any = false, moved = false;
        ForTile(t, [&](int x, int z, int i) {
            float v = paint[i], next = v;
            if (depth[i] > kPaintDepth) next = std::min(1.f, v + gain);
            else if ((x > 0 && depth[i - 1] > kPaintDepth) || (x < w - 1 && depth[i + 1] > kPaintDepth)
                     || (z > 0 && depth[i - w] > kPaintDepth) || (z < d - 1 && depth[i + w] > kPaintDepth))
                next = std::min(1.f, v + gain * 0.5f);
            else if (v > 0) next = std::max(0.f, v - fade);
            if (next != v) { paint[i] = next; moved = true; }
            any |= next > 0;
        });
        painted[t] = any;
        if (moved) ground_dirty = true;
    }

    /// After a tick: paints stepped tiles now, and the others (wet, painted, or next to water) every kSleepFlush.
    void PaintTick(float dt) {
        if (p.paint_rate <= 0 && p.paint_fade <= 0) return;
        std::vector<std::pair<int, float>> todo;
        for (int tz = 0; tz < tiles_z; tz++)
            for (int tx = 0; tx < tiles_x; tx++) {
                int t = tz * tiles_x + tx;
                bool near = wet[t] || painted[t] || (tx > 0 && wet[t - 1]) || (tx < tiles_x - 1 && wet[t + 1])
                         || (tz > 0 && wet[t - tiles_x]) || (tz < tiles_z - 1 && wet[t + tiles_x]);
                if (!near) { paint_time[t] = 0; continue; }
                paint_time[t] += dt;
                if (stepped[t] || paint_time[t] >= kSleepFlush) {
                    todo.push_back({t, paint_time[t]});
                    paint_time[t] = 0;
                }
            }
        pool->Run((int)todo.size(), [&](int j) { Paint(todo[j].first, todo[j].second); });
    }

    /// Catches a sleeping tile up on the evaporation and decay it skipped.
    void CatchUp(int t) {
        float time = slept[t];
        slept[t] = 0;
        if (time <= 0) return;
        const float evap = p.evaporation * time, keep = std::exp(-p.pollution_decay * time);
        ForTile(t, [&](int, int, int i) {
            if (depth[i] <= 0) return;
            depth[i] = std::max(0.f, depth[i] - evap);
            pol[i] = depth[i] > 0 ? pol[i] * keep : 0.f;
        });
        RecountTile(t);
        changed[t] = 1;
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

    /// Sets a cell's depth from a source. Water taken away takes its share of pollutant; a sleeping tile that changes
    /// noticeably wakes (it's stepped from the next tick).
    void SetFromSource(int i, float next) {
        float prev = depth[i];
        next = std::max(next, 0.f);
        if (next < prev) pol[i] = next > 0 ? pol[i] * (next / prev) : 0.f;
        depth[i] = next;
        int t = TileOf(i);
        if (!stepped[t] && std::fabs(next - prev) > kWakeDepth) Wake(t);
    }

    void ApplySources(float dt) {
        const float area = cell * cell;
        for (auto& src : sources) {
            const auto& s = src.s;
            switch (s.type) {
            case CS_WATER_STREAM: {
                float k = s.rate * dt / (src.weight_sum * area);
                float m = s.pollution * dt / src.weight_sum;
                for (auto c : src.cells) {
                    SetFromSource(c.i, depth[c.i] + k * c.w);
                    if (depth[c.i] > 0) pol[c.i] += m * c.w;
                }
                break;
            }
            case CS_WATER_LEVEL:
                for (auto c : src.cells) {
                    float target = std::max(s.level - ground[c.i], 0.f);
                    SetFromSource(c.i, depth[c.i] + (target - depth[c.i]) * std::min(1.f, p.level_rate * dt * c.w));
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
                    if (missing > 0) SetFromSource(c.i, depth[c.i] + missing * std::min(1.f, p.level_rate * dt * c.w) * scale);
                }
                break;
            }
            case CS_WATER_SEA:
                for (int i : border)
                    if (ground[i] < s.level) SetFromSource(i, s.level - ground[i]);
                break;
            }
        }
    }

    void Flux(int t, float dt) {
        const float k = dt * p.gravity, damp0 = std::max(0.f, 1.f - p.damping * dt);
        const float area = cell * cell, fric = dt * p.gravity * p.manning * p.manning;
        const float fricSheet = dt * p.gravity * std::max(0.f, kSheetManning * kSheetManning - p.manning * p.manning);
        const int open = p.open_edges;
        const int tx = t % tiles_x, tz = t / tiles_x;
        const int x0 = tx * kTile, z0 = tz * kTile;
        // Neighbouring tiles that aren't stepped this tick are walls (see the top of the file).
        const bool sl = tx > 0 && stepped[t - 1], sr = tx < tiles_x - 1 && stepped[t + 1];
        const bool st = tz > 0 && stepped[t - tiles_x], sb = tz < tiles_z - 1 && stepped[t + tiles_x];
        ForTile(t, [&](int x, int z, int i) {
            float di = depth[i];
            if (di <= 0) {
                fl[i] = fr[i] = ft[i] = fb[i] = 0;
                conc[i] = 0;
                return;
            }
            if (carry) conc[i] = pol[i] / (di * area);
            float h = ground[i] + di;
            const int edges = source_cell[i] ? 0 : open;
            float speed = std::sqrt(vx[i] * vx[i] + vz[i] * vz[i]);
            float hd = std::max(di, 0.02f);
            float sheet = std::clamp((kSheetFadeDepth - di) / (kSheetFadeDepth - kSheetFullDepth), 0.f, 1.f);
            float fk = p.manning > 0 ? fric + fricSheet * sheet * sheet * (3.f - 2.f * sheet) : 0.f;
            float damp = speed > 0 ? damp0 / (1.f + fk * speed / (hd * std::sqrt(hd))) : damp0;
            // Outside the map, an open edge acts like dry ground at this cell's height.
            auto pipe = [&](float f, bool inside, bool simulated, int n, int bit) {
                float dh;
                if (inside) {
                    if (!simulated) return 0.f;
                    dh = h - ground[n] - depth[n];
                } else if (edges & bit) dh = di;
                else return 0.f;
                return std::max(0.f, f * damp + k * di * dh);
            };
            float l = pipe(fl[i], x > 0, x > x0 || sl, i - 1, 1);
            float r = pipe(fr[i], x < w - 1, x < x0 + kTile - 1 || sr, i + 1, 2);
            float tp = pipe(ft[i], z > 0, z > z0 || st, i - w, 4);
            float b = pipe(fb[i], z < d - 1, z < z0 + kTile - 1 || sb, i + w, 8);
            float sum = l + r + tp + b;
            if (sum > 0) {
                // Never send out more than the cell holds.
                float s = std::min(1.f, di * area / (sum * dt));
                l *= s; r *= s; tp *= s; b *= s;
            }
            fl[i] = l; fr[i] = r; ft[i] = tp; fb[i] = b;
        });
    }

    void Depth(int t, float dt) {
        const float area = cell * cell, evap = p.evaporation * dt, vmax = p.max_speed;
        const float keep = std::exp(-p.pollution_decay * dt);
        double vol = 0, mass = 0;
        float md = 0, mv = 0, mq = 0, change = 0;
        int wetCells = 0, clamps = 0;
        bool any = false;
        ForTile(t, [&](int x, int z, int i) {
            // Pipes from cells in tiles that aren't stepped are zero (their flows were cleared when they slept).
            float inL = x > 0 ? fr[i - 1] : 0.f, inR = x < w - 1 ? fl[i + 1] : 0.f;
            float inT = z > 0 ? fb[i - w] : 0.f, inB = z < d - 1 ? ft[i + w] : 0.f;
            float out = fl[i] + fr[i] + ft[i] + fb[i];
            float d0 = depth[i];
            float d1 = d0 + dt * (inL + inR + inT + inB - out) / area;
            if (d1 < -1e-5f * std::max(1.f, d0)) clamps++;
            change = std::max(change, std::fabs(d1 - d0));
            d1 = std::max(0.f, d1 - evap);
            // Pollutant rides the same pipes, at the concentration of the cell it leaves.
            float m = pol[i];
            if (carry) {
                float mIn = inL * (x > 0 ? conc[i - 1] : 0.f) + inR * (x < w - 1 ? conc[i + 1] : 0.f)
                          + inT * (z > 0 ? conc[i - w] : 0.f) + inB * (z < d - 1 ? conc[i + w] : 0.f);
                m = d1 > 0 ? std::max(0.f, (m + dt * (mIn - out * conc[i])) * keep) : 0.f;
                pol[i] = m;
            }
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
                mq = std::max(mq, s * avg);
            }
            depth[i] = d1;
            vx[i] = u;
            vz[i] = v;
            if (d1 > 0) {
                any = true;
                vol += d1;
                mass += m;
                md = std::max(md, d1);
                if (d1 > kWet) wetCells++;
            }
        });
        tile_volume[t] = vol * area;
        tile_pol[t] = mass;
        tile_max_depth[t] = md;
        tile_max_speed[t] = mv;
        tile_max_flow[t] = mq;
        tile_wet_cells[t] = wetCells;
        tile_change[t] += change;
        tile_clamp[t] += clamps;
        wet[t] = any;
    }

    /// Chooses the tiles to step this tick: awake and stream tiles plus a ring of one around them.
    void BeginTick() {
        const int tiles = tiles_x * tiles_z;
        std::fill(stepped.begin(), stepped.end(), 0);
        for (int tz = 0; tz < tiles_z; tz++)
            for (int tx = 0; tx < tiles_x; tx++) {
                int t = tz * tiles_x + tx;
                if (!awake[t] && !stream_tile[t]) continue;
                for (int z = std::max(tz - 1, 0); z <= std::min(tz + 1, tiles_z - 1); z++)
                    for (int x = std::max(tx - 1, 0); x <= std::min(tx + 1, tiles_x - 1); x++) stepped[z * tiles_x + x] = 1;
            }
        double mass = 0;
        for (int t = 0; t < tiles; t++) mass += tile_pol[t];
        carry = mass > 0;
        for (auto& src : sources) carry |= src.s.type == CS_WATER_STREAM && src.s.pollution > 0;
        list.clear();
        max_depth = max_speed = 0;
        for (int t = 0; t < tiles; t++) {
            if (!stepped[t]) continue;
            list.push_back(t);
            CatchUp(t);
            tile_change[t] = 0;
            tile_clamp[t] = 0;
            max_depth = std::max(max_depth, tile_max_depth[t]);
            max_speed = std::max(max_speed, tile_max_speed[t]);
        }
    }

    void Substep(float dt) {
        ApplySources(dt);
        pool->Run((int)list.size(), [&](int j) { Flux(list[j], dt); });
        pool->Run((int)list.size(), [&](int j) { Depth(list[j], dt); });
        max_depth = max_speed = 0;
        for (int t : list) {
            max_depth = std::max(max_depth, tile_max_depth[t]);
            max_speed = std::max(max_speed, tile_max_speed[t]);
            changed[t] = 1;
        }
        ground_dirty = true;
    }

    /// Puts tiles that stayed calm to sleep and wakes ring tiles that changed; ages the sleeping ones.
    void EndTick(float dt) {
        for (int t : list) {
            bool still = tile_change[t] < kCalmDepth && tile_max_flow[t] < kCalmFlow;
            if (!wet[t]) {
                awake[t] = 0;
                calm[t] = 0;
            } else if (still && !stream_tile[t]) {
                calm[t] = (uint8_t)std::min(calm[t] + 1, 255);
                awake[t] = calm[t] < kCalmTicks;
            } else {
                calm[t] = 0;
                awake[t] = 1;
            }
            if (!awake[t]) ZeroFlows(t);
        }
        for (int t = 0; t < tiles_x * tiles_z; t++) {
            if (stepped[t] || !wet[t]) continue;
            slept[t] += dt;
            if (slept[t] >= kSleepFlush) CatchUp(t);
        }
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
    for (auto* v : {&w->ground, &w->depth, &w->fl, &w->fr, &w->ft, &w->fb, &w->vx, &w->vz, &w->pol, &w->conc, &w->paint}) v->assign(n, 0.f);
    for (auto* v : {&w->awake, &w->stepped, &w->calm, &w->changed, &w->wet, &w->stream_tile}) v->assign(tiles, 0);
    w->source_cell.assign(n, 0);
    std::fill(w->changed.begin(), w->changed.end(), 1);
    w->tile_volume.assign(tiles, 0);
    w->tile_pol.assign(tiles, 0);
    for (auto* v : {&w->tile_max_depth, &w->tile_max_speed, &w->tile_max_flow, &w->tile_change, &w->slept, &w->paint_time})
        v->assign(tiles, 0.f);
    w->painted.assign(tiles, 0);
    w->tile_wet_cells.assign(tiles, 0);
    w->tile_clamp.assign(tiles, 0);
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
    w->ground_dirty = true;
    return 0;
}

CS_API void cs_water_set_params(CsWater* w, const CsWaterParams* params) {
    if (w && params) w->p = *params;
}

CS_API int32_t cs_water_set_sources(CsWater* w, const CsWaterSource* sources, int32_t count) {
    if (!w || count < 0 || (count > 0 && !sources)) return -1;
    // Tiles under the old sources wake too, so water they held can move now.
    for (auto& src : w->sources)
        for (auto c : src.cells) w->Wake(w->TileOf(c.i));
    w->sources.clear();
    std::fill(w->stream_tile.begin(), w->stream_tile.end(), 0);
    std::fill(w->source_cell.begin(), w->source_cell.end(), 0);
    for (int k = 0; k < count; k++) {
        Source src;
        src.s = sources[k];
        w->BuildSourceCells(src);
        if (src.s.type == CS_WATER_SEA)
            for (int i : w->border) {
                w->source_cell[i] = 1;
                w->Wake(w->TileOf(i));
            }
        for (auto c : src.cells) {
            int t = w->TileOf(c.i);
            if (src.s.type == CS_WATER_STREAM) w->stream_tile[t] = 1;
            w->source_cell[c.i] = 1;
            w->Wake(t);
        }
        w->sources.push_back(std::move(src));
    }
    return 0;
}

CS_API int32_t cs_water_step(CsWater* w, float dt, int32_t max_substeps, CsWaterStats* stats) {
    if (!w || !(dt >= 0)) return -1;
    float remaining = dt;
    int n = 0, clamps = 0;
    if (dt > 0) {
        w->BeginTick();
        while (remaining > 1e-6f && n < std::max(max_substeps, 1)) {
            float c = std::sqrt(w->p.gravity * std::max(w->max_depth, kMinCflDepth)) + w->max_speed;
            float limit = std::min(0.5f * w->cell / c, kMaxSubstep);
            // Split what's left evenly, so the last substep isn't a sliver.
            float h = remaining / std::ceil(remaining / limit);
            w->Substep(h);
            remaining -= h;
            n++;
        }
        for (int t : w->list) clamps += w->tile_clamp[t];
        w->EndTick(dt - std::max(remaining, 0.f));
        w->PaintTick(dt - std::max(remaining, 0.f));
    }
    if (stats) {
        double vol = 0, mass = 0;
        float md = 0;
        int wetCells = 0, sleeping = 0;
        for (int t = 0; t < w->tiles_x * w->tiles_z; t++)
            if (w->wet[t]) {
                vol += w->tile_volume[t];
                md = std::max(md, w->tile_max_depth[t]);
                mass += w->tile_pol[t];
                wetCells += w->tile_wet_cells[t];
                if (!w->stepped[t]) sleeping++;
            }
        stats->volume = vol;
        stats->max_depth = md;
        stats->max_speed = w->max_speed;
        stats->wet_cells = wetCells;
        stats->active_tiles = (int)w->list.size();
        stats->substeps = n;
        stats->simulated = dt - std::max(remaining, 0.f);
        stats->sleeping_tiles = sleeping;
        stats->clamp_hits = clamps;
        stats->pollution = mass;
    }
    return 0;
}

CS_API int32_t cs_water_tile_size(void) { return kTile; }

CS_API int32_t cs_water_read(CsWater* w, float* out, float* extra, uint8_t* tile_changed, int32_t all) {
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
    const float area = w->cell * w->cell;
    auto concAt = [&](int i) { return w->depth[i] > 0 ? w->pol[i] / (w->depth[i] * area) : 0.f; };
    w->pool->Run((int)todo.size(), [&](int j) {
        w->ForTile(todo[j], [&](int x, int z, int i) {
            float* o = out + (size_t)i * 4;
            float g = w->ground[i], dep = w->depth[i];
            if (dep > kWet) {
                o[0] = g + dep; o[1] = dep; o[2] = w->vx[i]; o[3] = w->vz[i];
                if (extra) extra[i] = concAt(i);
                return;
            }
            float best = -std::numeric_limits<float>::infinity(), bestConc = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = x + dx, nz = z + dz;
                    if ((dx | dz) == 0 || nx < 0 || nz < 0 || nx >= W || nz >= D) continue;
                    int n = nz * W + nx;
                    if (w->depth[n] > kWet && w->ground[n] + w->depth[n] > best) {
                        best = w->ground[n] + w->depth[n];
                        bestConc = concAt(n);
                    }
                }
            if (best > -std::numeric_limits<float>::infinity()) {
                o[0] = std::min(best, g); o[1] = 0; o[2] = o[3] = 0;
            } else {
                o[0] = g - 1; o[1] = -1; o[2] = o[3] = 0;
            }
            if (extra) extra[i] = bestConc;
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
    std::fill(w->pol.begin(), w->pol.end(), 0.f);
    w->Reset();
}

CS_API void cs_water_get_pollution(CsWater* w, float* mass) {
    if (w && mass) std::copy(w->pol.begin(), w->pol.end(), mass);
}

CS_API void cs_water_set_pollution(CsWater* w, const float* mass) {
    if (!w) return;
    for (size_t i = 0; i < w->pol.size(); i++)
        w->pol[i] = mass && std::isfinite(mass[i]) && w->depth[i] > 0 ? std::max(mass[i], 0.f) : 0.f;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        w->RecountTile(t);
        w->changed[t] = 1;
    }
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

CS_API int32_t cs_water_drain(CsWater* w, float x, float z, float radius, float level, float* removed_surface) {
    if (!w) return -1;
    const int W = w->w, D = w->d;
    if (removed_surface) std::fill(removed_surface, removed_surface + w->depth.size(), std::numeric_limits<float>::quiet_NaN());
    Source src;
    src.s = CsWaterSource{CS_WATER_LAKE, x, z, radius, 0, level, 0, 0};
    w->BuildSourceCells(src);
    // The source's own surface: the highest wet cell under it, never above its level (a lake still filling is lower).
    float top = -std::numeric_limits<float>::infinity();
    for (auto c : src.cells)
        if (w->depth[c.i] > kWet) top = std::max(top, w->ground[c.i] + w->depth[c.i]);
    if (!std::isfinite(top)) return 0;
    const float floor = std::min(top, level) - 0.3f;
    std::vector<uint8_t> seen(w->depth.size());
    std::vector<int> stack;
    auto push = [&](int i) {
        if (seen[i] || w->depth[i] <= 0 || w->ground[i] >= level || w->ground[i] + w->depth[i] < floor) return;
        seen[i] = 1;
        stack.push_back(i);
    };
    for (auto c : src.cells) push(c.i);
    int n = 0;
    while (!stack.empty()) {
        int i = stack.back();
        stack.pop_back();
        if (removed_surface) removed_surface[i] = w->ground[i] + w->depth[i];
        w->depth[i] = w->pol[i] = 0.f;
        w->fl[i] = w->fr[i] = w->ft[i] = w->fb[i] = w->vx[i] = w->vz[i] = 0.f;
        n++;
        int cx = i % W, cz = i / W;
        if (cx > 0) push(i - 1);
        if (cx < W - 1) push(i + 1);
        if (cz > 0) push(i - W);
        if (cz < D - 1) push(i + W);
    }
    if (n == 0) return 0;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        bool hit = false;
        w->ForTile(t, [&](int, int, int i) { hit |= seen[i] != 0; });
        if (!hit) continue;
        w->RecountTile(t);
        w->Wake(t);
    }
    w->ground_dirty = true;
    return n;
}

CS_API int32_t cs_water_read_ground(CsWater* w, uint8_t* out, int32_t force) {
    if (!w || !out) return -1;
    if (!force && !w->ground_dirty) return 0;
    w->ground_dirty = false;
    const int W = w->w, D = w->d;
    const float c1 = w->cell, c2 = w->cell * std::sqrt(2.f), far = 1e9f;
    // Two-pass chamfer distance to water, carrying the surface of the water each cell is closest to.
    std::vector<float> dist(w->depth.size(), far), surf(w->depth.size(), 0.f);
    for (size_t i = 0; i < dist.size(); i++)
        if (w->depth[i] > kShoreDepth) { dist[i] = 0; surf[i] = w->ground[i] + w->depth[i]; }
    auto relax = [&](int i, int n, float cost) {
        if (dist[n] + cost < dist[i]) { dist[i] = dist[n] + cost; surf[i] = surf[n]; }
    };
    for (int z = 0; z < D; z++)
        for (int x = 0; x < W; x++) {
            int i = z * W + x;
            if (x > 0) relax(i, i - 1, c1);
            if (z > 0) {
                relax(i, i - W, c1);
                if (x > 0) relax(i, i - W - 1, c2);
                if (x < W - 1) relax(i, i - W + 1, c2);
            }
        }
    for (int z = D - 1; z >= 0; z--)
        for (int x = W - 1; x >= 0; x--) {
            int i = z * W + x;
            if (x < W - 1) relax(i, i + 1, c1);
            if (z < D - 1) {
                relax(i, i + W, c1);
                if (x < W - 1) relax(i, i + W + 1, c2);
                if (x > 0) relax(i, i + W - 1, c2);
            }
        }
    w->pool->Run(D, [&](int z) {
        for (int x = 0; x < W; x++) {
            size_t i = (size_t)z * W + x;
            float m = dist[i] >= far ? 64.f : dist[i] + 4.f * std::max(0.f, w->ground[i] - surf[i]);
            out[i * 2] = (uint8_t)std::min(255.f, std::round(m * 4.f));
            out[i * 2 + 1] = (uint8_t)std::round(std::clamp(w->paint[i], 0.f, 1.f) * 255.f);
        }
    });
    return 1;
}

CS_API void cs_water_get_paint(CsWater* w, float* paint) {
    if (w && paint) std::copy(w->paint.begin(), w->paint.end(), paint);
}

CS_API void cs_water_set_paint(CsWater* w, const float* paint) {
    if (!w) return;
    for (size_t i = 0; i < w->paint.size(); i++) w->paint[i] = paint && std::isfinite(paint[i]) ? std::clamp(paint[i], 0.f, 1.f) : 0.f;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        bool any = false;
        w->ForTile(t, [&](int, int, int i) { any |= w->paint[i] > 0; });
        w->painted[t] = any;
    }
    w->ground_dirty = true;
}
