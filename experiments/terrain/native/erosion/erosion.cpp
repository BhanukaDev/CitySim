// Hydraulic (droplet) and thermal erosion, and lake finding, for CitySim heightmaps.
//
// Droplet erosion follows Hans Theobald Beyer, "Implementation of a method for hydraulic erosion" (2015), as implemented
// in Sebastian Lague's Hydraulic-Erosion (MIT, see LICENSE.md). Changes: heights are metres and slopes are unitless
// (height change / cell size), so the same settings work at any cell size; a droplet speeds up going downhill (the
// reference's speed update had the sign flipped); and droplets run on several threads.
//
// Threads: the map is cut into square tiles coloured in a repeating 3x3 pattern. A droplet starts in a tile and can't
// get further from it than its lifetime plus the erosion radius; tiles are wider than that and same-colour tiles have
// two tiles between them, so droplets started in different tiles of one colour never touch the same cells. Each round
// runs the nine colours one after another.
// Every tile gets its own random stream, so a run gives the same result whatever the thread count.

#include "erosion.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <functional>
#include <limits>
#include <queue>
#include <thread>
#include <vector>

namespace {

struct Rng {
    uint64_t s;
    explicit Rng(uint64_t seed) : s(seed ? seed : 0x9E3779B97F4A7C15ull) {}
    uint64_t next() {
        // splitmix64
        uint64_t z = (s += 0x9E3779B97F4A7C15ull);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return z ^ (z >> 31);
    }
    float unit() { return (next() >> 40) * (1.0f / 16777216.0f); } // [0, 1)
};

uint64_t mix(uint64_t a, uint64_t b) {
    Rng r(a * 0x100000001B3ull ^ (b + 0x632BE59BD9B4E019ull));
    return r.next();
}

int thread_count() {
    unsigned n = std::thread::hardware_concurrency();
    return (int)std::clamp(n == 0 ? 4u : n, 1u, 64u);
}

/// Runs body(i) for i in [0, count) on a few threads.
template <class F>
void parallel_for(int count, F&& body) {
    int threads = std::min(thread_count(), count);
    if (threads <= 1) {
        for (int i = 0; i < count; i++) body(i);
        return;
    }
    std::atomic<int> next{0};
    std::vector<std::thread> pool;
    pool.reserve(threads);
    for (int t = 0; t < threads; t++)
        pool.emplace_back([&] {
            for (int i; (i = next.fetch_add(1)) < count;) body(i);
        });
    for (auto& th : pool) th.join();
}

struct BrushCell {
    int dx, dz;
    float weight;
};

struct Eroder {
    float* h;
    int w, d;
    float cell;
    CsErosionParams p;
    std::vector<BrushCell> brush;
    // Cells from the border over which erosion fades in. The border is the base level: droplets carry sediment off the
    // map there, so eroding it would drop it forever and cut canyons back into the map.
    float margin;
    int reach; // brush radius in cells

    float edge_weight(int x, int z) const {
        int dist = std::min(std::min(x, z), std::min(w - 1 - x, d - 1 - z));
        return std::clamp(dist / margin, 0.0f, 1.0f);
    }

    /// Height and gradient (metres per cell) at a position inside the map, bilinear.
    void sample(float x, float z, float& height, float& gx, float& gz) const {
        int ix = (int)x, iz = (int)z;
        float u = x - ix, v = z - iz;
        const float* r0 = h + (size_t)iz * w + ix;
        const float* r1 = r0 + w;
        float h00 = r0[0], h10 = r0[1], h01 = r1[0], h11 = r1[1];
        gx = (h10 - h00) * (1 - v) + (h11 - h01) * v;
        gz = (h01 - h00) * (1 - u) + (h11 - h10) * u;
        height = h00 * (1 - u) * (1 - v) + h10 * u * (1 - v) + h01 * (1 - u) * v + h11 * u * v;
    }

    void droplet(Rng& rng, float x, float z) const {
        float dx = 0, dz = 0, speed = 1, water = 1, sediment = 0;
        const float maxX = (float)(w - 1) - 1e-3f, maxZ = (float)(d - 1) - 1e-3f;
        for (int step = 0; step < p.max_lifetime; step++) {
            int ix = (int)x, iz = (int)z;
            float u = x - ix, v = z - iz;
            float height, gx, gz;
            sample(x, z, height, gx, gz);

            dx = dx * p.inertia - gx * (1 - p.inertia);
            dz = dz * p.inertia - gz * (1 - p.inertia);
            float len = std::sqrt(dx * dx + dz * dz);
            if (len < 1e-6f) {
                // Dead flat: wander off in a random direction rather than stopping.
                float a = rng.unit() * 6.2831853f;
                dx = std::cos(a);
                dz = std::sin(a);
            } else {
                dx /= len;
                dz /= len;
            }
            x += dx;
            z += dz;
            if (x < 0 || z < 0 || x >= maxX || z >= maxZ) break;

            float newHeight, ngx, ngz;
            sample(x, z, newHeight, ngx, ngz);
            float dh = newHeight - height;          // metres, negative downhill
            float slope = -dh / cell;               // unitless, positive downhill
            float capacity = std::max(slope, p.min_slope) * speed * water * p.capacity * cell;

            size_t i00 = (size_t)iz * w + ix;
            if (sediment > capacity || dh > 0) {
                // Uphill: fill the pit behind us (up to the step). Otherwise drop part of the excess.
                float amount = dh > 0 ? std::min(dh, sediment) : (sediment - capacity) * p.deposit_speed;
                sediment -= amount;
                h[i00] += amount * (1 - u) * (1 - v);
                h[i00 + 1] += amount * u * (1 - v);
                h[i00 + w] += amount * (1 - u) * v;
                h[i00 + w + 1] += amount * u * v;
            } else {
                // Never take more than the drop in front of us, so the droplet can't dig a hole it then falls into.
                float amount = std::min({(capacity - sediment) * p.erode_speed, -dh, p.max_erode_depth});
                // Weights are normalised over the whole brush, so near the border (edge weight < 1) less is taken.
                float total = 0, taken = 0;
                for (const auto& b : brush) total += b.weight;
                float k = amount / total;
                if (edge_weight(ix, iz) >= 1 && std::min(std::min(ix, iz), std::min(w - 1 - ix, d - 1 - iz)) > margin + reach) {
                    // Well inside the map: the whole brush is in bounds at full weight.
                    for (const auto& b : brush) h[i00 + (ptrdiff_t)b.dz * w + b.dx] -= k * b.weight;
                    taken = amount;
                } else {
                    for (const auto& b : brush) {
                        int bx = ix + b.dx, bz = iz + b.dz;
                        if (bx < 0 || bz < 0 || bx >= w || bz >= d) continue;
                        float take = k * b.weight * edge_weight(bx, bz);
                        h[(size_t)bz * w + bx] -= take;
                        taken += take;
                    }
                }
                sediment += taken;
            }

            // Gains speed going down, loses it going up; a little drag keeps it bounded on long slopes.
            speed = std::sqrt(std::max(0.0f, speed * speed * 0.98f + slope * p.gravity));
            water *= 1 - p.evaporate_speed;
            if (water < 0.01f) break;
        }
    }
};

/// One iteration of thermal erosion: material slides from each cell to lower neighbours where the slope is above the
/// talus angle. Pairwise and symmetric, so it conserves material and each cell can be computed on its own.
void thermal_step(const float* src, float* dst, int w, int d, float tan_talus, float cell, float rate) {
    static const int ox[8] = {1, -1, 0, 0, 1, 1, -1, -1};
    static const int oz[8] = {0, 0, 1, -1, 1, -1, 1, -1};
    static const float dist[8] = {1, 1, 1, 1, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f};
    float limit[8];
    for (int k = 0; k < 8; k++) limit[k] = tan_talus * dist[k] * cell;
    // A cell can lose to all eight neighbours at once; 1/16 of the excess per pair keeps that stable.
    float share = rate / 16.0f;
    int rows = d;
    int chunk = 32;
    parallel_for((rows + chunk - 1) / chunk, [&](int c) {
        int z0 = c * chunk, z1 = std::min(rows, z0 + chunk);
        for (int z = z0; z < z1; z++)
            for (int x = 0; x < w; x++) {
                size_t i = (size_t)z * w + x;
                float hi = src[i], delta = 0;
                for (int k = 0; k < 8; k++) {
                    int nx = x + ox[k], nz = z + oz[k];
                    if (nx < 0 || nz < 0 || nx >= w || nz >= d) continue;
                    float diff = src[(size_t)nz * w + nx] - hi;
                    float ex = std::fabs(diff) - limit[k];
                    if (ex > 0) delta += diff > 0 ? ex * share : -ex * share;
                }
                dst[i] = hi + delta;
            }
    });
}

} // namespace

