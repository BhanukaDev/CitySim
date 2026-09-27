using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CitySim.TerrainSystem;

namespace CitySim.WaterSystem;

/// <summary>
/// Flowing water over a <see cref="HeightMap"/> (C++, <c>native/water/</c>). Owns the native state and, when threaded,
/// a worker that steps it in fixed ticks of <see cref="TickSeconds"/> simulated seconds:
/// <see cref="WaterSettings.Speed"/> simulated seconds per real second (×8 = 16 ticks a second), as far as the CPU
/// keeps up (whole ticks are dropped when it can't; a tick is never shortened). The same state and the same changes at
/// the same ticks give the same water, whatever the frame rate or thread count. Changes from the main thread (ground
/// edits, sources, settings, fills) are queued and applied between ticks. After each tick the worker publishes a
/// snapshot (surface, depth, velocity and pollutant per water cell); queries and rendering read that, and the renderer
/// blends from one tick's snapshot to the next. Engine-agnostic.
///
/// The water grid is the terrain's vertex grid, or every <see cref="Factor"/>th vertex on very large maps, so water
/// cell (x, z) sits at local position (x, z) × <see cref="CellSize"/>, the same metres as the terrain.
/// </summary>
public sealed class WaterSim : IDisposable
{
    /// <summary>Largest water grid side we simulate; bigger terrains use a coarser water grid.</summary>
    public const int MaxCells = 2048;
    /// <summary>Snapshot pages (for rendering uploads), in water cells.</summary>
    public const int PageSize = 256;
    /// <summary>Simulated seconds per tick.</summary>
    public const double TickSeconds = 1.0;

