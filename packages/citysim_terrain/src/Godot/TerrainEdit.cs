using System;
using CitySim.TerrainSystem.Sculpt;

namespace CitySim.TerrainSystem;

/// <summary>
/// One undoable change to the ground from any system (roads, canals, building pads, scripts), started with
/// <see cref="Terrain.BeginEdit"/>. It shares the terrain's undo history with the sculpt/paint tools.
/// <code>
/// using var edit = terrain.BeginEdit();
/// var rect = VertexRect.FromMapRect(x0, z0, x1, z1, edit.Heights.CellSize, edit.Heights.Width, edit.Heights.Depth);
/// edit.Touch(rect);                       // before writing: saves the undo copy
/// for (...) edit.Heights[x, z] = ...;     // write directly (map vertex indices)
/// edit.Commit();                          // one undo step; the renderer, water and lakes follow
/// </code>
/// Only one edit (or tool stroke) can be open at a time. Disposing an open edit commits it; <see cref="Cancel"/> puts
/// everything back. Call <see cref="Changed"/> to show writes before committing (a live preview while dragging).
/// </summary>
public sealed class TerrainEdit : IDisposable
{
    private readonly Terrain _terrain;
    private readonly UndoStack _history;
    private readonly bool _paint;
    private bool _open = true;

    /// <summary>The map's heights, in metres above the terrain's origin (vertex indices, map order).</summary>
    public HeightMap Heights { get; }
    /// <summary>The map's painted materials (only undoable when the edit was begun with paint).</summary>
    public SplatMap Splat { get; }
    public bool IsOpen => _open;

    internal TerrainEdit(Terrain terrain, UndoStack history, HeightMap heights, SplatMap splat, bool paint)
    {
        _terrain = terrain;
        _history = history;
        _paint = paint;
        Heights = heights;
        Splat = splat;
        history.BeginStroke(heights, paint ? splat : null);
    }

    /// <summary>Call before writing inside <paramref name="rect"/> (clamped to the map): saves what's there for undo.</summary>
    public void Touch(VertexRect rect)
    {
        ThrowIfClosed();
        _history.Touch(rect.Clamp(Heights.Width, Heights.Depth));
    }

    /// <summary>The height vertex (x, z) had when the edit began (before any of its writes).</summary>
    public float Original(int x, int z)
    {
        ThrowIfClosed();
        return _history.StrokeOriginal(x, z);
    }

    /// <summary>Shows writes inside <paramref name="rect"/> now (pushed at the end of the frame) instead of on commit.</summary>
    public void Changed(VertexRect rect)
    {
        ThrowIfClosed();
        Apply(rect.Clamp(Heights.Width, Heights.Depth));
    }

    /// <summary>Records the edit as one undo step and shows it. Nothing is recorded if nothing was touched.</summary>
    public void Commit()
    {
        if (!_open) return;
        _open = false;
        var rect = _history.StrokeRect;
        _history.EndStroke();
        Apply(rect);
        _terrain.EditClosed();
    }

    /// <summary>Puts every touched vertex back as it was and records nothing.</summary>
    public void Cancel()
    {
        if (!_open) return;
        _open = false;
        Apply(_history.CancelStroke());
        _terrain.EditClosed();
    }

    public void Dispose() => Commit();

    private void Apply(VertexRect rect)
    {
        if (rect.IsEmpty) return;
        Heights.Invalidate(rect);
        _terrain.MarkDirty(rect);
        if (_paint) _terrain.MarkSplatDirty(rect);
    }

    private void ThrowIfClosed()
    {
        if (!_open) throw new InvalidOperationException("The terrain edit was already committed or cancelled.");
    }
}
