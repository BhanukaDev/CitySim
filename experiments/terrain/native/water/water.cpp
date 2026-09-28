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
// Storage is sparse: cells live in 64² tiles (TileData) allocated only where water is, next to it, or under a source,
// so memory follows the water, not the map (a 4097² grid would need ~0.8 GB dense). A tile's ground is read from the
// terrain heightmap (kept by pointer) when it's allocated. A dry tile with no paint, no source cells and no wet
// neighbouring tile is freed after a tick. Unallocated tiles are dry ground.
//
// Work is done per tile. A call to cs_water_step is one tick: at its start the tiles to step are chosen (awake
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
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

namespace {

constexpr int kShift = 6;
constexpr int kTile = 1 << kShift;
constexpr int kMask = kTile - 1;
constexpr int kN = kTile * kTile;
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
#ifndef CS_GROUND_LOW
#define CS_GROUND_LOW 0.5f
#endif
/// Where a coarse water cell's ground sits between the mean (0) and the lowest (1) of the terrain vertices it covers.
/// Measured (--demo-water, 3.5 m wide bed, 1.5 m deep): the mean left it 0.5 m deep on 7 m cells and the stream spread
/// 24 m wide; the lowest carried 14 m beds into the neighbouring cells (21 m wide); halfway keeps both at their width.
constexpr float kGroundLow = CS_GROUND_LOW;

/// One 64² tile's cells, local index (z & 63) * 64 + (x & 63). Cells past the grid edge (partial tiles) stay 0.
struct TileData {
    float ground[kN], depth[kN], fl[kN], fr[kN], ft[kN], fb[kN], vx[kN], vz[kN], pol[kN], conc[kN], paint[kN];
    uint8_t source_cell[kN];
};

/// A tile and its four neighbours (null = none, or not usable for the pass at hand).
struct Near {
    TileData *c, *l, *r, *t, *b;
};

// The cell next to local cell li: its tile (from n) and its local index in j. The caller checks the map edge.
inline TileData* Left(const Near& n, int li, int& j) {
    if (li & kMask) { j = li - 1; return n.c; }
    j = li + kMask; return n.l;
}
inline TileData* Right(const Near& n, int li, int& j) {
    if ((li & kMask) != kMask) { j = li + 1; return n.c; }
    j = li - kMask; return n.r;
}
inline TileData* Up(const Near& n, int li, int& j) {
    if (li >= kTile) { j = li - kTile; return n.c; }
    j = li + kN - kTile; return n.t;
}
inline TileData* Down(const Near& n, int li, int& j) {
    if (li < kN - kTile) { j = li + kTile; return n.c; }
    j = li - (kN - kTile); return n.b;
}

struct SourceCell {
    int32_t i;  // global index z * width + x
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
    // The terrain heightmap (owned by the caller, alive as long as this handle), factor terrain cells per water cell.
    const float* heights = nullptr;
    int hw = 0, hd = 0, factor = 1;
    std::vector<std::unique_ptr<TileData>> data;
    int allocated = 0;
    // Per tile: awake (wants stepping), stepped this tick, calm tick count, changed since the last read, holds water,
    // holds a stream source (always awake), holds any source cell, holds any wet paint.
    std::vector<uint8_t> awake, stepped, calm, changed, wet, stream_tile, source_tile, painted;
    std::vector<int> list;
    std::vector<double> tile_volume, tile_pol;
    std::vector<float> tile_max_depth, tile_max_speed, tile_max_flow, tile_change, slept, paint_time;
    // The water or the paint changed since the last cs_water_read_ground.
    bool ground_dirty = true;
    std::vector<int> tile_wet_cells, tile_clamp;
    std::vector<Source> sources;
    std::vector<int> border;     // cells within two of the border, for the sea
    std::vector<int> sea_cells;  // border cells below the sea's level
    float sea_level = 0;
    bool has_sea = false;
    // What the last cs_water_drain removed: per tile, the old surface (NaN = untouched).
    std::vector<int> drained_index;
    std::vector<std::pair<int, std::vector<float>>> drained;
    CsWaterParams p{9.81f, 0.2f, 0.0f, 25.0f, 2.0f, 15, 0.03f, 0.0f, 0.0f, 0.0f};
    float max_depth = 0, max_speed = 0;
    /// False while there's no pollutant anywhere and no source adds any: the pollutant passes are skipped.
    bool carry = false;
    std::unique_ptr<cs::ThreadPool> pool;

    int Tile(int x, int z) const { return (z >> kShift) * tiles_x + (x >> kShift); }
    static int Local(int x, int z) { return ((z & kMask) << kShift) | (x & kMask); }

    /// The tile holding global cell i (null when unallocated), its local index in li.
    TileData* Cell(int i, int& li) const {
        int x = i % w, z = i / w;
        li = Local(x, z);
        return data[Tile(x, z)].get();
    }