namespace {

constexpr uint32_t kNone = 0xFFFFFFFFu;
constexpr float kInfinity = std::numeric_limits<float>::infinity();
const int kOx[8] = {1, -1, 0, 0, 1, 1, -1, -1};
const int kOz[8] = {0, 0, 1, -1, 1, -1, 1, -1};

/// Priority-Flood (Barnes et al. 2014) from the map border and the sea (ground below sea_level connected to the
/// border). Writes the depression-filled surface to filled (may alias nothing else) and, if parent isn't null, the cell
/// each cell was reached from (kNone for the border and the sea): following parents leads out of every depression over
/// its spill point, downhill to the edge. If order isn't null it gets the cells in the order they were taken (every
/// cell's parent comes before it), followed by kNone; the sea's inner cells are never taken. Returns false if cancelled.
/// border_level (optional, per cell, only read on the border) is for a window of a bigger map, where the border isn't
/// an edge water runs off: only border cells where water leaves the window seed the flood, filled to their level there
/// (a lake reaching past the window keeps its spill height). NaN = not a seed: the cell fills like any other. A seed
/// in a lake (level above the ground) waits until just after its level: if the lake's own spill point is in the
/// window, the flood reaches the lake through it first and the lake drains there, as on the whole map; otherwise the
/// seed takes what's left and the lake drains out of the window.
template <class Cancelled, class Report>
bool priority_flood(const float* heights, int width, int depth, float sea_level, float* filled, uint32_t* parent,
                    uint32_t* order, Cancelled&& cancelled, Report&& report, const float* border_level = nullptr) {
    const size_t n = (size_t)width * depth;
    std::memcpy(filled, heights, n * sizeof(float));
    if (parent) std::fill(parent, parent + n, kNone);
    if (order) std::fill(order, order + n, kNone);
    std::vector<uint8_t> closed(n, 0);

    struct Node {
        float h;
        uint32_t i;
        bool operator>(const Node& o) const { return h > o.h; }
    };
    std::priority_queue<Node, std::vector<Node>, std::greater<Node>> open;
    std::queue<uint32_t> pit;
    constexpr uint32_t kLateSeed = 1u << 31; // on a Node's index: a lake seed, not closed until it's taken
    auto border = [&](int x, int z) { return x == 0 || z == 0 || x == width - 1 || z == depth - 1; };

    // The sea drains, so it's closed from the start, and its shore seeds the flood.
    {
        std::vector<uint32_t> stack;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) {
                if (!border(x, z)) continue;
                size_t i = (size_t)z * width + x;
                if (heights[i] < sea_level && !closed[i] && !(border_level && std::isnan(border_level[i]))) {
                    closed[i] = 1;
                    stack.push_back((uint32_t)i);
                }
            }
        while (!stack.empty()) {
            uint32_t i = stack.back();
            stack.pop_back();
            int x = i % width, z = i / width;
            bool shore = false;
            for (int k = 0; k < 8; k++) {
                int nx = x + kOx[k], nz = z + kOz[k];
                if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                size_t j = (size_t)nz * width + nx;
                if (heights[j] < sea_level) {
                    if (!closed[j]) { closed[j] = 1; stack.push_back((uint32_t)j); }
                } else shore = true;
            }
            if (shore) open.push({heights[i], i});
        }
    }
    for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++) {
            if (!border(x, z)) continue;
            size_t i = (size_t)z * width + x;
            if (closed[i] || (border_level && std::isnan(border_level[i]))) continue;
            if (border_level && border_level[i] > heights[i]) {
                open.push({std::nextafter(border_level[i], kInfinity), (uint32_t)i | kLateSeed});
                continue;
            }
            closed[i] = 1;
            open.push({filled[i], (uint32_t)i});
        }

    // Always grow from the lowest open cell; a neighbour lower than it is in a depression and is raised to its level
    // (and handled first, through the plain queue).
    size_t done = 0;
    while (!open.empty() || !pit.empty()) {
        uint32_t c;
        if (!pit.empty()) { c = pit.front(); pit.pop(); }
        else {
            c = open.top().i;
            open.pop();
            if (c & kLateSeed) {
                c &= ~kLateSeed;
                if (closed[c]) continue; // reached through the window first
                closed[c] = 1;
                filled[c] = std::max(filled[c], border_level[c]);
            }
        }
        if (order) order[done] = c;
        if ((++done & 0xFFFFF) == 0) {
            if (cancelled()) return false;
            report((float)done / (float)n);
        }
        int x = c % width, z = c / width;
        float level = filled[c];
        for (int k = 0; k < 8; k++) {
            int nx = x + kOx[k], nz = z + kOz[k];
            if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
            size_t j = (size_t)nz * width + nx;
            if (closed[j]) continue;
            closed[j] = 1;
            if (parent) parent[j] = c;
            if (filled[j] <= level) { filled[j] = level; pit.push((uint32_t)j); }
            else open.push({filled[j], (uint32_t)j});
        }
    }
    return true;
}

