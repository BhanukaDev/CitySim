using System.Collections.Generic;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>What an undo or redo changed: a vertex rectangle, and whether heights and/or splat weights changed there.</summary>
public readonly record struct UndoChange(VertexRect Rect, bool Heights, bool Splat)
{
    public static readonly UndoChange None = new(VertexRect.Empty, false, false);
}

/// <summary>
/// Stroke-based undo/redo shared by sculpting (heights) and painting (splat weights), so both live in one
/// history. A stroke snapshots the map it edits when it starts, tracks which vertices it touched, and at
/// the end stores only the before/after data of that rectangle.
/// </summary>
public sealed class UndoStack
{
    private sealed record Entry(VertexRect Rect, float[]? HeightsBefore, float[]? HeightsAfter,
        float[]? SplatBefore, float[]? SplatAfter);

    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private float[]? _heightsBefore;
    private float[]? _splatBefore;
    private bool _inStroke;
    private VertexRect _strokeRect = VertexRect.Empty;

    public int Capacity { get; set; } = 50;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool InStroke => _inStroke;

    /// <summary>Starts a stroke that edits <paramref name="map"/> (sculpting) or <paramref name="splat"/> (painting).</summary>
    public void BeginStroke(HeightMap? map, SplatMap? splat = null)
    {
        _heightsBefore = map?.Snapshot();
        _splatBefore = splat?.Snapshot();
        _inStroke = true;
        _strokeRect = VertexRect.Empty;
    }

    public void Touch(VertexRect rect) => _strokeRect = _strokeRect.Union(rect);

    public void EndStroke(HeightMap map, SplatMap splat)
    {
        if (_inStroke && !_strokeRect.IsEmpty)
        {
            var r = _strokeRect;
            _undo.AddLast(new Entry(r,
                _heightsBefore is null ? null : HeightMap.CopyRegion(_heightsBefore, map.Width, r),
                _heightsBefore is null ? null : map.CopyRegion(r),
                _splatBefore is null ? null : SplatMap.CopyRegion(_splatBefore, splat.Width, r),
                _splatBefore is null ? null : splat.CopyRegion(r)));
            if (_undo.Count > Capacity) _undo.RemoveFirst();
            _redo.Clear();
        }
        _heightsBefore = null;
        _splatBefore = null;
        _inStroke = false;
        _strokeRect = VertexRect.Empty;
    }

    /// <summary>Restores the last stroke. Returns what changed, or <see cref="UndoChange.None"/> if there was nothing to undo.</summary>
    public UndoChange Undo(HeightMap map, SplatMap splat)
    {
        if (InStroke || _undo.Last is null) return UndoChange.None;
        var e = _undo.Last.Value;
        _undo.RemoveLast();
        _redo.Push(e);
        return Restore(e, map, splat, e.HeightsBefore, e.SplatBefore);
    }

    public UndoChange Redo(HeightMap map, SplatMap splat)
    {
        if (InStroke || _redo.Count == 0) return UndoChange.None;
        var e = _redo.Pop();
        _undo.AddLast(e);
        return Restore(e, map, splat, e.HeightsAfter, e.SplatAfter);
    }

    private static UndoChange Restore(Entry e, HeightMap map, SplatMap splat, float[]? heights, float[]? weights)
    {
        if (heights is not null) map.PasteRegion(e.Rect, heights);
        if (weights is not null) splat.PasteRegion(e.Rect, weights);
        return new UndoChange(e.Rect, heights is not null, weights is not null);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _heightsBefore = null;
        _splatBefore = null;
        _inStroke = false;
        _strokeRect = VertexRect.Empty;
    }
}