    public HeightMap Ground { get; }
    public int Factor { get; }
    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }
    public int TileSize { get; }
    public int TilesX { get; }
    public int TilesZ { get; }
    public int PagesX { get; }
    public int PagesZ { get; }

    private IntPtr _h;
    private readonly bool _threaded;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _queueLock = new();
    private readonly List<Action> _queue = new();
    private VertexRect _groundDirty = VertexRect.Empty;
    private bool _paramsDirty = true, _sourcesDirty = true;

    // Snapshot: 4 floats per water cell (display surface, depth, velocity x, z; see cs_water_read), the pollutant
    // concentration per cell, and dirty pages.
    private readonly float[] _snapshot;
    private readonly float[] _pollution;
    private long _publishes;
    private readonly bool[] _pageDirty;
    private readonly byte[] _tileChanged;
    private readonly object _snapLock = new();

    // Ground marks (distance to water, wet paint; 2 bytes per cell, see cs_water_read_ground), read at most once a
    // second of real time: they change slowly and the distance pass costs a few ms.
    private readonly byte[] _groundMarks;
    private readonly byte[] _groundScratch;
    private long _groundVersion;
    private readonly Stopwatch _groundClock = Stopwatch.StartNew();
    private double _groundReadAt = double.NegativeInfinity;
    private readonly object _groundLock = new();

    private WaterSettings _settings = new();
    private IReadOnlyList<WaterSource> _sources = [];
    private WaterStats _stats;
    private double _simTime, _ratio, _stepMs, _advanceDebt;
    private long _ticks, _clampHits;
    private readonly object _statsLock = new();

    /// <param name="threads">Native worker threads (0 = the default); results don't depend on it.</param>
    public WaterSim(HeightMap ground, bool threaded = true, int threads = 0)
    {
        Ground = ground;
        int cells = Math.Max(ground.Width, ground.Depth) - 1;
        int f = Math.Max(1, (cells + MaxCells - 1) / MaxCells);
        if ((ground.Width - 1) % f != 0 || (ground.Depth - 1) % f != 0) f = 1;
        Factor = f;
        Width = (ground.Width - 1) / f + 1;
        Depth = (ground.Depth - 1) / f + 1;
        CellSize = ground.CellSize * f;
        TileSize = WaterNative.TileSize;
        TilesX = (Width + TileSize - 1) / TileSize;
        TilesZ = (Depth + TileSize - 1) / TileSize;
        PagesX = Math.Max(1, (Width - 1 + PageSize - 1) / PageSize);
        PagesZ = Math.Max(1, (Depth - 1 + PageSize - 1) / PageSize);
        _snapshot = new float[Width * Depth * 4];
        _pollution = new float[Width * Depth];
        _pageDirty = new bool[PagesX * PagesZ];
        _tileChanged = new byte[TilesX * TilesZ];
        _groundMarks = new byte[Width * Depth * 2];
        _groundScratch = new byte[Width * Depth * 2];
        _h = WaterNative.Create(Width, Depth, CellSize, threads);
        WaterNative.SetGround(_h, ground.Data, ground.Width, ground.Depth, Factor, 0, 0, Width - 1, Depth - 1);
        _threaded = threaded;
        Publish(all: true);
        if (threaded)
        {
            _thread = new Thread(Run) { Name = "Water sim", IsBackground = true };
            _thread.Start();
        }
    }

    // --- Settings and sources ---

    public WaterSettings Settings
    {
        get => _settings;
        set { _settings = value; lock (_queueLock) _paramsDirty = true; }
    }

    /// <summary>Set while the game is paused (Esc menu): the worker stops stepping but keeps applying changes.</summary>
    public bool Suspended { get; set; }

    public IReadOnlyList<WaterSource> Sources => _sources;
    /// <summary>Raised (on the caller's thread) when <see cref="SetSources"/> replaces the list.</summary>
    public event Action? SourcesChanged;

    public void SetSources(IEnumerable<WaterSource> sources)
    {
        _sources = sources.ToArray();
        lock (_queueLock) _sourcesDirty = true;
        SourcesChanged?.Invoke();
    }

    public int NextSourceId() => _sources.Count == 0 ? 1 : _sources.Max(s => s.Id) + 1;

    // --- Commands ---

    /// <summary>Terrain heights changed in <paramref name="terrainRect"/> (terrain vertex indices).</summary>
    public void GroundChanged(VertexRect terrainRect)
    {
        if (terrainRect.IsEmpty) return;
        lock (_queueLock) _groundDirty = _groundDirty.Union(terrainRect);
        if (!_threaded) ApplyQueued();
    }

    /// <summary>
    /// Fills hollows up to the given lake levels (one per terrain vertex, NaN = dry; <c>LakeMap.Level</c>) and floods
    /// everything below sea level that connects to the border when there's a sea source.
    /// </summary>
    public void FillHollows(float[]? lakeLevels)
    {
        float[]? surface = null;
        if (lakeLevels is not null && lakeLevels.Length == Ground.Width * Ground.Depth)
        {
            surface = new float[Width * Depth];
            for (int z = 0; z < Depth; z++)
                for (int x = 0; x < Width; x++)
                    surface[z * Width + x] = lakeLevels[z * Factor * Ground.Width + x * Factor];
        }
        Enqueue(() =>
        {
            if (surface is not null) WaterNative.RaiseTo(_h, surface);
            WaterNative.FillSources(_h);
        });
    }

    /// <summary>
    /// Drains the water a Lake or River source holds (see <c>cs_water_drain</c>): its basin at its level, not what ran
    /// on downhill. Returns the drained surface per water cell (NaN = untouched), for <see cref="RestoreSurface"/>.
    /// Waits for the worker.
    /// </summary>
    public float[] DrainSource(WaterSource s)
    {
        var removed = new float[Width * Depth];
        Sync(() => WaterNative.Drain(_h, s.X, s.Z, s.Radius, s.Level, removed));
        return removed;
    }

    /// <summary>Raises the water back to a surface from <see cref="DrainSource"/> (NaN = leave).</summary>
    public void RestoreSurface(float[] surface)
    {
        if (surface.Length != Width * Depth) throw new ArgumentException("Surface grid has the wrong size.", nameof(surface));
        Enqueue(() => WaterNative.RaiseTo(_h, surface));
    }

    /// <summary>Raises the water to at least the given surface per terrain vertex (NaN = leave), like <see cref="FillHollows"/> without the sea.</summary>
    public void RaiseTo(float[] terrainLevels)
    {
        if (terrainLevels.Length != Ground.Width * Ground.Depth) throw new ArgumentException("Level grid has the wrong size.", nameof(terrainLevels));
        var surface = new float[Width * Depth];
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
                surface[z * Width + x] = terrainLevels[z * Factor * Ground.Width + x * Factor];
        Enqueue(() => WaterNative.RaiseTo(_h, surface));
    }

    /// <summary>Runs <paramref name="a"/> on the worker and waits for it.</summary>
    private void Sync(Action a)
    {
        if (!_threaded)
        {
            ApplyQueued();
            a();
            return;
        }
        using var done = new ManualResetEventSlim();
        Enqueue(() => { a(); done.Set(); });
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The water simulation didn't answer.");
    }

    /// <summary>Removes all water.</summary>
    public void Clear() => Enqueue(() => WaterNative.SetDepth(_h, ReadOnlySpan<float>.Empty));

    /// <summary>Replaces the water with saved depths (<see cref="Width"/> × <see cref="Depth"/>).</summary>
    public void LoadDepth(float[] depth)
    {
        if (depth.Length != Width * Depth) throw new ArgumentException("Water depth grid has the wrong size.", nameof(depth));
        Enqueue(() => WaterNative.SetDepth(_h, depth));
    }

    /// <summary>Replaces the pollutant with saved masses (kg per cell; call after <see cref="LoadDepth"/>).</summary>
    public void LoadPollution(float[] mass)
    {
        if (mass.Length != Width * Depth) throw new ArgumentException("Pollution grid has the wrong size.", nameof(mass));
        Enqueue(() => WaterNative.SetPollution(_h, mass));
    }

    /// <summary>Replaces the wet paint with saved values (0..1 per water cell).</summary>
    public void LoadPaint(float[] paint)
    {
        if (paint.Length != Width * Depth) throw new ArgumentException("Paint grid has the wrong size.", nameof(paint));
        Enqueue(() => WaterNative.SetPaint(_h, paint));
    }

    /// <summary>The wet paint per water cell, 0..1 (waits for the worker; for saving).</summary>
    public float[] ReadPaint() => ReadGrid(WaterNative.GetPaint);

    /// <summary>The current depth per water cell (waits for the worker; for saving).</summary>
    public float[] ReadDepth() => ReadGrid(WaterNative.GetDepth);

    /// <summary>The current pollutant mass per water cell in kg (waits for the worker; for saving).</summary>
    public float[] ReadPollution() => ReadGrid(WaterNative.GetPollution);

    private delegate void GridReader(IntPtr h, Span<float> into);

    private float[] ReadGrid(GridReader read)
    {
        var result = new float[Width * Depth];
        Sync(() => read(_h, result));
        return result;
    }

    private void Enqueue(Action a)
    {
        lock (_queueLock) _queue.Add(a);
        if (!_threaded) ApplyQueued();
    }

    /// <summary>Applies queued changes (worker thread, or the caller when not threaded).</summary>
    private void ApplyQueued()
    {
        Action[] actions;
        VertexRect ground;
        bool prm, src;
        lock (_queueLock)
        {
            actions = _queue.ToArray();
            _queue.Clear();
            ground = _groundDirty;
            _groundDirty = VertexRect.Empty;
            prm = _paramsDirty;
            src = _sourcesDirty;
            _paramsDirty = _sourcesDirty = false;
        }
        if (prm) WaterNative.SetParams(_h, ToParams(_settings));
        if (!ground.IsEmpty)
        {
            // A water cell averages the vertices up to Factor/2 away, so widen by one water cell.
            int x0 = ground.MinX / Factor - 1, z0 = ground.MinZ / Factor - 1;
            int x1 = (ground.MaxX + Factor - 1) / Factor + 1, z1 = (ground.MaxZ + Factor - 1) / Factor + 1;
            WaterNative.SetGround(_h, Ground.Data, Ground.Width, Ground.Depth, Factor, x0, z0, x1, z1);
        }
        if (src) WaterNative.SetSources(_h, _sources.Select(s => s.ToNative()).ToArray());
        foreach (var a in actions) a();
    }

    private static WaterNative.Params ToParams(WaterSettings s) => new()
    {
        Gravity = 9.81f,
        Damping = 0.2f,
        Evaporation = s.EvaporationMmPerMin / 1000f / 60f,
        MaxSpeed = 25f,
        LevelRate = 2f,
        OpenEdges = s.OpenEdges ? 15 : 0,
        Manning = 0.03f,
        PollutionDecay = s.PollutionHalfLifeMin > 0 ? MathF.Log(2f) / (s.PollutionHalfLifeMin * 60f) : 0f,
        PaintRate = s.PaintMinutes > 0 ? 1f / (s.PaintMinutes * 60f) : 0f,
        PaintFade = s.PaintFadeHours > 0 ? 1f / (s.PaintFadeHours * 3600f) : 0f,
    };

    // --- Stepping ---

    /// <summary>
    /// Simulates <paramref name="seconds"/> right now, in whole ticks (a remainder carries over to the next call; only
    /// without the worker: demos and tests).
    /// </summary>
    public void Advance(double seconds)
    {
        if (_threaded) throw new InvalidOperationException("Advance is for unthreaded sims.");
        ApplyQueued();
        _advanceDebt += seconds;
        while (_advanceDebt >= TickSeconds - 1e-9)
        {
            StepTick();
            _advanceDebt -= TickSeconds;
        }
        Publish(all: false);
    }

    /// <summary>
    /// Simulates <paramref name="seconds"/> (whole ticks) as fast as possible on the worker (it does nothing else
    /// meanwhile), e.g. so a screenshot shows settled water. Without the worker, runs right away.
    /// </summary>
    public void RunFor(double seconds) => Enqueue(() =>
    {
        var clock = Stopwatch.StartNew();
        double publishAt = 0;
        for (long n = (long)Math.Round(seconds / TickSeconds); n > 0; n--)
        {
            StepTick();
            if (clock.Elapsed.TotalSeconds >= publishAt)
            {
                Publish(all: false);
                publishAt = clock.Elapsed.TotalSeconds + 0.1;
            }
        }
    });

    private void StepTick()
    {
        var sw = Stopwatch.StartNew();
        var st = WaterNative.Step(_h, (float)TickSeconds, 1_000_000);
        lock (_statsLock)
        {
            _stats = st;
            _simTime += st.Simulated;
            _ticks++;
            _clampHits += st.ClampHits;
            _stepMs = sw.Elapsed.TotalMilliseconds / Math.Max(st.Substeps, 1);
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, debt = 0, publishAt = 0, ratioWindow = 0, ratioSim = 0;
        while (!_stop)
        {
            // Queued changes land between ticks.
            ApplyQueued();
            double now = clock.Elapsed.TotalSeconds, real = Math.Min(now - last, 0.25);
            last = now;
            var s = _settings;
            bool running = !s.Paused && !Suspended;
            if (running) debt += real * s.Speed;
            else debt = 0;
            // When the CPU can't keep up, drop whole ticks of backlog: the water slows down instead of stalling.
            debt = Math.Min(debt, Math.Max(s.Speed * 0.25, TickSeconds));
            bool stepped = false;
            if (debt >= TickSeconds)
            {
                StepTick();
                debt -= TickSeconds;
                ratioSim += TickSeconds;
                Publish(all: false);
                stepped = true;
            }
            else if (now >= publishAt)
            {
                // Paused or between ticks: still show queued changes (edits, fills) now and then.
                Publish(all: false);
                publishAt = now + 1.0 / 30.0;
            }
            ratioWindow += real;
            if (ratioWindow >= 1.0)
            {
                lock (_statsLock) _ratio = running ? ratioSim / ratioWindow : 0;
                ratioWindow = ratioSim = 0;
            }
            if (!stepped || debt < TickSeconds) Thread.Sleep(running ? 1 : 5);
        }
    }

    private void Publish(bool all)
    {
        PublishGround(all);
        lock (_snapLock)
        {
            if (WaterNative.Read(_h, _snapshot, _pollution, _tileChanged, all) <= 0) return;
            _publishes++;
            int tilesPerPage = PageSize / TileSize;
            for (int tz = 0; tz < TilesZ; tz++)
                for (int tx = 0; tx < TilesX; tx++)
                {
                    if (_tileChanged[tz * TilesX + tx] == 0) continue;
                    // A tile's cells also appear as the last row/column of the page before it.
                    int px0 = Math.Max(0, (tx * TileSize - 1) / PageSize), px1 = Math.Min(PagesX - 1, tx / tilesPerPage);
                    int pz0 = Math.Max(0, (tz * TileSize - 1) / PageSize), pz1 = Math.Min(PagesZ - 1, tz / tilesPerPage);
                    for (int pz = pz0; pz <= pz1; pz++)
                        for (int px = px0; px <= px1; px++) _pageDirty[pz * PagesX + px] = true;
                }
        }
    }

    private void PublishGround(bool force)
    {
        double now = _groundClock.Elapsed.TotalSeconds;
        if (!force && now < _groundReadAt + 1.0) return;
        _groundReadAt = now;
        if (!WaterNative.ReadGround(_h, _groundScratch, force)) return;
        lock (_groundLock)
        {
            _groundScratch.CopyTo(_groundMarks, 0);
            _groundVersion++;
        }
    }

    /// <summary>Goes up when new ground marks are published (<see cref="CopyGroundMarks"/>).</summary>
    public long GroundVersion { get { lock (_groundLock) return _groundVersion; } }

    /// <summary>
    /// Copies the ground marks: per water cell (row-major) the distance to water in quarter metres (255 = 63.75 m or
    /// more, each metre above the water counting as 4) and the wet paint (0..255). Returns their version.
    /// </summary>
    public long CopyGroundMarks(Span<byte> into)
    {
        lock (_groundLock)
        {
            _groundMarks.CopyTo(into);
            return _groundVersion;
        }
    }

    /// <summary>
    /// Publishes everything now, ground marks included (unthreaded sims, e.g. demos, after <see cref="Advance"/>).
    /// </summary>
    public void PublishNow()
    {
        if (_threaded) Sync(() => Publish(all: true));
        else
        {
            ApplyQueued();
            Publish(all: true);
        }
    }

    // --- Snapshot reads ---

    /// <summary>
    /// If page (px, pz) changed since its last copy, copies its (<see cref="PageSize"/> + 1)² cells into
    /// <paramref name="rgba"/> (rows of PageSize + 1, cells past the grid repeat the edge) and returns true.
    /// <paramref name="anyWater"/> tells whether anything in it should be drawn, <paramref name="tileWater"/> which of its
    /// (<see cref="PageSize"/> / <see cref="TileSize"/>)² tiles.
    /// </summary>
    public bool CopyPage(int px, int pz, Span<float> rgba, Span<float> pollution, Span<bool> tileWater, out bool anyWater,
        bool force = false)
    {
        anyWater = false;
        tileWater.Clear();
        int n = PageSize + 1, tilesPerPage = PageSize / TileSize;
        lock (_snapLock)
        {
            int p = pz * PagesX + px;
            if (!_pageDirty[p] && !force) return false;
            _pageDirty[p] = false;
            int x0 = px * PageSize, z0 = pz * PageSize;
            for (int z = 0; z < n; z++)
            {
                int sz = Math.Min(z0 + z, Depth - 1);
                for (int x = 0; x < n; x++)
                {
                    int sx = Math.Min(x0 + x, Width - 1);
                    var src = _snapshot.AsSpan((sz * Width + sx) * 4, 4);
                    src.CopyTo(rgba.Slice((z * n + x) * 4, 4));
                    pollution[z * n + x] = _pollution[sz * Width + sx];
                    if (src[1] < 0f) continue;
                    anyWater = true;
                    // A cell on a tile border belongs to both tiles' meshes.
                    for (int tz = Math.Max(0, (z - 1) / TileSize); tz <= Math.Min(tilesPerPage - 1, z / TileSize); tz++)
                        for (int tx = Math.Max(0, (x - 1) / TileSize); tx <= Math.Min(tilesPerPage - 1, x / TileSize); tx++)
                            tileWater[tz * tilesPerPage + tx] = true;
                }
            }
        }
        return true;
    }

    /// <summary>Water depth at a local position (metres; 0 when dry), bilinear.</summary>
    public float DepthAt(float x, float z) => Bilinear(x, z, 1, clampZero: true);

    /// <summary>Water surface at a local position, or null when dry.</summary>
    public float? SurfaceAt(float x, float z) => DepthAt(x, z) > 0.01f ? Bilinear(x, z, 0, clampZero: false) : null;

    /// <summary>Pollutant concentration (kg/m³) at a local position (nearest cell; 0 when dry).</summary>
    public float PollutionAt(float x, float z)
    {
        int cx = Math.Clamp((int)MathF.Round(x / CellSize), 0, Width - 1), cz = Math.Clamp((int)MathF.Round(z / CellSize), 0, Depth - 1);
        lock (_snapLock) return _snapshot[(cz * Width + cx) * 4 + 1] > 0.01f ? _pollution[cz * Width + cx] : 0f;
    }

    /// <summary>Water velocity (m/s, x and z) at a local position.</summary>
    public (float X, float Z) VelocityAt(float x, float z) => (Bilinear(x, z, 2, false), Bilinear(x, z, 3, false));

    private float Bilinear(float x, float z, int channel, bool clampZero)
    {
        float fx = Math.Clamp(x / CellSize, 0f, Width - 1.001f), fz = Math.Clamp(z / CellSize, 0f, Depth - 1.001f);
        int x0 = (int)fx, z0 = (int)fz;
        float tx = fx - x0, tz = fz - z0;
        lock (_snapLock)
        {
            float V(int xi, int zi)
            {
                float v = _snapshot[(zi * Width + xi) * 4 + channel];
                return clampZero ? Math.Max(v, 0f) : v;
            }
            float a = V(x0, z0) + (V(x0 + 1, z0) - V(x0, z0)) * tx;
            float b = V(x0, z0 + 1) + (V(x0 + 1, z0 + 1) - V(x0, z0 + 1)) * tx;
            return a + (b - a) * tz;
        }
    }

    // --- Stats ---

    public WaterStats LastStats { get { lock (_statsLock) return _stats; } }
    public double SimTime { get { lock (_statsLock) return _simTime; } }
    /// <summary>Cells that would have sent out more water than they held, over all ticks (the pipes are scaled, so 0).</summary>
    public long ClampHits { get { lock (_statsLock) return _clampHits; } }
    /// <summary>Ticks simulated so far.</summary>
    public long Ticks { get { lock (_statsLock) return _ticks; } }
    /// <summary>Counts snapshots published; the renderer starts a new blend when it changes.</summary>
    public long Publishes { get { lock (_snapLock) return _publishes; } }
    /// <summary>Simulated seconds per real second over the last second (below <see cref="WaterSettings.Speed"/> when the CPU can't keep up).</summary>
    public double SimRatio { get { lock (_statsLock) return _ratio; } }
    /// <summary>Time per substep in the last step, in milliseconds.</summary>
    public double StepMs { get { lock (_statsLock) return _stepMs; } }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
        if (_h != IntPtr.Zero)
        {
            WaterNative.Destroy(_h);
            _h = IntPtr.Zero;
        }
    }
}
