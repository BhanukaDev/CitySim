using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// One in-progress Draw-tool session (DESIGN.md → Draw tool, mode 1): the PIs placed so far, and a small undo/redo
/// stack scoped to this draw (ROADMAP.md S2). Finished splines are undone on the graph (<c>SplineNetwork</c>).
/// Discarded on cancel or finish. Core-only: no Godot, no terrain.
/// </summary>
public sealed class DrawSession
{
    private readonly List<Pi> _pis = new();
    private readonly Stack<Pi> _redo = new();

    public IReadOnlyList<Pi> Pis => _pis;
    public bool IsEmpty => _pis.Count == 0;

    /// <summary>Radius offered to the next corner placed (Shift+wheel / <c>[</c> <c>]</c>).</summary>
    public float PendingRadius { get; private set; }

    /// <summary>Clears the draw and resets the pending radius to the profile's default.</summary>
    public void Reset(float defaultRadius)
    {
        _pis.Clear();
        _redo.Clear();
        PendingRadius = defaultRadius;
    }

    /// <summary>Places a PI (start or corner). <paramref name="hard"/> is only honoured by the caller after it has
    /// already checked <c>ProfileRules.AllowHardCorners</c> — this class doesn't know about profiles.</summary>
    public void Place(Vector2 position, bool hard)
    {
        _pis.Add(new Pi(position, hard ? 0 : PendingRadius, Hard: hard));
        _redo.Clear();
    }

    /// <summary>Replaces the placed PIs (taking an offered turnout). One step, like a click; clears redo.</summary>
    public void ReplaceWith(IEnumerable<Pi> pis)
    {
        _pis.Clear();
        _pis.AddRange(pis);
        _redo.Clear();
    }

    /// <summary>RMB / Ctrl+Z: pops the last PI. False when there was nothing to pop (caller cancels the draw).</summary>
    public bool Undo()
    {
        if (_pis.Count == 0) return false;
        _redo.Push(_pis[^1]);
        _pis.RemoveAt(_pis.Count - 1);
        return true;
    }

    /// <summary>Ctrl+Shift+Z / Ctrl+Y: restores the last undone PI. False when there's nothing to redo.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _pis.Add(_redo.Pop());
        return true;
    }

    /// <summary>Sets the pending radius, clamped to <paramref name="minRadius"/> (the profile's, or ~0 with Anarchy). The live corner (the last PI placed,
    /// which the preview rounds as the mouse moves on) takes it too, so Shift+wheel grows or shrinks it in place
    /// (storyboard step 2).</summary>
    public void SetPendingRadius(float radius, float minRadius)
    {
        PendingRadius = MathF.Max(radius, minRadius);
        if (_pis.Count >= 2 && !_pis[^1].Hard) _pis[^1] = _pis[^1] with { Radius = PendingRadius };
    }

    /// <summary>The placed PIs plus a floating end point at <paramref name="cursor"/> (not yet committed) — for the
    /// preview ribbon.</summary>
    public Alignment BuildPreview(Vector2 cursor)
    {
        var pis = new List<Pi>(_pis) { new(cursor) };
        return new Alignment(pis);
    }

    /// <summary>The finished alignment (no cursor point). Call on double-click/Enter once <see cref="Pis"/> has at
    /// least two points.</summary>
    public Alignment Finish() => new(_pis);
}
