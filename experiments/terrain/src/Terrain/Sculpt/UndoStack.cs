using System.Collections.Generic;

namespace CitySim.TerrainSystem.Sculpt;

/// <summary>
/// Stroke-based undo/redo for heightmap edits. A stroke snapshots the whole map when it starts
/// (cheap: ~1 MB for 513²), tracks which vertices it touched, and at the end stores only the
/// before/after heights of that rectangle.
/// </summary>
public sealed class UndoStack
{
    private sealed record Entry(VertexRect Rect, float[] Before, float[] After);

    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private float[]? _strokeBefore;
    private VertexRect _strokeRect = VertexRect.Empty;

    public int Capacity { get; set; } = 50;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool InStroke => _strokeBefore is not null;

    public void BeginStroke(HeightMap map)
    {
        _strokeBefore = map.Snapshot();
        _strokeRect = VertexRect.Empty;
    }

    public void Touch(VertexRect rect) => _strokeRect = _strokeRect.Union(rect);

    public void EndStroke(HeightMap map)
    {
        if (_strokeBefore is not null && !_strokeRect.IsEmpty)
        {
            var before = HeightMap.CopyRegion(_strokeBefore, map.Width, _strokeRect);
            _undo.AddLast(new Entry(_strokeRect, before, map.CopyRegion(_strokeRect)));
            if (_undo.Count > Capacity) _undo.RemoveFirst();
            _redo.Clear();
        }
        _strokeBefore = null;
        _strokeRect = VertexRect.Empty;
    }

    /// <summary>Restores the last stroke. Returns the changed rectangle, or empty if there was nothing to undo.</summary>
    public VertexRect Undo(HeightMap map)
    {
        if (InStroke || _undo.Last is null) return VertexRect.Empty;
        var e = _undo.Last.Value;
        _undo.RemoveLast();
        map.PasteRegion(e.Rect, e.Before);
        _redo.Push(e);
        return e.Rect;
    }

    public VertexRect Redo(HeightMap map)
    {
        if (InStroke || _redo.Count == 0) return VertexRect.Empty;
        var e = _redo.Pop();
        map.PasteRegion(e.Rect, e.After);
        _undo.AddLast(e);
        return e.Rect;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _strokeBefore = null;
        _strokeRect = VertexRect.Empty;
    }
}