/// Drains shallow depressions by cutting a channel from each pit (a local low point under the fill) along its flood
/// path, over the spill point and down to lower ground, descending all the way. Only when no cell on the path needs
/// cutting by more than max_cut metres; deeper depressions stay closed (lakes).
///
/// The flood path only moves in 8 directions, so it's smoothed into a curve first, and the channel is carved along the
/// curve with a flat bed about two cells wide and banks that rise as a parabola (about 30 degrees at their steepest
/// within the cut), so it reads as a small stream valley rather than a trench. Returns the number of pits drained, or
/// -1 if cancelled.
// Meander of drain channels, in cells: sideways swing, wave length, and the distance over which it fades in at each end.
constexpr float kMeanderAmplitude = 2.0f, kMeanderWavelength = 45.0f, kMeanderTaper = 12.0f;
// Rounding passes where channel banks meet the ground.
constexpr int kShoulderPasses = 3;
// 1-2-1 passes over the channel path: 24 rounds bends over roughly 3-4 cells either side.
constexpr int kSmoothPasses = 24;

template <class Cancelled, class Report>
int breach(float* h, int width, int depth, float cell_size, float sea_level, float max_cut, Cancelled&& cancelled,
           Report&& report) {
    const size_t n = (size_t)width * depth;
    std::vector<float> filled(n);
    std::vector<uint32_t> parent(n);
    if (!priority_flood(h, width, depth, sea_level, filled.data(), parent.data(), nullptr, cancelled,
                        [&](float f) { report(0.7f * f); }))
        return -1;
    // Keeps the channel strictly descending (1 mm per cell), so water finds its way along it.
    const float step = 1e-3f;
    // Cells within this distance of the curve get the bed height, so the bed is a connected band.
    const float bed = 1.0f;
    // Bank rise per cell² beyond the bed: slope tan(30°) three cells out.
    const float a = std::tan(30.0f * 3.14159265f / 180.0f) * cell_size / 6.0f;
    // How far the banks reach. The ground beside the path can stand much higher than the cut the path needs (a notch in
    // a ridge), so the banks go well past that; cutting them off sooner leaves a cliff.
    const float reach = bed + 12.0f;

    struct Point {
        float x, z, t;
    };
    std::vector<Point> path, smooth;
    // Every channel's surface (bed plus banks), blended into the ground once at the end: many hollows drain along the
    // same route, and rounding the banks once per channel would lower that route again each time.
    std::vector<float> surface(n, std::numeric_limits<float>::infinity());
    bool any = false;
    int drained = 0;
    for (size_t s = 0; s < n; s++) {
        if ((s & 0xFFFF) == 0) {
            if (cancelled()) return -1;
            report(0.7f + 0.3f * (float)s / (float)n);
        }
        if (!(filled[s] > h[s] + 1e-3f)) continue;
        int x = s % width, z = s / width;
        bool pit = true;
        for (int k = 0; k < 8 && pit; k++) {
            int nx = x + kOx[k], nz = z + kOz[k];
            if (nx >= 0 && nz >= 0 && nx < width && nz < depth && h[(size_t)nz * width + nx] < h[s]) pit = false;
        }
        if (!pit) continue;

        path.clear();
        path.push_back({(float)x, (float)z, h[s]});
        float target = h[s];
        bool ok = true, cut = false;
        for (uint32_t k = parent[s]; k != kNone; k = parent[k]) {
            target -= step;
            path.push_back({(float)(k % width), (float)(k / width), target});
            if (h[k] <= target) break; // lower ground (or another lake): the water gets out from here
            if (h[k] - target > max_cut) { ok = false; break; }
            cut = true;
        }
        if (!ok || !cut) continue;

        // Round off the grid's corners: 1-2-1 passes over the points, ends fixed.
        for (int pass = 0; pass < kSmoothPasses && path.size() > 2; pass++) {
            smooth = path;
            for (size_t i = 1; i + 1 < path.size(); i++) {
                smooth[i].x = 0.25f * path[i - 1].x + 0.5f * path[i].x + 0.25f * path[i + 1].x;
                smooth[i].z = 0.25f * path[i - 1].z + 0.5f * path[i].z + 0.25f * path[i + 1].z;
            }
            path.swap(smooth);
        }

        // Meander: push the curve sideways along a slow wave, fading to nothing at both ends so it still starts in the
        // pit and ends on lower ground.
        {
            size_t m = path.size();
            std::vector<float> along(m, 0.0f);
            for (size_t i = 1; i < m; i++)
                along[i] = along[i - 1] + std::hypot(path[i].x - path[i - 1].x, path[i].z - path[i - 1].z);
            float total = along[m - 1];
            float phase = (float)(mix(s, 0x5EED) & 0xFFFF) / 65536.0f * 6.2831853f;
            smooth = path;
            for (size_t i = 1; i + 1 < m; i++) {
                float tx = path[i + 1].x - path[i - 1].x, tz = path[i + 1].z - path[i - 1].z;
                float len = std::hypot(tx, tz);
                if (len < 1e-4f) continue;
                float taper = std::min({1.0f, along[i] / kMeanderTaper, (total - along[i]) / kMeanderTaper});
                float off = kMeanderAmplitude * taper * std::sin(along[i] / kMeanderWavelength * 6.2831853f + phase);
                smooth[i].x += -tz / len * off;
                smooth[i].z += tx / len * off;
            }
            path.swap(smooth);
        }

        // This channel's bed and banks into the shared surface.
        any = true;
        for (size_t i = 0; i + 1 < path.size(); i++) {
            const Point& p0 = path[i];
            const Point& p1 = path[i + 1];
            float len = std::hypot(p1.x - p0.x, p1.z - p0.z);
            int samples = std::max(1, (int)std::ceil(len));
            for (int j = 0; j < samples; j++) {
                float u = (float)j / samples;
                float cx = p0.x + (p1.x - p0.x) * u, cz = p0.z + (p1.z - p0.z) * u, ct = p0.t + (p1.t - p0.t) * u;
                int x0 = std::max(1, (int)std::floor(cx - reach)), x1 = std::min(width - 2, (int)std::ceil(cx + reach));
                int z0 = std::max(1, (int)std::floor(cz - reach)), z1 = std::min(depth - 2, (int)std::ceil(cz + reach));
                for (int qz = z0; qz <= z1; qz++)
                    for (int qx = x0; qx <= x1; qx++) {
                        float d = std::hypot(qx - cx, qz - cz);
                        if (d > reach) continue;
                        float e = std::max(0.0f, d - bed);
                        // The bed dips slightly toward the curve, so it's never dead flat across.
                        float& v = surface[(size_t)qz * width + qx];
                        v = std::min(v, ct + a * e * e + step * d);
                    }
            }
        }
        drained++;
    }
    if (!any) return drained;
    for (size_t i = 0; i < n; i++) h[i] = std::min(h[i], surface[i]);
    // Round the crease where the banks meet the ground: near channels, a cell above the average of its 8 neighbours is
    // lowered to it. That rounds convex edges (bank tops) and leaves the bed and bank feet alone, and a cell lowered to
    // its neighbours' average is never below all of them, so no new pits.
    for (int pass = 0; pass < kShoulderPasses; pass++)
        for (int z = 1; z < depth - 1; z++)
            for (int x = 1; x < width - 1; x++) {
                size_t i = (size_t)z * width + x;
                if (!std::isfinite(surface[i])) continue;
                float sum = 0;
                for (int k = 0; k < 8; k++) sum += h[(size_t)(z + kOz[k]) * width + (x + kOx[k])];
                h[i] = std::min(h[i], sum * 0.125f);
            }
    return drained;
}

} // namespace

