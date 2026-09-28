using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
/// The water grid is the terrain's vertex grid, or every <see cref="Factor"/>th vertex on very large maps (7 m cells at
/// 28.7 km), so water cell (x, z) sits at local position (x, z) × <see cref="CellSize"/>, the same metres as the
/// terrain. Storage is sparse (see <c>water.h</c>): memory follows the water, not the map. Grids handed in and out are
/// <see cref="WaterGrid"/>s in the same tiles. The ground marks for the terrain shader are on a coarser grid
/// (<see cref="MarksWidth"/>, at most <see cref="MaxMarkCells"/> + 1 a side), as the hover flood preview is.
/// </summary>
public sealed class WaterSim : IDisposable
{
    /// <summary>Largest water grid side we simulate; bigger terrains use a coarser water grid.</summary>
    public const int MaxCells = 4096;
    /// <summary>The largest grid side new sims use (debug flag <c>--water-cells=n</c>; 2048 gives the 14 m cells big maps had before M6 phase 3f).</summary>
    public static int GridLimit { get; set; } = MaxCells;
    /// <summary>Largest ground-marks grid side (a dense texture and a distance pass); finer water grids are averaged into it.</summary>
    public const int MaxMarkCells = 2048;
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
    /// <summary>Water cells per ground-marks cell.</summary>
    public int MarkFactor { get; }
    public int MarksWidth { get; }
    public int MarksDepth { get; }
    public float MarksCellSize => CellSize * MarkFactor;
    /// <summary>Terrain cells per ground-marks cell (the coarse grid the hover preview also runs on).</summary>
    public int MarksTerrainFactor => Factor * MarkFactor;

    private IntPtr _h;
    private GCHandle _pin;
    private readonly bool _threaded;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _queueLock = new();
    private readonly List<Action> _queue = new();
    private VertexRect _groundDirty = VertexRect.Empty;
    private bool _paramsDirty = true, _sourcesDirty = true;

    // Snapshot per tile (null = nothing shows): 4 floats per water cell (display surface, depth, velocity x, z; see
    // cs_water_read_tiles) and the pollutant concentration per cell; dirty pages.
    private readonly float[]?[] _snapshot;
    private readonly float[]?[] _pollution;
    private long _publishes;
    private readonly bool[] _pageDirty;
    private readonly byte[] _tileChanged;
    private readonly object _snapLock = new();
    // Worker-side read buffers, ReadBatch tiles at a time.
    private const int ReadBatch = 64;
    private readonly int[] _readTiles = new int[ReadBatch];
    private readonly float[] _readRgba = new float[ReadBatch * WaterGrid.TileCells * 4];
    private readonly float[] _readPollution = new float[ReadBatch * WaterGrid.TileCells];
    private readonly byte[] _readVisible = new byte[ReadBatch];

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
    /// <param name="maxCells">Largest water grid side (tests use a small one to get a coarser grid on a small map).</param>
    public WaterSim(HeightMap ground, bool threaded = true, int threads = 0, int maxCells = 0, int maxMarkCells = MaxMarkCells)
    {
        if (maxCells <= 0) maxCells = GridLimit;
        Ground = ground;
        int cells = Math.Max(ground.Width, ground.Depth) - 1;
        int f = Math.Max(1, (cells + maxCells - 1) / maxCells);
        if ((ground.Width - 1) % f != 0 || (ground.Depth - 1) % f != 0) f = 1;
        Factor = f;
        Width = (ground.Width - 1) / f + 1;
        Depth = (ground.Depth - 1) / f + 1;
        CellSize = ground.CellSize * f;
        TileSize = WaterNative.TileSize;
        if (TileSize != WaterGrid.Tile) throw new InvalidOperationException($"Water library tiles are {TileSize}², WaterGrid expects {WaterGrid.Tile}².");
        TilesX = (Width + TileSize - 1) / TileSize;
        TilesZ = (Depth + TileSize - 1) / TileSize;
        PagesX = Math.Max(1, (Width - 1 + PageSize - 1) / PageSize);
        PagesZ = Math.Max(1, (Depth - 1 + PageSize - 1) / PageSize);
        int m = Math.Max(1, (Math.Max(Width, Depth) - 1 + maxMarkCells - 1) / maxMarkCells);
        if ((Width - 1) % m != 0 || (Depth - 1) % m != 0) m = 1;
        MarkFactor = m;
        MarksWidth = (Width - 1) / m + 1;
        MarksDepth = (Depth - 1) / m + 1;
        _snapshot = new float[TilesX * TilesZ][];
        _pollution = new float[TilesX * TilesZ][];
        _pageDirty = new bool[PagesX * PagesZ];
        _tileChanged = new byte[TilesX * TilesZ];
        _groundMarks = new byte[MarksWidth * MarksDepth * 2];
        _groundScratch = new byte[MarksWidth * MarksDepth * 2];
        _pin = GCHandle.Alloc(ground.Buffer, GCHandleType.Pinned);
        unsafe
        {
            _h = WaterNative.Create(Width, Depth, CellSize, threads, (float*)_pin.AddrOfPinnedObject(), ground.Width, ground.Depth, Factor);
        }
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
        var surface = lakeLevels is not null && lakeLevels.Length == Ground.Width * Ground.Depth ? Levels(lakeLevels) : null;
        Enqueue(() =>
        {
            if (surface is not null) RaiseTiles(surface);
            WaterNative.FillSources(_h);
        });
    }

