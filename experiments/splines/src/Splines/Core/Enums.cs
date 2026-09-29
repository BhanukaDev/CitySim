using System;

namespace CitySim.Splines;

/// <summary>How edges of a profile meet at a junction (DESIGN.md → Junctions).</summary>
public enum JunctionKind
{
    /// <summary>Any branch angle above the profile's minimum; a footprint with cut-backs and curb arcs.</summary>
    Node,
    /// <summary>A branch must leave tangentially, within the profile's turnout angle.</summary>
    Turnout,
    /// <summary>Edges share a node with no footprint.</summary>
    Join,
    /// <summary>No junctions at all.</summary>
    None,
}

/// <summary>How a vertical-profile station gets its height.</summary>
public enum VerticalMode { Ground, Absolute, Offset }

/// <summary>Whether the spline shapes the ground with its section template.</summary>
public enum ShapingMode { Section, None }

/// <summary>How the section's outer edge meets the natural ground.</summary>
public enum EdgeMode { Slope, Wall, Auto }

/// <summary>The snaps and guides a profile offers, in the options bar's toggle order.</summary>
[Flags]
public enum SnapProviders
{
    None = 0,
    Node = 1 << 0,
    Edge = 1 << 1,
    Crossing = 1 << 2,
    Extension = 1 << 3,
    NodeAlign = 1 << 4,
    Parallel = 1 << 5,
    Perpendicular = 1 << 6,
    Angle = 1 << 7,
    Length = 1 << 8,
    EqualLength = 1 << 9,
    All = (1 << 10) - 1,
}

/// <summary>The Draw tool's modes (keys 1–4). All of them produce PIs.</summary>
public enum DrawMode { Draw, Curve, Freehand, Grid }