namespace {

// Ground mask encodings (see erosion.h): shore and gully distances in metres over these ranges, wear and deposits on a
// log scale.
constexpr float kShoreNear = -16.0f, kShoreFar = 64.0f;
constexpr float kGullyNear = -8.0f, kGullyFar = 24.0f;
constexpr float kLogScale = 32.0f;
// Shore distance: metres added per metre the ground stands above the water it's measured from, so a high bank next to
// a lake doesn't count as its beach.
constexpr float kShoreClimb = 4.0f;
const float kInf = std::numeric_limits<float>::infinity();
constexpr float kRiverBank = 6.0f;

// Milliseconds spent in each stage of the last find_water call (flood, lakes, flow, masks), for cs_last_find_water_ms.
float g_stage_ms[4] = {};
using Clock = std::chrono::steady_clock;
float ms_since(Clock::time_point& t) {
    auto now = Clock::now();
    float ms = std::chrono::duration<float, std::milli>(now - t).count();
    t = now;
    return ms;
}

uint8_t encode(float v, float near, float far) {
    return (uint8_t)std::lround(std::clamp((far - v) / (far - near), 0.0f, 1.0f) * 255.0f);
}

uint8_t encode_log(float v) { return (uint8_t)std::lround(std::clamp(kLogScale * std::log2(1.0f + v), 0.0f, 255.0f)); }

/// Two-pass chamfer distance (8 neighbours, metres). Cells start at their seed distance (infinity = not a source) and
/// take their source's level along with its distance, when level isn't null.
void chamfer(float* dist, float* level, int width, int depth, float cell_size) {
    const float straight = cell_size, diagonal = cell_size * 1.41421356f;
    auto relax = [&](size_t i, size_t j, float step) {
        float c = dist[j] + step;
        if (c < dist[i]) {
            dist[i] = c;
            if (level) level[i] = level[j];
        }
    };
    for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++) {
            size_t i = (size_t)z * width + x;
            if (x > 0) relax(i, i - 1, straight);
            if (z > 0) {
                relax(i, i - width, straight);
                if (x > 0) relax(i, i - width - 1, diagonal);
                if (x < width - 1) relax(i, i - width + 1, diagonal);
            }
        }
    for (int z = depth - 1; z >= 0; z--)
        for (int x = width - 1; x >= 0; x--) {
            size_t i = (size_t)z * width + x;
            if (x < width - 1) relax(i, i + 1, straight);
            if (z < depth - 1) {
                relax(i, i + width, straight);
                if (x < width - 1) relax(i, i + width + 1, diagonal);
                if (x > 0) relax(i, i + width - 1, diagonal);
            }
        }
}

/// Separable 1-2-1 blur of a byte map, in place: rows, then 64-wide column strips, in parallel.
void blur(uint8_t* v, int width, int depth, int passes) {
    constexpr int kStrip = 64;
    for (int p = 0; p < passes; p++) {
        parallel_for(depth, [&](int z) {
            std::vector<uint16_t> row(width);
            uint8_t* r = v + (size_t)z * width;
            for (int x = 0; x < width; x++)
                row[x] = (uint16_t)(r[std::max(x - 1, 0)] + 2 * r[x] + r[std::min(x + 1, width - 1)]);
            for (int x = 0; x < width; x++) r[x] = (uint8_t)((row[x] + 2) / 4);
        });
        parallel_for((width + kStrip - 1) / kStrip, [&](int strip) {
            int x0 = strip * kStrip, w = std::min(kStrip, width - x0);
            // Previous row's original values, so each row blurs from unblurred neighbours.
            std::vector<uint8_t> prev(v + x0, v + x0 + w);
            for (int z = 0; z < depth; z++) {
                uint8_t* r = v + (size_t)z * width + x0;
                const uint8_t* next = v + (size_t)std::min(z + 1, depth - 1) * width + x0;
                for (int x = 0; x < w; x++) {
                    uint8_t cur = r[x];
                    r[x] = (uint8_t)((prev[x] + 2 * cur + next[x] + 2) / 4);
                    prev[x] = cur;
                }
            }
        });
    }
}

/// Flow state kept per cell between searches (cs_find_water's flow_state): catchment area as 16-bit log2 x 2048 (0.03 %
/// steps, 1 m² .. 4e9 m²) in bits 0-15, the gully bit in bit 16, and the flood parent's direction (bits 17-19, kOx/kOz
/// index) with bit 20 set when there is one: on flats (lakes, filled pits) water follows it; and how far the flood
/// raised the cell (bits 21-31, cm, up to 20.47 m), so filled pits that aren't lakes route the same from outside a
/// window. The sediment a cell passes on is its capacity, area x slope, so it needn't be kept.
constexpr float kAreaScale = 2048.0f;
constexpr uint32_t kHasParent = 1u << 20;
uint32_t pack_flow(float area, uint8_t channel, int parent_dir, float raised) {
    float v = area > 1.0f ? std::log2(area) * kAreaScale : 0.0f;
    uint32_t cm = (uint32_t)std::lround(std::clamp(raised * 100.0f, 0.0f, 2047.0f));
    return (uint32_t)std::lround(std::min(v, 65535.0f)) | (channel ? 1u << 16 : 0u) |
           (parent_dir >= 0 ? kHasParent | (uint32_t)parent_dir << 17 : 0u) | cm << 21;
}
float flow_raised(uint32_t f) { return (float)(f >> 21) * 0.01f; }
float flow_area(uint32_t f) { return std::exp2((float)(f & 0xFFFFu) / kAreaScale); }
bool flow_channel(uint32_t f) { return (f >> 16) & 1u; }
int flow_parent_dir(uint32_t f) { return f & kHasParent ? (int)((f >> 17) & 7u) : -1; }