    /// <summary>Terrain-vertex levels (NaN = none) sampled onto the water cells, only tiles that have any.</summary>
    private WaterGrid Levels(float[] terrainLevels)
    {
        var grid = new WaterGrid(Width, Depth, float.NaN);
        int gw = Ground.Width;
        for (int t = 0; t < TilesX * TilesZ; t++)
            grid.ForTile(t, (x, z, i) =>
            {
                float v = terrainLevels[z * Factor * gw + x * Factor];
                if (float.IsFinite(v)) grid.GetOrAdd(t)[i] = v;
            });
        return grid;
    }

    /// <summary>Worker: raises the water to a surface grid (NaN = leave), then recounts.</summary>
    private void RaiseTiles(WaterGrid surface)
    {
        foreach (int t in surface.Tiles()) WaterNative.RaiseTile(_h, t, surface.GetTile(t));
        WaterNative.Commit(_h);
    }

    /// <summary>
    /// Drains the water a Lake or River source holds (see <c>cs_water_drain</c>): its basin at its level, not what ran
    /// on downhill. Returns the drained surface per water cell (NaN = untouched), for <see cref="RestoreSurface"/>.
    /// Waits for the worker.
    /// </summary>
    public WaterGrid DrainSource(WaterSource s)
    {
        var removed = new WaterGrid(Width, Depth, float.NaN);
        Sync(() =>
        {
            if (WaterNative.Drain(_h, s.X, s.Z, s.Radius, s.Level) <= 0) return;
            foreach (int t in WaterNative.ListDrained(_h))
            {
                var values = new float[WaterGrid.TileCells];
                if (WaterNative.GetDrained(_h, t, values)) removed.SetTile(t, values);
            }
        });
        return removed;
    }

    /// <summary>Raises the water back to a surface from <see cref="DrainSource"/> (NaN = leave).</summary>
    public void RestoreSurface(WaterGrid surface)
    {
        if (surface.Width != Width || surface.Depth != Depth) throw new ArgumentException("Surface grid has the wrong size.", nameof(surface));
        Enqueue(() => RaiseTiles(surface));
    }

