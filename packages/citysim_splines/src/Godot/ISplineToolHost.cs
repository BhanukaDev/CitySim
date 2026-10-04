using System;

namespace CitySim.Splines.Godot;

/// <summary>
/// What the Draw and Edit tools read from the consumer's UI: the picked profile, the active tool and draw mode, the
/// enabled snaps, Anarchy and Grid mode's blocks. The splines testbed implements it with its options bar; a game
/// implements it with its own build UI. Set on a tool as a node (<c>HostNode</c>) that implements this.
/// </summary>
public interface ISplineToolHost
{
    /// <summary>The profile new splines are drawn with; null = nothing picked, the Draw tool idles.</summary>
    SplineProfile? Profile { get; }
    SplineTool Tool { get; }
    DrawMode Mode { get; }
    SnapProviders EnabledSnaps { get; }
    /// <summary>Invalid issues still build (and stay red), and radii may go below the profile's minimum.</summary>
    bool Anarchy { get; }
    /// <summary>Grid mode's blocks: along the first edge, and across it.</summary>
    (int Cols, int Rows) GridBlocks { get; }
    GridFit GridFit { get; }

    /// <summary>A draw mode was picked (the Draw tool ends its chain).</summary>
    event Action<DrawMode>? ModeChanged;

    void SetTool(SplineTool tool);
    void SetAnarchy(bool on);
    /// <summary>Sets Grid mode's blocks, kept within limits.</summary>
    void SetGridBlocks(int cols, int rows);
}