/// What runs into a window from the cells just outside it (see find_water_window).
struct Inflow {
    struct Cell {
        uint32_t i;       // window cell
        float area;       // m²
        float sediment;   // carried at capacity
        uint8_t channel;  // a gully continues into it
    };
    std::vector<Cell> cells;
};

/// The ground masks (see cs_find_water in erosion.h). filled/parent/order come from priority_flood; order is
/// overwritten with the result. water is the lake level per cell (NaN = dry).
template <class Cancelled>
bool ground_masks(const float* heights, const float* filled, std::vector<uint32_t>& parent_buffer, uint32_t* order,
                  const float* water, int width, int depth, float sea_level, const CsGroundParams& p,
                  Cancelled&& cancelled, const Inflow* inflow = nullptr, uint32_t* flow_state = nullptr) {
    const size_t n = (size_t)width * depth;
    const float cs = p.cell_size, cellArea = cs * cs;
    const uint32_t* parent = parent_buffer.data();
    auto sea = [&](size_t i) { return heights[i] < sea_level && !(filled[i] > heights[i] + 1e-3f); };

    // Flow (multiple flow directions, Quinn/Holmgren): each cell splits its water between its lower neighbours by
    // slope², so it spreads on open slopes (a single direction leaves parallel grid lines). Once gully_min_area has
    // gathered, it all goes to the steepest neighbour, so gullies stay narrow. On flats (filled lakes and pits) it follows its flood parent out over the spill point. The flood takes
    // cells in rising order of the fill, so walking it backwards reaches every cell after all the cells above it.
    // Sediment is carried at capacity, area x slope; where the slope eases the excess settles (fans at the foot of
    // slopes, deltas where streams meet a lake or the sea).
    const float diagonal = cs * 1.41421356f;
    auto t = Clock::now();
    std::vector<float> area(n, 0.0f), sediment(n, 0.0f);
    // Wear (stream power, sqrt(area) x slope) as a log-scaled byte, set as each cell is reached (its area is complete
    // then): no slope array (268 MB at 8193²).
    std::vector<uint8_t> wear(n);
    // Gully beds start where catchment x slope² passes gully_min_area x kGullySlope² (channel heads need more catchment
    // on gentle ground), or where gully_min_area gathers in a hollow (concave ground: a stream bed or a drain channel).
    // They follow the steepest path down (water spreading over a flat bed would otherwise drop under the threshold and
    // leave a dashed line), and end in a fan where they run out onto open, gentle ground. Without the hollow test a
    // smooth plain sprouts parallel gullies straight down its slope.
    constexpr float kGullySlope = 0.2f, kGullyEnd = 0.1f;
    // Hollow: ground 2 cells away averages this much (x cell size) above the cell.
    constexpr float kHollow = 0.02f;
    const float channelHead = p.gully_min_area * kGullySlope * kGullySlope;
    auto hollow = [&](int x, int z) {
        float sum = 0;
        int k = 0;
        for (int d = 0; d < 8; d++) {
            int nx = x + 2 * kOx[d], nz = z + 2 * kOz[d];
            if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
            sum += heights[(size_t)nz * width + nx];
            k++;
        }
        return k > 0 && sum / k - heights[(size_t)z * width + x] >= kHollow * cs;
    };
    std::vector<uint8_t> channel(n, 0);
    if (inflow)
        for (const auto& in : inflow->cells) {
            area[in.i] += in.area;
            sediment[in.i] += in.sediment;
            channel[in.i] |= in.channel;
        }
    size_t count = 0;
    while (count < n && order[count] != kNone) count++;
    for (size_t k = count; k-- > 0;) {
        uint32_t i = order[k];
        area[i] += cellArea;
        int x = i % width, z = i / width;
        float drops[8], total = 0, s = 0;
        int steepest = -1;
        for (int d = 0; d < 8; d++) {
            drops[d] = 0;
            int nx = x + kOx[d], nz = z + kOz[d];
            if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
            float drop = filled[i] - filled[(size_t)nz * width + nx];
            if (!(drop > 0)) continue;
            drops[d] = drop / (d < 4 ? cs : diagonal);
            if (drops[d] > s) { s = drops[d]; steepest = d; }
        }
        wear[i] = encode_log(std::sqrt(area[i]) * s);
        float power = area[i] * s * s;
        if (power >= channelHead || (area[i] >= p.gully_min_area && hollow(x, z))) channel[i] = 1;
        else if (channel[i] && area[i] < p.river_min_area && power < channelHead * kGullyEnd) channel[i] = 0;
        float share[8];
        bool single = area[i] >= p.gully_min_area;
        for (int d = 0; d < 8; d++) {
            float r = drops[d] / s;
            share[d] = single ? (d == steepest ? 1.0f : 0.0f) : r * r * (d < 4 ? 1.0f : 0.70710678f);
            total += share[d];
        }
        if (channel[i]) {
            if (steepest >= 0) channel[(size_t)(z + kOz[steepest]) * width + (x + kOx[steepest])] = 1;
            else if (parent[i] != kNone) channel[parent[i]] = 1;
        }
        float capacity = area[i] * s;
        float settled = std::max(0.0f, sediment[i] - capacity);
        if (total > 0) {
            for (int d = 0; d < 8; d++) {
                if (share[d] == 0) continue;
                size_t j = (size_t)(z + kOz[d]) * width + (x + kOx[d]);
                float f = share[d] / total;
                area[j] += area[i] * f;
                sediment[j] += capacity * f;
            }
        } else if (uint32_t j = parent[i]; j != kNone) {
            area[j] += area[i];
            sediment[j] += sediment[i] - settled;
        } else if (!(heights[i] < sea_level)) {
            settled = 0; // off the map edge; on the sea floor it settles
        }
        sediment[i] = settled / cellArea; // from here on: what settled here, in cells' worth
    }
    if (flow_state)
        parallel_for(depth, [&](int z) {
            for (size_t i = (size_t)z * width, end = i + width; i < end; i++) {
                int dir = -1;
                if (uint32_t pi = parent[i]; pi != kNone) {
                    int dx = (int)(pi % width) - (int)(i % width), dz = (int)(pi / width) - z;
                    for (int d = 0; d < 8; d++)
                        if (kOx[d] == dx && kOz[d] == dz) dir = d;
                }
                flow_state[i] = pack_flow(area[i], channel[i], dir, filled[i] - heights[i]);
            }
        });
    parent_buffer = {}; // not needed from here on (268 MB at 8193²)
    g_stage_ms[2] = ms_since(t);
    if (cancelled()) return false;

    // Deposits as log-scaled bytes; both blurred into soft bands.
    std::vector<uint8_t> deposit(n);
    parallel_for(depth, [&](int z) {
        for (size_t i = (size_t)z * width, end = i + width; i < end; i++) deposit[i] = encode_log(sediment[i]);
    });
    blur(wear.data(), width, depth, 1);
    blur(deposit.data(), width, depth, 2);
    if (cancelled()) return false;

    // Distances. Rivers and gullies start negative (inside the channel) by a half width growing with their catchment.
    // Buffers reused: shore in sediment's, the river level in area's (each cell reads its area before writing it).
    std::vector<float>& shore = sediment;
    std::vector<float>& level = area;
    std::vector<float> gully(n);
    parallel_for(depth, [&](int z) {
    for (size_t i = (size_t)z * width, end = i + width; i < end; i++) {
        float a = area[i];
        shore[i] = gully[i] = kInf;
        // Only rivers: lakes and the sea are simulated water now, and the terrain shader takes their shores from the
        // water sim (a hollow nobody put a lake source in stays dry ground).
        if (a >= p.river_min_area && std::isnan(water[i]) && !sea(i)) {
            // Half width grows with the catchment; small rivers start a few metres out, so their banks are narrower
            // than a lake's beach.
            shore[i] = kRiverBank - std::clamp(3.0f * std::sqrt(a / 1e6f), 1.5f, 20.0f);
            level[i] = filled[i];
        }
        if (channel[i] && std::isnan(water[i]) && !sea(i))
            gully[i] = -std::clamp(0.015f * std::sqrt(a), 2.5f, 6.0f); // at least 2.5, or a diagonal line reads as dashes
    }
    });
    chamfer(shore.data(), level.data(), width, depth, cs);
    chamfer(gully.data(), nullptr, width, depth, cs);
    if (cancelled()) return false;

    parallel_for(depth, [&](int z) {
    for (size_t i = (size_t)z * width, end = i + width; i < end; i++) {
        float s = shore[i];
        if (std::isfinite(s)) s += kShoreClimb * std::max(0.0f, heights[i] - level[i]);
        order[i] = (uint32_t)encode(s, kShoreNear, kShoreFar) | (uint32_t)encode(gully[i], kGullyNear, kGullyFar) << 8 |
                   (uint32_t)wear[i] << 16 | (uint32_t)deposit[i] << 24;
    }
    });
    g_stage_ms[3] = ms_since(t);
    return true;
}