    /// <summary>Raises the water to at least the given surface per terrain vertex (NaN = leave), like <see cref="FillHollows"/> without the sea.</summary>
    public void RaiseTo(float[] terrainLevels)
    {
        if (terrainLevels.Length != Ground.Width * Ground.Depth) throw new ArgumentException("Level grid has the wrong size.", nameof(terrainLevels));
        var surface = Levels(terrainLevels);
        Enqueue(() => RaiseTiles(surface));
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

    /// <summary>Removes all water (the wet paint stays).</summary>
    public void Clear() => Enqueue(() => WaterNative.Clear(_h));

    /// <summary>A new grid over the water cells (to fill for the Load methods).</summary>
    public WaterGrid NewGrid() => new(Width, Depth);

    /// <summary>
    /// Replaces the water with saved depths, and optionally the pollutant (kg per cell) and the wet paint (0..1); grids
    /// of <see cref="Width"/> × <see cref="Depth"/>.
    /// </summary>
    public void LoadWater(WaterGrid depth, WaterGrid? pollution = null, WaterGrid? paint = null)
    {
        Check(depth);
        if (pollution is not null) Check(pollution);
        if (paint is not null) Check(paint);
        Enqueue(() =>
        {
            WaterNative.Clear(_h);
            Write(depth, WaterNative.Field.Depth);
            if (pollution is not null) Write(pollution, WaterNative.Field.Pollution);
            if (paint is not null) Write(paint, WaterNative.Field.Paint);
            WaterNative.Commit(_h);
        });
    }

    /// <summary>Replaces the water with saved depths (the pollutant is cleared).</summary>
    public void LoadDepth(WaterGrid depth) => LoadWater(depth);

    /// <summary>Sets the pollutant (kg per cell) where the grid has tiles.</summary>
    public void LoadPollution(WaterGrid mass) => LoadField(mass, WaterNative.Field.Pollution);

    /// <summary>Sets the wet paint (0..1 per water cell) where the grid has tiles.</summary>
    public void LoadPaint(WaterGrid paint) => LoadField(paint, WaterNative.Field.Paint);

    private void LoadField(WaterGrid grid, WaterNative.Field field)
    {
        Check(grid);
        Enqueue(() =>
        {
            Write(grid, field);
            WaterNative.Commit(_h);
        });
    }

    private void Check(WaterGrid g)
    {
        if (g.Width != Width || g.Depth != Depth) throw new ArgumentException($"Water grid is {g.Width}x{g.Depth}, the sim {Width}x{Depth}.");
    }

    private void Write(WaterGrid grid, WaterNative.Field field)
    {
        foreach (int t in grid.Tiles()) WaterNative.SetTile(_h, t, field, grid.GetTile(t));
    }

    /// <summary>The wet paint per water cell, 0..1 (waits for the worker; for saving).</summary>
    public WaterGrid ReadPaint() => ReadGrid(WaterNative.Field.Paint);

    /// <summary>The current depth per water cell (waits for the worker; for saving).</summary>
    public WaterGrid ReadDepth() => ReadGrid(WaterNative.Field.Depth);

    /// <summary>The current pollutant mass per water cell in kg (waits for the worker; for saving).</summary>
    public WaterGrid ReadPollution() => ReadGrid(WaterNative.Field.Pollution);

    private WaterGrid ReadGrid(WaterNative.Field field)
    {
        var result = NewGrid();
        Sync(() =>
        {
            var tile = new float[WaterGrid.TileCells];
            foreach (int t in WaterNative.ListTiles(_h))
            {
                if (!WaterNative.GetTile(_h, t, field, tile)) continue;
                bool any = false;
                foreach (float v in tile) if (v > 0f) { any = true; break; }
                if (!any) continue;
                result.SetTile(t, tile);
                tile = new float[WaterGrid.TileCells];
            }
        });
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
            // A water cell's ground comes from the vertices up to Factor/2 away, so widen by one water cell.
            int x0 = ground.MinX / Factor - 1, z0 = ground.MinZ / Factor - 1;
            int x1 = (ground.MaxX + Factor - 1) / Factor + 1, z1 = (ground.MaxZ + Factor - 1) / Factor + 1;
            WaterNative.SetGround(_h, x0, z0, x1, z1);
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
        if (WaterNative.ChangedTiles(_h, _tileChanged, all) <= 0) return;
        int tilesPerPage = PageSize / TileSize, n = 0;
        for (int t = 0; t <= _tileChanged.Length; t++)
        {
            if (t < _tileChanged.Length && _tileChanged[t] != 0) _readTiles[n++] = t;
            if (n == 0 || (n < ReadBatch && t < _tileChanged.Length)) continue;
            WaterNative.ReadTiles(_h, _readTiles.AsSpan(0, n), _readRgba, _readPollution, _readVisible);
            lock (_snapLock)
                for (int k = 0; k < n; k++)
                {
                    int tile = _readTiles[k];
                    if (_readVisible[k] == 0)
                    {
                        _snapshot[tile] = null;
                        _pollution[tile] = null;
                    }
                    else
                    {
                        _readRgba.AsSpan(k * WaterGrid.TileCells * 4, WaterGrid.TileCells * 4)
                            .CopyTo(_snapshot[tile] ??= new float[WaterGrid.TileCells * 4]);
                        _readPollution.AsSpan(k * WaterGrid.TileCells, WaterGrid.TileCells)
                            .CopyTo(_pollution[tile] ??= new float[WaterGrid.TileCells]);
                    }
                    // A tile's cells also appear as the last row/column of the page before it.
                    int tx = tile % TilesX, tz = tile / TilesX;
                    int px0 = Math.Max(0, (tx * TileSize - 1) / PageSize), px1 = Math.Min(PagesX - 1, tx / tilesPerPage);
                    int pz0 = Math.Max(0, (tz * TileSize - 1) / PageSize), pz1 = Math.Min(PagesZ - 1, tz / tilesPerPage);
                    for (int pz = pz0; pz <= pz1; pz++)
                        for (int px = px0; px <= px1; px++) _pageDirty[pz * PagesX + px] = true;
                }
            n = 0;
        }
        lock (_snapLock) _publishes++;
    }

    private void PublishGround(bool force)
    {
        double now = _groundClock.Elapsed.TotalSeconds;
        if (!force && now < _groundReadAt + 1.0) return;
        _groundReadAt = now;
        if (!WaterNative.ReadGround(_h, _groundScratch, force, MarkFactor)) return;
        lock (_groundLock)
        {
            _groundScratch.CopyTo(_groundMarks, 0);
            _groundVersion++;
        }
    }

    /// <summary>Goes up when new ground marks are published (<see cref="CopyGroundMarks"/>).</summary>
    public long GroundVersion { get { lock (_groundLock) return _groundVersion; } }

    /// <summary>
    /// Copies the ground marks: per marks cell (<see cref="MarksWidth"/> × <see cref="MarksDepth"/>, row-major) the distance to water in quarter metres (255 = 63.75 m or
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
            // Nothing shows anywhere on the page (most of a big map): its tiles are all hidden, no cells to copy.
            bool shows = false;
            for (int tz = z0 / TileSize; tz <= Math.Min((z0 + PageSize) / TileSize, TilesZ - 1) && !shows; tz++)
                for (int tx = x0 / TileSize; tx <= Math.Min((x0 + PageSize) / TileSize, TilesX - 1) && !shows; tx++)
                    shows = _snapshot[tz * TilesX + tx] is not null;
            if (!shows) return true;
            for (int z = 0; z < n; z++)
            {
                int sz = Math.Min(z0 + z, Depth - 1);
                for (int x = 0; x < n; x++)
                {
                    int sx = Math.Min(x0 + x, Width - 1);
                    var dst = rgba.Slice((z * n + x) * 4, 4);
                    int t = sz / TileSize * TilesX + sx / TileSize, li = WaterGrid.Local(sx, sz);
                    if (_snapshot[t] is not { } snap)
                    {
                        // Nothing shows here: hidden, 1 m under the ground like the library's hidden cells.
                        dst[0] = Ground[sx * Factor, sz * Factor] - 1f;
                        dst[1] = -1f;
                        dst[2] = dst[3] = 0f;
                        pollution[z * n + x] = 0f;
                        continue;
                    }
                    snap.AsSpan(li * 4, 4).CopyTo(dst);
                    pollution[z * n + x] = _pollution[t]![li];
                    if (dst[1] < 0f) continue;
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
        int t = cz / TileSize * TilesX + cx / TileSize, li = WaterGrid.Local(cx, cz);
        lock (_snapLock)
            return _snapshot[t] is { } snap && snap[li * 4 + 1] > 0.01f ? _pollution[t]![li] : 0f;
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
                float v;
                if (_snapshot[zi / TileSize * TilesX + xi / TileSize] is { } snap) v = snap[WaterGrid.Local(xi, zi) * 4 + channel];
                else v = channel switch { 0 => Ground[xi * Factor, zi * Factor] - 1f, 1 => -1f, _ => 0f };
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
        if (_pin.IsAllocated) _pin.Free();
    }
}
