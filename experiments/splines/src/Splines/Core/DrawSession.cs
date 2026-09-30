using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// One in-progress Draw-tool chain (DESIGN.md → Draw tool, mode 1): the points placed so far. Each click builds the
/// leg up to it at once, so only the leg from the last point to the cursor is a preview; the next leg continues the
/// road just built, making the last point its live corner. <see cref="Undo"/>/<see cref="Redo"/> step through the
/// points; the caller undoes the matching graph step (<c>SplineNetwork</c>). Discarded when the chain ends.
/// Core-only: no Godot, no terrain.
/// </summary>
public sealed class DrawSession
{
    private readonly List<Pi> _placed = new();
    private readonly Stack<Pi> _redo = new();

    /// <summary>Every point placed in this chain; a built leg runs between each two.</summary>
    public IReadOnlyList<Pi> Placed => _placed;
    /// <summary>The point the preview leg starts from (the last placed), or none.</summary>
    public IReadOnlyList<Pi> Pis => _placed.Count == 0 ? Array.Empty<Pi>() : new[] { _placed[^1] };
    public int LegsBuilt => Math.Max(0, _placed.Count - 1);
    public bool IsEmpty => _placed.Count == 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Radius offered to the next corner placed (Shift+wheel / <c>[</c> <c>]</c>).</summary>
    public float PendingRadius { get; private set; }

    /// <summary>The last point is a dead end the next leg continues (the road built so far, or one the chain started
    /// on), so it's the live corner Shift+wheel sizes. Set by the caller, which knows the graph.</summary>
    public bool StartIsCorner { get; set; }

    /// <summary>Clears the chain and resets the pending radius to the profile's default.</summary>
    public void Reset(float defaultRadius)
    {
        _placed.Clear();
        _redo.Clear();
        StartIsCorner = false;
        PendingRadius = defaultRadius;
    }

    /// <summary>Records a placed point (the chain's start, or the end of a leg just built). <paramref name="hard"/>
    /// is only honoured by the caller after it has checked <c>ProfileRules.AllowHardCorners</c>.</summary>
    public void Place(Vector2 position, bool hard)
    {
        _placed.Add(Point(position, hard));
        _redo.Clear();
    }

    /// <summary>Steps back one point (the caller undoes its leg). False when there was nothing to pop.</summary>
    public bool Undo()
    {
        if (_placed.Count == 0) return false;
        _redo.Push(_placed[^1]);
        _placed.RemoveAt(_placed.Count - 1);
        return true;
    }

    /// <summary>Restores the last point stepped back (the caller redoes its leg). False when there's nothing.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _placed.Add(_redo.Pop());
        return true;
    }

    /// <summary>Sets the pending radius, kept at or below <paramref name="maxRadius"/> (the most the live corner fits,
    /// Anarchy or not) and at or above <paramref name="minRadius"/> (the profile's, or ~0 with Anarchy). The live
    /// corner (the last point, when the next leg continues the road there) takes it too, so Shift+wheel grows or
    /// shrinks it in place (storyboard step 2).</summary>
    public void SetPendingRadius(float radius, float minRadius, float maxRadius = float.PositiveInfinity)
    {
        PendingRadius = MathF.Max(MathF.Min(radius, maxRadius), minRadius);
        if (StartIsCorner && _placed.Count > 0 && !_placed[^1].Hard) _placed[^1] = _placed[^1] with { Radius = PendingRadius };
    }

    /// <summary>The preview leg: the last point to a floating end at <paramref name="cursor"/>, which carries the
    /// pending radius (used if it lands on a dead end it continues).</summary>
    public Alignment BuildPreview(Vector2 cursor) => new(new[] { _placed[^1], Point(cursor, false) });

    /// <summary>The leg a click at <paramref name="position"/> builds.</summary>
    public Alignment LegTo(Vector2 position, bool hard) => new(new[] { _placed[^1], Point(position, hard) });

    private Pi Point(Vector2 position, bool hard) => new(position, hard ? 0 : PendingRadius, Hard: hard);
}