using MakeInflow = std::function<void(const float* filled, Inflow& inflow)>;

/// Lakes and (with ground) the ground masks. For a window of a bigger map, border_level seeds the flood (see
/// priority_flood) and make_inflow adds what flows in from outside, once the fill is known. flow_state (optional) gets
/// each cell's area and gully bit (pack_flow).
int32_t find_water(const float* heights, int32_t width, int32_t depth, float sea_level, float min_depth,
                   int32_t min_cells, const CsGroundParams* ground_params, float* water_level, uint32_t* ground,
                   CsProgress* progress, uint32_t* flow_state = nullptr, const float* border_level = nullptr,
                   const MakeInflow* make_inflow = nullptr) {
    if (!heights || !water_level || width < 3 || depth < 3) return -1;
    if (ground && (!ground_params || !(ground_params->cell_size > 0))) return -1;
    auto cancelled = [&] { return progress && __atomic_load_n(&progress->cancel, __ATOMIC_RELAXED) != 0; };
    auto report = [&](float f) { if (progress) __atomic_store(&progress->progress, &f, __ATOMIC_RELAXED); };

    const size_t n = (size_t)width * depth;
    float floodShare = ground ? 0.5f : 0.8f;
    // The fill is built in the output buffer and turned into lake levels. The ground masks need the fill too, so then it
    // gets its own buffer (and the flood order is built in the ground buffer).
    std::vector<float> fillCopy;
    std::vector<uint32_t> parent;
    std::fill(g_stage_ms, g_stage_ms + 4, 0.0f);
    auto t = Clock::now();
    if (ground) { fillCopy.resize(n); parent.resize(n); }
    float* filled = ground ? fillCopy.data() : water_level;
    if (!priority_flood(heights, width, depth, sea_level, filled, ground ? parent.data() : nullptr, ground, cancelled,
                        [&](float f) { report(floodShare * f); }, border_level))
        return 0;
    g_stage_ms[0] = ms_since(t);
    if (ground) std::memcpy(water_level, filled, n * sizeof(float));
    std::vector<uint8_t> closed(n, 0);

    // Lakes: connected cells the fill raised. Keep the deep and large enough ones.
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const float eps = 1e-3f;
    std::vector<uint32_t> cells, stack;
    int lakes = 0;
    for (size_t s = 0; s < n; s++) {
        if (closed[s]) continue;
        if (!(water_level[s] > heights[s] + eps)) { closed[s] = 1; continue; }
        cells.clear();
        stack.push_back((uint32_t)s);
        closed[s] = 1;
        float deepest = 0;
        while (!stack.empty()) {
            uint32_t i = stack.back();
            stack.pop_back();
            cells.push_back(i);
            deepest = std::max(deepest, water_level[i] - heights[i]);
            int x = i % width, z = i / width;
            for (int k = 0; k < 8; k++) {
                int nx = x + kOx[k], nz = z + kOz[k];
                if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                size_t j = (size_t)nz * width + nx;
                if (closed[j] || !(water_level[j] > heights[j] + eps)) continue;
                closed[j] = 1;
                stack.push_back((uint32_t)j);
            }
        }
        bool keep = deepest >= min_depth && (int64_t)cells.size() >= (int64_t)min_cells;
        if (keep) lakes++;
        else for (uint32_t i : cells) water_level[i] = nan;
    }
    // Everything the fill didn't raise is dry.
    for (size_t i = 0; i < n; i++)
        if (!(water_level[i] > heights[i] + eps)) water_level[i] = nan;
    closed = {};
    g_stage_ms[1] = ms_since(t);
    if (ground) {
        report(0.6f);
        if (cancelled()) return lakes;
        Inflow inflow;
        if (make_inflow) (*make_inflow)(filled, inflow);
        if (!ground_masks(heights, filled, parent, ground, water_level, width, depth, sea_level, *ground_params,
                          cancelled, make_inflow ? &inflow : nullptr, flow_state))
            return lakes;
    }
    report(1);
    return lakes;
}