    /// Ground under water cell (x, z) from the terrain vertices around it: between their mean and their lowest
    /// (kGroundLow), so a stream bed narrower than a cell keeps most of its depth instead of being averaged away.
    float GroundAt(int x, int z) const {
        if (factor == 1) return heights[(size_t)z * hw + x];
        int half = factor / 2, cx = x * factor, cz = z * factor;
        double sum = 0;
        float low = std::numeric_limits<float>::infinity();
        int n = 0;
        for (int hz = std::max(cz - half, 0); hz <= std::min(cz + half, hd - 1); hz++)
            for (int hx = std::max(cx - half, 0); hx <= std::min(cx + half, hw - 1); hx++) {
                float h = heights[(size_t)hz * hw + hx];
                sum += h;
                low = std::min(low, h);
                n++;
            }
        float mean = (float)(sum / n);
        return mean + (low - mean) * kGroundLow;
    }

    float GroundOf(int i) const {
        int li;
        auto* c = Cell(i, li);
        return c ? c->ground[li] : GroundAt(i % w, i / w);
    }

    template <class F>
    void ForTile(int t, F&& f) const {
        int x0 = (t % tiles_x) * kTile, z0 = (t / tiles_x) * kTile;
        int nx = std::min(kTile, w - x0), nz = std::min(kTile, d - z0);
        for (int lz = 0; lz < nz; lz++)
            for (int lx = 0; lx < nx; lx++) f(x0 + lx, z0 + lz, (lz << kShift) | lx);
    }

    /// Tile t and its neighbours; with steppedOnly, neighbours not stepped this tick are left out (walls).
    Near Neighbours(int t, bool steppedOnly) const {
        int tx = t % tiles_x, tz = t / tiles_x;
        auto get = [&](bool inside, int n) -> TileData* {
            return inside && (!steppedOnly || stepped[n]) ? data[n].get() : nullptr;
        };
        return {data[t].get(), get(tx > 0, t - 1), get(tx < tiles_x - 1, t + 1), get(tz > 0, t - tiles_x),
                get(tz < tiles_z - 1, t + tiles_x)};
    }

    TileData* Ensure(int t) {
        if (auto* c = data[t].get()) return c;
        auto tile = std::make_unique<TileData>();
        auto* c = tile.get();
        data[t] = std::move(tile);
        allocated++;
        ForTile(t, [&](int x, int z, int li) { c->ground[li] = GroundAt(x, z); });
        return c;
    }

    void Release(int t) {
        if (!data[t]) return;
        data[t].reset();
        allocated--;
        awake[t] = calm[t] = wet[t] = painted[t] = 0;
        tile_volume[t] = tile_pol[t] = 0;
        tile_max_depth[t] = tile_max_speed[t] = tile_max_flow[t] = slept[t] = paint_time[t] = 0;
        tile_wet_cells[t] = 0;
        changed[t] = 1;
    }

    bool WetAround(int t) const {
        int tx = t % tiles_x, tz = t / tiles_x;
        for (int z = std::max(tz - 1, 0); z <= std::min(tz + 1, tiles_z - 1); z++)
            for (int x = std::max(tx - 1, 0); x <= std::min(tx + 1, tiles_x - 1); x++)
                if (wet[z * tiles_x + x]) return true;
        return false;
    }

    /// Frees tiles nothing needs any more: dry, unpainted, no source, no wet tile around.
    void ReleaseIdle() {
        for (int t = 0; t < tiles_x * tiles_z; t++)
            if (data[t] && !awake[t] && !painted[t] && !source_tile[t] && !stream_tile[t] && !WetAround(t)) Release(t);
    }

    void Wake(int t) {
        awake[t] = changed[t] = 1;
        calm[t] = 0;
    }

    /// Wakes the allocated tiles in a rect (unallocated ones are dry ground and have nothing to do).
    void MarkRect(int x0, int z0, int x1, int z1) {
        for (int tz = z0 >> kShift; tz <= z1 >> kShift; tz++)
            for (int tx = x0 >> kShift; tx <= x1 >> kShift; tx++) {
                int t = tz * tiles_x + tx;
                if (data[t]) Wake(t);
            }
    }

    /// Recomputes a tile's totals from its cells (after changes made outside a substep).
    void RecountTile(int t) {
        const float area = cell * cell;
        double vol = 0, mass = 0;
        float md = 0, mv = 0;
        int wetCells = 0;
        bool any = false, paintAny = false;
        if (auto* c = data[t].get())
            ForTile(t, [&](int, int, int i) {
                paintAny |= c->paint[i] > 0;
                float di = c->depth[i];
                if (di <= 0) return;
                any = true;
                vol += di;
                mass += c->pol[i];
                md = std::max(md, di);
                mv = std::max(mv, std::sqrt(c->vx[i] * c->vx[i] + c->vz[i] * c->vz[i]));
                if (di > kWet) wetCells++;
            });
        tile_volume[t] = vol * area;
        tile_pol[t] = mass;
        tile_max_depth[t] = md;
        tile_max_speed[t] = mv;
        tile_wet_cells[t] = wetCells;
        wet[t] = any;
        painted[t] = paintAny;
    }

