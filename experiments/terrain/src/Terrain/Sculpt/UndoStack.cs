using System;
using System.Collections.Generic;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>What an undo or redo changed: a vertex rectangle, and whether heights and/or painted layers changed there.</summary>
public readonly record struct UndoChange(VertexRect Rect, bool Heights, bool Splat)
{
    public static readonly UndoChange None = new(VertexRect.Empty, false, false);
}

/// <summary>
/// Stroke-based undo/redo shared by sculpting (heights) and painting (splat control values), so both live in one
/// history. Nothing is copied when a stroke starts: the stroke calls <see cref="Touch"/> before each edit, and the
/// first touch of a 128² tile copies that tile. An entry keeps only its tiles.
///
/// Each tile keeps one buffer, holding whichever state is not on the map: the "before" state while the entry is on the
/// undo stack, the "after" state once undone. Undo and redo both swap the buffer with the map.
/// </summary>
public sealed class UndoStack
{
    public const int TileSize = 128;

    private sealed class Tile(VertexRect rect, float[]? heights, uint[]? splat)
    {
        public readonly VertexRect Rect = rect;
        public readonly float[]? Heights = heights;
        public readonly uint[]? Splat = splat;
    }

    private sealed class Entry
    {
        public required VertexRect Rect;
        public required List<Tile> Tiles;
        public float[]? AllHeights;
        public Action? UndoAction, RedoAction;
        public bool Heights, Splat;
        public long Bytes;
    }

    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private long _bytes;

    private HeightMap? _map;
    private SplatMap? _splat;
    private bool _inStroke;
    private readonly Dictionary<int, Tile> _strokeTiles = new();
    private VertexRect _strokeRect = VertexRect.Empty;

    public int Capacity { get; set; } = 50;
    /// <summary>Oldest entries are dropped while the history holds more than this (the newest entry is always kept).</summary>
    public long MaxBytes { get; set; } = 768L << 20;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool InStroke => _inStroke;
    /// <summary>Memory held by undo and redo entries.</summary>
    public long Bytes => _bytes;

    /// <summary>Starts a stroke that edits <paramref name="map"/> (sculpting) and/or <paramref name="splat"/> (painting).</summary>
    public void BeginStroke(HeightMap? map, SplatMap? splat = null)
    {
        _map = map;
        _splat = splat;
        _inStroke = true;
        _strokeTiles.Clear();
        _strokeRect = VertexRect.Empty;
    }

    /// <summary>Call <em>before</em> editing <paramref name="rect"/>: saves the tiles it covers that this stroke hasn't touched yet.</summary>
    public void Touch(VertexRect rect)
    {
        if (!_inStroke || rect.IsEmpty) return;
        _strokeRect = _strokeRect.Union(rect);
        var (w, d) = _map is not null ? (_map.Width, _map.Depth) : (_splat!.Width, _splat.Depth);
        int tilesX = (w + TileSize - 1) / TileSize;
        for (int tz = rect.MinZ / TileSize; tz <= rect.MaxZ / TileSize; tz++)
            for (int tx = rect.MinX / TileSize; tx <= rect.MaxX / TileSize; tx++)
            {
                int key = tz * tilesX + tx;
                if (_strokeTiles.ContainsKey(key)) continue;
                var r = new VertexRect(tx * TileSize, tz * TileSize,
                    Math.Min(tx * TileSize + TileSize, w) - 1, Math.Min(tz * TileSize + TileSize, d) - 1);
                _strokeTiles[key] = new Tile(r, _map?.CopyRegion(r), _splat?.CopyRegion(r));
            }
    }

    public void EndStroke()
    {
        if (_inStroke && _strokeTiles.Count > 0)
        {
            var e = new Entry { Rect = _strokeRect, Tiles = new List<Tile>(_strokeTiles.Values), Heights = _map is not null, Splat = _splat is not null };
            foreach (var t in e.Tiles)
                e.Bytes += (t.Heights?.Length ?? 0) * sizeof(float) + (t.Splat?.Length ?? 0) * sizeof(uint);
            Push(e);
        }
        _map = null;
        _splat = null;
        _inStroke = false;
        _strokeTiles.Clear();
        _strokeRect = VertexRect.Empty;
    }

    /// <summary>
    /// Records a whole-map height change made outside a stroke (the terrain generator): <paramref name="before"/> is a
    /// full copy of the heights from before it (see <see cref="HeightMap.Snapshot"/>). The entry keeps only that copy.
    /// </summary>
    public void PushHeights(float[] before, HeightMap map)
    {
        Push(new Entry
        {
            Rect = new VertexRect(0, 0, map.Width - 1, map.Depth - 1), Tiles = [], AllHeights = before,
            Heights = true, Bytes = (long)before.Length * sizeof(float),
        });
    }

    /// <summary>
    /// Records a change that isn't heights or paint (e.g. placing a water source) as a pair of actions, so it shares the
    /// one history with strokes.
    /// </summary>
    public void PushAction(Action undo, Action redo)
    {
        Push(new Entry { Rect = VertexRect.Empty, Tiles = [], UndoAction = undo, RedoAction = redo, Bytes = 256 });
    }

    private void Push(Entry e)
    {
        _undo.AddLast(e);
        _bytes += e.Bytes;
        foreach (var r in _redo) _bytes -= r.Bytes;
        _redo.Clear();
        while (_undo.Count > 1 && (_undo.Count > Capacity || _bytes > MaxBytes))
        {
            _bytes -= _undo.First!.Value.Bytes;
            _undo.RemoveFirst();
        }
    }

    /// <summary>Restores the last entry. Returns what changed, or <see cref="UndoChange.None"/> if there was nothing to undo.</summary>
    public UndoChange Undo(HeightMap map, SplatMap splat)
    {
        if (InStroke || _undo.Last is null) return UndoChange.None;
        var e = _undo.Last.Value;
        _undo.RemoveLast();
        _redo.Push(e);
        if (e.UndoAction is { } undo)
        {
            undo();
            return UndoChange.None;
        }
        return Swap(e, map, splat);
    }

    public UndoChange Redo(HeightMap map, SplatMap splat)
    {
        if (InStroke || _redo.Count == 0) return UndoChange.None;
        var e = _redo.Pop();
        _undo.AddLast(e);
        if (e.RedoAction is { } redo)
        {
            redo();
            return UndoChange.None;
        }
        return Swap(e, map, splat);
    }

    private static UndoChange Swap(Entry e, HeightMap map, SplatMap splat)
    {
        if (e.AllHeights is not null) map.SwapRegion(e.Rect, e.AllHeights);
        foreach (var t in e.Tiles)
        {
            if (t.Heights is not null) map.SwapRegion(t.Rect, t.Heights);
            if (t.Splat is not null) splat.SwapRegion(t.Rect, t.Splat);
        }
        return new UndoChange(e.Rect, e.Heights, e.Splat);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _bytes = 0;
        _map = null;
        _splat = null;
        _inStroke = false;
        _strokeTiles.Clear();
        _strokeRect = VertexRect.Empty;
    }
}