/// find_water on the window [x0..x1] x [z0..z1] (inclusive) of a width x depth map, for re-searching after an edit
/// inside it. level and flow are the whole map's results from the last search (level NaN = dry); they're read on and
/// just outside the window's border: lakes crossing it keep their level, and the cells just outside pass their stored
/// area (and sediment at capacity, and gullies) to the window cells they drain into, split as ground_masks splits it.
/// Outputs are window-sized. What changed inside doesn't reach cells downstream of the window, and a lake cut by its
/// border is kept or dropped on the part inside: a full search puts that right.
int32_t find_water_window(const float* heights, int32_t width, int32_t depth, int32_t x0, int32_t z0, int32_t x1,
                          int32_t z1, float sea_level, float min_depth, int32_t min_cells, const CsGroundParams* gp,
                          const float* level, const uint32_t* flow, float* water_out, uint32_t* ground_out,
                          uint32_t* flow_out, CsProgress* progress) {
    if (!heights || !level || !flow || !gp || !(gp->cell_size > 0) || x0 < 0 || z0 < 0 || x1 >= width || z1 >= depth
        || x1 - x0 < 2 || z1 - z0 < 2)
        return -1;
    const int ww = x1 - x0 + 1, wd = z1 - z0 + 1;
    const size_t n = (size_t)ww * wd;
    const float nan = std::numeric_limits<float>::quiet_NaN();
    std::vector<float> local(n);
    for (int z = 0; z < wd; z++)
        std::memcpy(&local[(size_t)z * ww], heights + (size_t)(z0 + z) * width + x0, ww * sizeof(float));

    // Seeds: border cells water leaves the window from. On the map's edge (it runs off), in a lake crossing the border
    // (at its level), in the sea, or with a lower cell just outside (by the last search's fill). Elsewhere the border is a wall
    // that water fills up against: were every border cell a drain, a dam in a river would empty its lake out of the
    // window's upstream side instead of over the dam.
    // The last search's fill: a lake's level, else the ground plus what the flood raised it by (filled pits).
    auto oldFill = [&](int gx, int gz) {
        size_t g = (size_t)gz * width + gx;
        return level[g] > heights[g] ? level[g] : heights[g] + flow_raised(flow[g]);
    };
    std::vector<float> border(n, nan);
    int seeds = 0;
    auto seed = [&](int lx, int lz) {
        int gx = x0 + lx, gz = z0 + lz;
        size_t li = (size_t)lz * ww + lx, g = (size_t)gz * width + gx;
        float lv = level[g], fill = std::max(local[li], oldFill(gx, gz));
        bool exit = gx == 0 || gz == 0 || gx == width - 1 || gz == depth - 1 || local[li] < sea_level;
        if (!exit && !std::isnan(lv)) {
            // In a lake: an exit only where the lake drained out of the window last time (its flood parent is
            // outside); where water came in, it's a wall like any other.
            int d = flow_parent_dir(flow[g]), px = gx + (d >= 0 ? kOx[d] : 0), pz = gz + (d >= 0 ? kOz[d] : 0);
            exit = d < 0 || px < x0 || px > x1 || pz < z0 || pz > z1;
            if (!exit) return;
        }
        for (int k = 0; k < 8 && !exit; k++) {
            int nx = gx + kOx[k], nz = gz + kOz[k];
            if (nx >= x0 && nx <= x1 && nz >= z0 && nz <= z1) continue; // inside
            exit = oldFill(nx, nz) < fill;
        }
        if (exit) { border[li] = std::isnan(lv) ? local[li] : lv; seeds++; }
    };
    for (int x = 0; x < ww; x++) { seed(x, 0); seed(x, wd - 1); }
    for (int z = 1; z < wd - 1; z++) { seed(0, z); seed(ww - 1, z); }
    // Nowhere for water to leave (a window inside a closed basin): let every border cell drain, as a full map's does.
    if (seeds == 0)
        for (size_t i = 0; i < n; i++) border[i] = local[i];

    const float cs = gp->cell_size, diagonal = cs * 1.41421356f;
    MakeInflow inflow = [&](const float* filled, Inflow& in) {
        // Old fill outside the window: the lake level where there is one, else the ground.
        auto filledAt = [&](int gx, int gz) {
            if (gx >= x0 && gx <= x1 && gz >= z0 && gz <= z1) return filled[(size_t)(gz - z0) * ww + (gx - x0)];
            return oldFill(gx, gz);
        };
        auto visit = [&](int ox, int oz) {
            if (ox < 0 || oz < 0 || ox >= width || oz >= depth) return;
            size_t go = (size_t)oz * width + ox;
            uint32_t f = flow[go];
            if ((f & 0xFFFFu) == 0) return;
            float a = flow_area(f), fo = filledAt(ox, oz);
            // A cell the flood raised (lake, filled pit) sits on a flat, where water follows its flood parent. Tested on
            // what was stored, not on slopes: the stored fill is rounded to cm, and a flat must stay flat.
            bool flat = flow_raised(f) > 0 || level[go] > heights[go];
            float drops[8], s = 0, total = 0;
            int steepest = -1;
            for (int d = 0; d < 8; d++) {
                drops[d] = 0;
                int nx = ox + kOx[d], nz = oz + kOz[d];
                if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                float drop = fo - filledAt(nx, nz);
                if (!(drop > 0)) continue;
                drops[d] = drop / (d < 4 ? cs : diagonal);
                if (drops[d] > s) { s = drops[d]; steepest = d; }
            }
            if (flat || steepest < 0) {
                // Flat (a lake or filled pit): all of it goes to its flood parent, if that's inside.
                int d = flow_parent_dir(f), nx = ox + (d >= 0 ? kOx[d] : 0), nz = oz + (d >= 0 ? kOz[d] : 0);
                if (d >= 0 && nx >= x0 && nx <= x1 && nz >= z0 && nz <= z1)
                    in.cells.push_back({(uint32_t)((nz - z0) * ww + (nx - x0)), a, 0.0f, (uint8_t)flow_channel(f)});
                return;
            }
            bool single = a >= gp->gully_min_area;
            float share[8];
            for (int d = 0; d < 8; d++) {
                float r = drops[d] / s;
                share[d] = single ? (d == steepest ? 1.0f : 0.0f) : r * r * (d < 4 ? 1.0f : 0.70710678f);
                total += share[d];
            }
            float capacity = a * s;
            for (int d = 0; d < 8; d++) {
                int nx = ox + kOx[d], nz = oz + kOz[d];
                if (share[d] == 0 || nx < x0 || nx > x1 || nz < z0 || nz > z1) continue;
                float fr = share[d] / total;
                in.cells.push_back({(uint32_t)((nz - z0) * ww + (nx - x0)), a * fr, capacity * fr,
                                    (uint8_t)(flow_channel(f) && d == steepest)});
            }
        };
        for (int x = x0 - 1; x <= x1 + 1; x++) { visit(x, z0 - 1); visit(x, z1 + 1); }
        for (int z = z0; z <= z1; z++) { visit(x0 - 1, z); visit(x1 + 1, z); }
    };
    return find_water(local.data(), ww, wd, sea_level, min_depth, min_cells, gp, water_out, ground_out, progress,
                      flow_out, border.data(), &inflow);
}

} // namespace

CS_API int32_t cs_find_lakes(const float* heights, int32_t width, int32_t depth, float sea_level,
                             float min_depth, int32_t min_cells, float* water_level, CsProgress* progress) {
    return find_water(heights, width, depth, sea_level, min_depth, min_cells, nullptr, water_level, nullptr, progress);
}