    void ZeroFlows(int t) {
        if (auto* c = data[t].get())
            for (auto* a : {c->fl, c->fr, c->ft, c->fb, c->vx, c->vz}) std::fill(a, a + kN, 0.f);
        tile_max_speed[t] = 0;
    }

    /// After the depths were set from outside: clear flows, recount, wake every wet tile, redraw everything.
    void Reset() {
        for (int t = 0; t < tiles_x * tiles_z; t++) {
            if (auto* c = data[t].get()) {
                ZeroFlows(t);
                for (int i = 0; i < kN; i++)
                    if (c->depth[i] <= 0) c->pol[i] = 0;
            }
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
        auto* c = data[t].get();
        if (!c) return;
        const float gain = p.paint_rate * time, fade = p.paint_fade * time;
        const Near n = Neighbours(t, false);
        bool any = false, moved = false;
        auto deep = [](TileData* o, int j) { return o && o->depth[j] > kPaintDepth; };
        ForTile(t, [&](int x, int z, int i) {
            float v = c->paint[i], next = v;
            int j;
            if (c->depth[i] > kPaintDepth) next = std::min(1.f, v + gain);
            else if ((x > 0 && deep(Left(n, i, j), j)) || (x < w - 1 && deep(Right(n, i, j), j))
                     || (z > 0 && deep(Up(n, i, j), j)) || (z < d - 1 && deep(Down(n, i, j), j)))
                next = std::min(1.f, v + gain * 0.5f);
            else if (v > 0) next = std::max(0.f, v - fade);
            if (next != v) { c->paint[i] = next; moved = true; }
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
                if (!near || !data[t]) { paint_time[t] = 0; continue; }
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
        auto* c = data[t].get();
        if (time <= 0 || !c) return;
        const float evap = p.evaporation * time, keep = std::exp(-p.pollution_decay * time);
        ForTile(t, [&](int, int, int i) {
            if (c->depth[i] <= 0) return;
            c->depth[i] = std::max(0.f, c->depth[i] - evap);
            c->pol[i] = c->depth[i] > 0 ? c->pol[i] * keep : 0.f;
        });
        RecountTile(t);
        changed[t] = 1;
    }

    void BuildSourceCells(Source& src) const {
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

    /// The border cells below the sea's level; their tiles are allocated.
    void BuildSeaCells() {
        sea_cells.clear();
        if (!has_sea) return;
        for (int i : border)
            if (GroundOf(i) < sea_level) {
                sea_cells.push_back(i);
                Ensure(Tile(i % w, i / w));
            }
    }

    /// Sets a cell's depth from a source. Water taken away takes its share of pollutant; a sleeping tile that changes
    /// noticeably wakes (it's stepped from the next tick).
    void SetFromSource(TileData* c, int li, int t, float next) {
        float prev = c->depth[li];
        next = std::max(next, 0.f);
        if (next < prev) c->pol[li] = next > 0 ? c->pol[li] * (next / prev) : 0.f;
        c->depth[li] = next;
        if (!stepped[t] && std::fabs(next - prev) > kWakeDepth) Wake(t);
    }

    void ApplySources(float dt) {
        const float area = cell * cell;
        for (auto& src : sources) {
            const auto& s = src.s;
            auto each = [&](auto&& f) {
                for (auto sc : src.cells) {
                    int x = sc.i % w, z = sc.i / w, t = Tile(x, z), li = Local(x, z);
                    f(Ensure(t), li, t, sc.w);
                }
            };
            switch (s.type) {
            case CS_WATER_STREAM: {
                float k = s.rate * dt / (src.weight_sum * area);
                float m = s.pollution * dt / src.weight_sum;
                each([&](TileData* c, int li, int t, float wt) {
                    SetFromSource(c, li, t, c->depth[li] + k * wt);
                    if (c->depth[li] > 0) c->pol[li] += m * wt;
                });
                break;
            }
            case CS_WATER_LEVEL:
                each([&](TileData* c, int li, int t, float wt) {
                    float target = std::max(s.level - c->ground[li], 0.f);
                    SetFromSource(c, li, t, c->depth[li] + (target - c->depth[li]) * std::min(1.f, p.level_rate * dt * wt));
                });
                break;
            case CS_WATER_LAKE: {
                double need = 0;
                each([&](TileData* c, int li, int, float wt) {
                    float missing = s.level - c->ground[li] - c->depth[li];
                    if (missing > 0) need += missing * std::min(1.f, p.level_rate * dt * wt);
                });
                if (need <= 0) break;
                double allowed = s.max_rate * dt / area;
                float scale = (float)std::min(1.0, allowed / need);
                each([&](TileData* c, int li, int t, float wt) {
                    float missing = s.level - c->ground[li] - c->depth[li];
                    if (missing > 0) SetFromSource(c, li, t, c->depth[li] + missing * std::min(1.f, p.level_rate * dt * wt) * scale);
                });
                break;
            }
            case CS_WATER_SEA:
                for (int i : sea_cells) {
                    int x = i % w, z = i / w, t = Tile(x, z), li = Local(x, z);
                    auto* c = Ensure(t);
                    if (c->ground[li] < s.level) SetFromSource(c, li, t, s.level - c->ground[li]);
                }
                break;
            }
        }
    }

    void Flux(int t, float dt) {
        const float k = dt * p.gravity, damp0 = std::max(0.f, 1.f - p.damping * dt);
        const float area = cell * cell, fric = dt * p.gravity * p.manning * p.manning;
        const float fricSheet = dt * p.gravity * std::max(0.f, kSheetManning * kSheetManning - p.manning * p.manning);
        // Under a sea every border cell is closed, so the sea feeds the map instead of draining off it.
        const int open = has_sea ? 0 : p.open_edges;
        // Neighbouring tiles that aren't stepped this tick are walls (see the top of the file).
        const Near n = Neighbours(t, true);
        TileData* c = n.c;
        ForTile(t, [&](int x, int z, int i) {
            float di = c->depth[i];
            if (di <= 0) {
                c->fl[i] = c->fr[i] = c->ft[i] = c->fb[i] = 0;
                c->conc[i] = 0;
                return;
            }
            if (carry) c->conc[i] = c->pol[i] / (di * area);
            float h = c->ground[i] + di;
            const int edges = c->source_cell[i] ? 0 : open;
            float speed = std::sqrt(c->vx[i] * c->vx[i] + c->vz[i] * c->vz[i]);
            float hd = std::max(di, 0.02f);
            float sheet = std::clamp((kSheetFadeDepth - di) / (kSheetFadeDepth - kSheetFullDepth), 0.f, 1.f);
            float fk = p.manning > 0 ? fric + fricSheet * sheet * sheet * (3.f - 2.f * sheet) : 0.f;
            float damp = speed > 0 ? damp0 / (1.f + fk * speed / (hd * std::sqrt(hd))) : damp0;
            // Outside the map, an open edge acts like dry ground at this cell's height.
            auto pipe = [&](float f, bool inside, TileData* o, int j, int bit) {
                float dh;
                if (inside) {
                    if (!o) return 0.f;
                    dh = h - o->ground[j] - o->depth[j];
                } else if (edges & bit) dh = di;
                else return 0.f;
                return std::max(0.f, f * damp + k * di * dh);
            };
            int j;
            TileData* o;
            o = Left(n, i, j);
            float l = pipe(c->fl[i], x > 0, o, j, 1);
            o = Right(n, i, j);
            float r = pipe(c->fr[i], x < w - 1, o, j, 2);
            o = Up(n, i, j);
            float tp = pipe(c->ft[i], z > 0, o, j, 4);
            o = Down(n, i, j);
            float b = pipe(c->fb[i], z < d - 1, o, j, 8);
            float sum = l + r + tp + b;
            if (sum > 0) {
                // Never send out more than the cell holds.
                float s = std::min(1.f, di * area / (sum * dt));
                l *= s; r *= s; tp *= s; b *= s;
            }
            c->fl[i] = l; c->fr[i] = r; c->ft[i] = tp; c->fb[i] = b;
        });
    }

    void Depth(int t, float dt) {
        const float area = cell * cell, evap = p.evaporation * dt, vmax = p.max_speed;
        const float keep = std::exp(-p.pollution_decay * dt);
        // Pipes from cells in tiles that aren't stepped are zero (their flows were cleared when they slept).
        const Near n = Neighbours(t, true);
        TileData* c = n.c;
        double vol = 0, mass = 0;
        float md = 0, mv = 0, mq = 0, change = 0;
        int wetCells = 0, clamps = 0;
        bool any = false;
        ForTile(t, [&](int x, int z, int i) {
            int jl, jr, jt, jb;
            TileData* ol = x > 0 ? Left(n, i, jl) : nullptr;
            TileData* orr = x < w - 1 ? Right(n, i, jr) : nullptr;
            TileData* ot = z > 0 ? Up(n, i, jt) : nullptr;
            TileData* ob = z < d - 1 ? Down(n, i, jb) : nullptr;
            float inL = ol ? ol->fr[jl] : 0.f, inR = orr ? orr->fl[jr] : 0.f;
            float inT = ot ? ot->fb[jt] : 0.f, inB = ob ? ob->ft[jb] : 0.f;
            float out = c->fl[i] + c->fr[i] + c->ft[i] + c->fb[i];
            float d0 = c->depth[i];
            float d1 = d0 + dt * (inL + inR + inT + inB - out) / area;
            if (d1 < -1e-5f * std::max(1.f, d0)) clamps++;
            change = std::max(change, std::fabs(d1 - d0));
            d1 = std::max(0.f, d1 - evap);
            // Pollutant rides the same pipes, at the concentration of the cell it leaves.
            float m = c->pol[i];
            if (carry) {
                float mIn = inL * (ol ? ol->conc[jl] : 0.f) + inR * (orr ? orr->conc[jr] : 0.f)
                          + inT * (ot ? ot->conc[jt] : 0.f) + inB * (ob ? ob->conc[jb] : 0.f);
                m = d1 > 0 ? std::max(0.f, (m + dt * (mIn - out * c->conc[i])) * keep) : 0.f;
                c->pol[i] = m;
            }
            float avg = 0.5f * (d0 + d1);
            float u = 0, v = 0;
            // Films thinner than kWet get no velocity: flow / a tiny depth spikes, and the CFL limit would follow it.
            if (avg > kWet) {
                u = 0.5f * (inL - c->fl[i] + c->fr[i] - inR) / (cell * avg);
                v = 0.5f * (inT - c->ft[i] + c->fb[i] - inB) / (cell * avg);
                // Flow passing through a thin cell from a deeper one reads as a huge speed; cap at Froude 3 (fast,
                // supercritical flow on steep slopes stays possible).
                float cap = std::min(vmax, 3.f * std::sqrt(p.gravity * avg));
                float s = std::sqrt(u * u + v * v);
                if (s > cap) { u *= cap / s; v *= cap / s; s = cap; }
                mv = std::max(mv, s);
                mq = std::max(mq, s * avg);
            }
            c->depth[i] = d1;
            c->vx[i] = u;
            c->vz[i] = v;
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

    /// Chooses the tiles to step this tick: awake and stream tiles plus a ring of one around them (allocated here).
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
            Ensure(t);
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

    float* Field(TileData* c, int field) const {
        switch (field) {
        case CS_WATER_FIELD_DEPTH: return c->depth;
        case CS_WATER_FIELD_POLLUTION: return c->pol;
        case CS_WATER_FIELD_PAINT: return c->paint;
        default: return nullptr;
        }
    }
};

CS_API CsWater* cs_water_create(int32_t width, int32_t depth, float cell_size, int32_t threads, const float* heights,
                                int32_t terrain_width, int32_t terrain_depth, int32_t factor) {
    if (width < 2 || depth < 2 || !(cell_size > 0) || (int64_t)width * depth > (int64_t)8193 * 8193 || !heights
        || factor < 1 || (terrain_width - 1) != (width - 1) * factor || (terrain_depth - 1) != (depth - 1) * factor)
        return nullptr;
    auto* w = new CsWater();
    w->w = width;
    w->d = depth;
    w->cell = cell_size;
    w->heights = heights;
    w->hw = terrain_width;
    w->hd = terrain_depth;
    w->factor = factor;
    w->tiles_x = (width + kTile - 1) / kTile;
    w->tiles_z = (depth + kTile - 1) / kTile;
    size_t tiles = (size_t)w->tiles_x * w->tiles_z;
    w->data.resize(tiles);
    for (auto* v : {&w->awake, &w->stepped, &w->calm, &w->changed, &w->wet, &w->stream_tile, &w->source_tile, &w->painted})
        v->assign(tiles, 0);
    std::fill(w->changed.begin(), w->changed.end(), 1);
    w->tile_volume.assign(tiles, 0);
    w->tile_pol.assign(tiles, 0);
    for (auto* v : {&w->tile_max_depth, &w->tile_max_speed, &w->tile_max_flow, &w->tile_change, &w->slept, &w->paint_time})
        v->assign(tiles, 0.f);
    w->tile_wet_cells.assign(tiles, 0);
    w->tile_clamp.assign(tiles, 0);
    w->drained_index.assign(tiles, -1);
    for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
            if (x < 2 || z < 2 || x >= width - 2 || z >= depth - 2) w->border.push_back(z * width + x);
    w->pool = std::make_unique<cs::ThreadPool>(threads > 0 ? threads : cs::ThreadPool::DefaultThreads());
    return w;
}

CS_API void cs_water_destroy(CsWater* w) { delete w; }

CS_API int32_t cs_water_set_ground(CsWater* w, int32_t x0, int32_t z0, int32_t x1, int32_t z1) {
    if (!w) return -1;
    x0 = std::max(x0, 0); z0 = std::max(z0, 0);
    x1 = std::min(x1, w->w - 1); z1 = std::min(z1, w->d - 1);
    if (x0 > x1 || z0 > z1) return 0;
    w->pool->Run(z1 - z0 + 1, [&](int j) {
        int z = z0 + j;
        for (int x = x0; x <= x1; x++)
            if (auto* c = w->data[w->Tile(x, z)].get()) c->ground[CsWater::Local(x, z)] = w->GroundAt(x, z);
    });
    w->MarkRect(x0, z0, x1, z1);
    if (w->has_sea && (x0 < 2 || z0 < 2 || x1 >= w->w - 2 || z1 >= w->d - 2)) w->BuildSeaCells();
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
        for (auto c : src.cells) {
            int t = w->Tile(c.i % w->w, c.i / w->w);
            if (w->data[t]) w->Wake(t);
        }
    for (int i : w->sea_cells) w->Wake(w->Tile(i % w->w, i / w->w));
    w->sources.clear();
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        if (w->source_tile[t] && w->data[t]) std::memset(w->data[t]->source_cell, 0, kN);
        w->source_tile[t] = w->stream_tile[t] = 0;
    }
    w->has_sea = false;
    for (int k = 0; k < count; k++) {
        Source src;
        src.s = sources[k];
        w->BuildSourceCells(src);
        if (src.s.type == CS_WATER_SEA) {
            w->has_sea = true;
            w->sea_level = src.s.level;
        }
        for (auto c : src.cells) {
            int x = c.i % w->w, z = c.i / w->w, t = w->Tile(x, z);
            w->Ensure(t)->source_cell[CsWater::Local(x, z)] = 1;
            w->source_tile[t] = 1;
            if (src.s.type == CS_WATER_STREAM) w->stream_tile[t] = 1;
            w->Wake(t);
        }
        w->sources.push_back(std::move(src));
    }
    w->BuildSeaCells();
    for (int i : w->sea_cells) w->Wake(w->Tile(i % w->w, i / w->w));
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
        w->ReleaseIdle();
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
        stats->allocated_tiles = w->allocated;
        stats->allocated_mb = (float)(w->allocated * (double)sizeof(TileData) / (1024.0 * 1024.0));
    }
    return 0;
}

CS_API int32_t cs_water_tile_size(void) { return kTile; }

CS_API int32_t cs_water_changed_tiles(CsWater* w, uint8_t* flags, int32_t all) {
    if (!w || !flags) return -1;
    int n = 0;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        flags[t] = all || w->changed[t];
        n += flags[t];
        w->changed[t] = 0;
    }
    return n;
}

CS_API int32_t cs_water_read_tiles(CsWater* w, const int32_t* tiles, int32_t count, float* out, float* extra, uint8_t* visible) {
    if (!w || !tiles || !out || !visible || count < 0) return -1;
    const int W = w->w, D = w->d;
    const float area = w->cell * w->cell;
    w->pool->Run(count, [&](int k) {
        const int t = tiles[k];
        float* o4 = out + (size_t)k * kN * 4;
        float* ex = extra ? extra + (size_t)k * kN : nullptr;
        // Hidden everywhere to start (also the cells past the grid edge of a partial tile).
        for (int i = 0; i < kN; i++) {
            o4[i * 4] = 0; o4[i * 4 + 1] = -1; o4[i * 4 + 2] = o4[i * 4 + 3] = 0;
            if (ex) ex[i] = 0;
        }
        visible[k] = 0;
        if (t < 0 || t >= w->tiles_x * w->tiles_z || !w->WetAround(t)) return;
        const TileData* c = w->data[t].get();
        bool any = false;
        auto concAt = [&](const TileData* o, int j) { return o->depth[j] > 0 ? o->pol[j] / (o->depth[j] * area) : 0.f; };
        w->ForTile(t, [&](int x, int z, int i) {
            float* o = o4 + (size_t)i * 4;
            float g = c ? c->ground[i] : w->GroundAt(x, z), dep = c ? c->depth[i] : 0.f;
            if (dep > kWet) {
                o[0] = g + dep; o[1] = dep; o[2] = c->vx[i]; o[3] = c->vz[i];
                if (ex) ex[i] = concAt(c, i);
                any = true;
                return;
            }
            float best = -std::numeric_limits<float>::infinity(), bestConc = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = x + dx, nz = z + dz;
                    if ((dx | dz) == 0 || nx < 0 || nz < 0 || nx >= W || nz >= D) continue;
                    const TileData* on = w->data[w->Tile(nx, nz)].get();
                    if (!on) continue;
                    int j = CsWater::Local(nx, nz);
                    if (on->depth[j] > kWet && on->ground[j] + on->depth[j] > best) {
                        best = on->ground[j] + on->depth[j];
                        bestConc = concAt(on, j);
                    }
                }
            if (best > -std::numeric_limits<float>::infinity()) {
                o[0] = std::min(best, g); o[1] = 0;
                any = true;
            } else o[0] = g - 1;
            if (ex) ex[i] = bestConc;
        });
        visible[k] = any;
    });
    return 0;
}

CS_API int32_t cs_water_list_tiles(CsWater* w, int32_t* out, int32_t capacity) {
    if (!w) return -1;
    int n = 0;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++)
        if (w->data[t]) {
            if (out && n < capacity) out[n] = t;
            n++;
        }
    return n;
}

CS_API int32_t cs_water_get_tile(CsWater* w, int32_t t, int32_t field, float* out) {
    if (!w || !out || t < 0 || t >= w->tiles_x * w->tiles_z) return -1;
    auto* c = w->data[t].get();
    if (!c) return 0;
    const float* f = w->Field(c, field);
    if (!f) return -1;
    std::copy(f, f + kN, out);
    return 1;
}

CS_API int32_t cs_water_set_tile(CsWater* w, int32_t t, int32_t field, const float* in) {
    if (!w || t < 0 || t >= w->tiles_x * w->tiles_z) return -1;
    if (!in && !w->data[t]) return 0;
    auto* c = w->Ensure(t);
    float* f = w->Field(c, field);
    if (!f) return -1;
    w->ForTile(t, [&](int, int, int i) {
        float v = in && std::isfinite(in[i]) ? std::max(in[i], 0.f) : 0.f;
        f[i] = field == CS_WATER_FIELD_PAINT ? std::min(v, 1.f) : v;
    });
    return 0;
}

CS_API void cs_water_commit(CsWater* w) {
    if (w) w->Reset();
}

CS_API void cs_water_clear(CsWater* w) {
    if (!w) return;
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        auto* c = w->data[t].get();
        if (!c) continue;
        bool keep = w->source_tile[t];
        for (int i = 0; i < kN && !keep; i++) keep = c->paint[i] > 0;
        if (!keep) { w->Release(t); continue; }
        std::fill(c->depth, c->depth + kN, 0.f);
        std::fill(c->pol, c->pol + kN, 0.f);
    }
    w->Reset();
}

CS_API int32_t cs_water_raise_tile(CsWater* w, int32_t t, const float* surface) {
    if (!w || !surface || t < 0 || t >= w->tiles_x * w->tiles_z) return -1;
    bool any = false;
    w->ForTile(t, [&](int x, int z, int i) {
        any |= std::isfinite(surface[i]) && surface[i] > (w->data[t] ? w->data[t]->ground[i] : w->GroundAt(x, z));
    });
    if (!any) return 0;
    auto* c = w->Ensure(t);
    w->ForTile(t, [&](int, int, int i) {
        if (std::isfinite(surface[i])) c->depth[i] = std::max(c->depth[i], surface[i] - c->ground[i]);
    });
    return 1;
}

CS_API void cs_water_fill_sources(CsWater* w) {
    if (!w) return;
    const int W = w->w, D = w->d;
    if (w->has_sea) {
        float level = w->sea_level;
        std::vector<uint8_t> seen((size_t)W * D);
        std::vector<int> stack;
        auto push = [&](int i) {
            if (seen[i]) return;
            seen[i] = 1;
            int x = i % W, z = i / W;
            auto* c = w->Ensure(w->Tile(x, z));
            if (c->ground[CsWater::Local(x, z)] >= level) return;
            stack.push_back(i);
        };
        for (int i : w->border)
            if (w->GroundOf(i) < level) push(i);
        while (!stack.empty()) {
            int i = stack.back();
            stack.pop_back();
            int x = i % W, z = i / W, li = CsWater::Local(x, z);
            auto* c = w->data[w->Tile(x, z)].get();
            c->depth[li] = std::max(c->depth[li], level - c->ground[li]);
            if (x > 0) push(i - 1);
            if (x < W - 1) push(i + 1);
            if (z > 0) push(i - W);
            if (z < D - 1) push(i + W);
        }
    }
    w->Reset();
}

CS_API int32_t cs_water_drain(CsWater* w, float x, float z, float radius, float level) {
    if (!w) return -1;
    const int W = w->w, D = w->d;
    for (auto& [t, v] : w->drained) w->drained_index[t] = -1;
    w->drained.clear();
    Source src;
    src.s = CsWaterSource{CS_WATER_LAKE, x, z, radius, 0, level, 0, 0};
    w->BuildSourceCells(src);
    auto depthOf = [&](int i, TileData*& c, int& li) {
        c = w->Cell(i, li);
        return c ? c->depth[li] : 0.f;
    };
    // The source's own surface: the highest wet cell under it, never above its level (a lake still filling is lower).
    float top = -std::numeric_limits<float>::infinity();
    for (auto sc : src.cells) {
        TileData* c;
        int li;
        if (depthOf(sc.i, c, li) > kWet) top = std::max(top, c->ground[li] + c->depth[li]);
    }
    if (!std::isfinite(top)) return 0;
    const float floor = std::min(top, level) - 0.3f;
    std::vector<uint8_t> seen((size_t)W * D);
    std::vector<int> stack;
    auto push = [&](int i) {
        TileData* c;
        int li;
        if (seen[i] || depthOf(i, c, li) <= 0 || c->ground[li] >= level || c->ground[li] + c->depth[li] < floor) return;
        seen[i] = 1;
        stack.push_back(i);
    };
    for (auto sc : src.cells) push(sc.i);
    int n = 0;
    std::vector<uint8_t> hit(w->tiles_x * w->tiles_z);
    while (!stack.empty()) {
        int i = stack.back();
        stack.pop_back();
        int cx = i % W, cz = i / W, t = w->Tile(cx, cz), li = CsWater::Local(cx, cz);
        auto* c = w->data[t].get();
        if (w->drained_index[t] < 0) {
            w->drained_index[t] = (int)w->drained.size();
            w->drained.push_back({t, std::vector<float>(kN, std::numeric_limits<float>::quiet_NaN())});
        }
        w->drained[w->drained_index[t]].second[li] = c->ground[li] + c->depth[li];
        c->depth[li] = c->pol[li] = 0.f;
        c->fl[li] = c->fr[li] = c->ft[li] = c->fb[li] = c->vx[li] = c->vz[li] = 0.f;
        hit[t] = 1;
        n++;
        if (cx > 0) push(i - 1);
        if (cx < W - 1) push(i + 1);
        if (cz > 0) push(i - W);
        if (cz < D - 1) push(i + W);
    }
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        if (!hit[t]) continue;
        w->RecountTile(t);
        w->Wake(t);
    }
    w->ground_dirty = true;
    return n;
}

CS_API int32_t cs_water_list_drained(CsWater* w, int32_t* out, int32_t capacity) {
    if (!w) return -1;
    int n = (int)w->drained.size();
    for (int k = 0; out && k < std::min(n, capacity); k++) out[k] = w->drained[k].first;
    return n;
}

CS_API int32_t cs_water_get_drained(CsWater* w, int32_t t, float* out) {
    if (!w || !out || t < 0 || t >= w->tiles_x * w->tiles_z) return -1;
    int k = w->drained_index[t];
    if (k < 0) return 0;
    std::copy(w->drained[k].second.begin(), w->drained[k].second.end(), out);
    return 1;
}

CS_API int32_t cs_water_read_ground(CsWater* w, uint8_t* out, int32_t force, int32_t mark_factor) {
    if (!w || !out || mark_factor < 1 || (w->w - 1) % mark_factor != 0 || (w->d - 1) % mark_factor != 0) return -1;
    if (!force && !w->ground_dirty) return 0;
    w->ground_dirty = false;
    const int m = mark_factor, half = m / 2;
    const int W = (w->w - 1) / m + 1, D = (w->d - 1) / m + 1;
    const float c1 = w->cell * m, c2 = c1 * std::sqrt(2.f), far = 1e9f;
    // A mark cell covers the water cells nearest to it: seeds from the deep water and the paint of its block.
    std::vector<float> dist((size_t)W * D, far), surf((size_t)W * D, 0.f), paint((size_t)W * D, 0.f);
    for (int t = 0; t < w->tiles_x * w->tiles_z; t++) {
        const TileData* c = w->data[t].get();
        if (!c) continue;
        w->ForTile(t, [&](int x, int z, int i) {
            size_t k = (size_t)((z + half) / m) * W + (x + half) / m;
            paint[k] = std::max(paint[k], c->paint[i]);
            if (c->depth[i] <= kShoreDepth) return;
            float s = c->ground[i] + c->depth[i];
            if (dist[k] > 0) { dist[k] = 0; surf[k] = s; }
            else surf[k] = std::max(surf[k], s);
        });
    }
    // Two-pass chamfer distance to water, carrying the surface of the water each cell is closest to.
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
            float mk = 64.f;
            // Past 64 m the byte saturates anyway, so the ground is only looked up near water.
            if (dist[i] < 64.f) mk = dist[i] + 4.f * std::max(0.f, w->GroundOf(z * m * w->w + x * m) - surf[i]);
            out[i * 2] = (uint8_t)std::min(255.f, std::round(mk * 4.f));
            out[i * 2 + 1] = (uint8_t)std::round(std::clamp(paint[i], 0.f, 1.f) * 255.f);
        }
    });
    return 1;
}