CS_API int32_t cs_find_water(const float* heights, int32_t width, int32_t depth, float sea_level, float min_depth,
                             int32_t min_cells, const CsGroundParams* ground_params, float* water_level,
                             uint32_t* ground, uint32_t* flow_state, CsProgress* progress) {
    if (!ground) return -1;
    return find_water(heights, width, depth, sea_level, min_depth, min_cells, ground_params, water_level, ground,
                      progress, flow_state);
}

CS_API int32_t cs_find_water_window(const float* heights, int32_t width, int32_t depth, int32_t x0, int32_t z0,
                                    int32_t x1, int32_t z1, float sea_level, float min_depth, int32_t min_cells,
                                    const CsGroundParams* ground_params, const float* level, const uint32_t* flow,
                                    float* water_out, uint32_t* ground_out, uint32_t* flow_out, CsProgress* progress) {
    if (!water_out || !ground_out || !flow_out) return -1;
    return find_water_window(heights, width, depth, x0, z0, x1, z1, sea_level, min_depth, min_cells, ground_params,
                             level, flow, water_out, ground_out, flow_out, progress);
}

CS_API void cs_last_find_water_ms(float* out4) {
    if (out4) std::copy(g_stage_ms, g_stage_ms + 4, out4);
}

CS_API int32_t cs_erode(float* heights, int32_t width, int32_t depth, float cell_size,
                        const CsErosionParams* params, CsProgress* progress) {
    if (!heights || !params || width < 4 || depth < 4 || !(cell_size > 0)) return -1;
    const CsErosionParams& p = *params;
    auto cancelled = [&] { return progress && __atomic_load_n(&progress->cancel, __ATOMIC_RELAXED) != 0; };
    auto report = [&](float f) { if (progress) __atomic_store(&progress->progress, &f, __ATOMIC_RELAXED); };

    Eroder e{heights, width, depth, cell_size, p, {}, 1.0f, 0};
    e.p.max_lifetime = std::clamp(p.max_lifetime, 1, 512);
    e.p.inertia = std::clamp(p.inertia, 0.0f, 0.99f);
    e.p.evaporate_speed = std::clamp(p.evaporate_speed, 0.0f, 1.0f);
    if (!(e.p.max_erode_depth > 0)) e.p.max_erode_depth = std::numeric_limits<float>::max();

    float radius = std::clamp(p.erosion_radius, 0.5f, 16.0f);
    int r = (int)std::ceil(radius);
    for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++) {
            float wgt = radius - std::sqrt((float)(dx * dx + dz * dz));
            if (wgt > 0) e.brush.push_back({dx, dz, wgt});
        }
    // The brush is anchored at the droplet's cell corner; make sure it's never empty.
    if (e.brush.empty()) e.brush.push_back({0, 0, 1});
    e.margin = (float)(r + 8);
    e.reach = r;

    bool thermal = p.thermal_iterations > 0 && p.talus_degrees > 0 && p.talus_degrees < 90;
    bool drain = p.breach_depth > 0;
    float breachShare = drain ? 0.1f : 0.0f;
    float hydraulicShare = thermal ? 0.85f : 1.0f;


    // Tiles wide enough that same-colour tiles can't interact (see the header comment).
    int tile = e.p.max_lifetime + r + 3;
    int tilesX = (width - 1 + tile - 1) / tile, tilesZ = (depth - 1 + tile - 1) / tile;
    double totalDroplets = std::max(0.0, (double)p.droplets_per_cell) * (double)(width - 1) * (double)(depth - 1);
    // More rounds interleave the tiles' work, so no tile runs far ahead of its neighbours.
    int rounds = (int)std::clamp(totalDroplets / ((double)tilesX * tilesZ * 2000.0), 1.0, 32.0);

    std::vector<int> colour[9];
    for (int tz = 0; tz < tilesZ; tz++)
        for (int tx = 0; tx < tilesX; tx++) colour[tx % 3 + 3 * (tz % 3)].push_back(tz * tilesX + tx);

    int phase = 0, phases = rounds * 9;
    for (int round = 0; round < rounds; round++)
        for (int c = 0; c < 9; c++, phase++) {
            if (cancelled()) return 1;
            const auto& list = colour[c];
            parallel_for((int)list.size(), [&](int k) {
                int t = list[k];
                int x0 = (t % tilesX) * tile, z0 = (t / tilesX) * tile;
                int x1 = std::min(x0 + tile, width - 1), z1 = std::min(z0 + tile, depth - 1);
                // Droplets in proportion to the tile's area (edge tiles are clipped), spread evenly over the rounds.
                double share = totalDroplets * (double)(x1 - x0) * (z1 - z0) / ((double)(width - 1) * (depth - 1));
                Rng rng(mix((uint64_t)(uint32_t)p.seed, (uint64_t)round * 1000003ull + (uint64_t)t));
                double first = share * round / rounds, last = share * (round + 1) / rounds;
                long n = (long)std::floor(last) - (long)std::floor(first);
                for (long i = 0; i < n; i++) {
                    if ((i & 1023) == 0 && cancelled()) return;
                    float x = x0 + rng.unit() * (x1 - x0), z = z0 + rng.unit() * (z1 - z0);
                    e.droplet(rng, std::min(x, (float)(width - 2)), std::min(z, (float)(depth - 2)));
                }
            });
            report((hydraulicShare - breachShare) * (phase + 1) / phases);
        }
    if (cancelled()) return 1;

    if (thermal) {
        size_t n = (size_t)width * depth;
        std::vector<float> tmp(n);
        float tanTalus = std::tan(p.talus_degrees * 3.14159265f / 180.0f);
        float rate = std::clamp(p.thermal_rate, 0.0f, 1.0f);
        float* a = heights;
        float* b = tmp.data();
        for (int it = 0; it < p.thermal_iterations; it++) {
            if (cancelled()) return 1;
            thermal_step(a, b, width, depth, tanTalus, cell_size, rate);
            std::swap(a, b);
            report(hydraulicShare - breachShare + (1 - hydraulicShare) * (it + 1) / p.thermal_iterations);
        }
        if (a != heights) std::memcpy(heights, a, n * sizeof(float));
    }
    // Last, so no sediment settles back into them: outlets for shallow hollows, as rivers would cut.
    // A channel's banks can reshape hollows next to it, so repeat while passes still drain something.
    const int breachPasses = 3;
    for (int pass = 0; drain && pass < breachPasses; pass++) {
        int drained = breach(heights, width, depth, cell_size, p.sea_level, p.breach_depth, cancelled, [&](float f) {
            report(1 - breachShare + breachShare * (pass + f) / breachPasses);
        });
        if (drained < 0) return 1;
        if (drained == 0) break;
    }
    report(1);
    return 0;
}
