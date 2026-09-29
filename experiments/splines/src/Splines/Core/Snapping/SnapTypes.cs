using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// An existing built alignment offered as a snap/guide source. There's no graph yet (S4), so the caller
/// (<c>SplineDrawTool</c> today) passes every alignment it knows about; a "node" is just one of
/// <see cref="Alignment"/>'s two ends, and an "edge" is its <see cref="Splines.Alignment.Curve"/>.
/// </summary>
public readonly record struct SnapCandidate(Alignment Alignment, float Width, string Label = "");

/// <summary>Which level of DESIGN.md's snapping priority list produced a <see cref="SnapResult"/>.</summary>
public enum SnapKind { None, Node, Edge, GuideCrossing, GuideSingle, Angle, CtrlAngle, Length, EqualLength }

/// <summary>The line-shaped guide kinds. Equal length is a tick, not a line (see <see cref="SnapResult.EqualLengthTickStation"/>).</summary>
public enum GuideKind { Extension, NodeAlign, Parallel, Perpendicular }

/// <summary>
/// A guide to draw: a polyline in plan space. Two points for a straight guide; several (sampled via
/// <see cref="Curve.SampleEvery"/>) for a parallel guide following an arc, so the renderer never needs to know
/// a guide followed a curve.
/// </summary>
public readonly record struct GuideLine(IReadOnlyList<Vector2> Points, GuideKind Kind, string Tag);

/// <summary>Everything <see cref="SnapEngine"/> needs to resolve one frame's cursor position.</summary>
public sealed record SnapQuery
{
    public required Vector2 Cursor { get; init; }

    /// <summary>PIs placed so far this draw (<see cref="DrawSession.Pis"/>). Empty on the very first click.</summary>
    public IReadOnlyList<Pi> SessionPis { get; init; } = Array.Empty<Pi>();

    /// <summary>Heading of the edge the session started on, if the start PI itself snapped onto one. Feeds the
    /// soft-angle reference for the first leg; ignored once <see cref="SessionPis"/> has two or more points (the
    /// previous leg is the reference from then on).</summary>
    public Vector2? StartHeading { get; init; }

    public IReadOnlyList<SnapCandidate> Candidates { get; init; } = Array.Empty<SnapCandidate>();
    public required ProfileRules Rules { get; init; }

    /// <summary>The options bar's toggle bitmask; ANDed with <see cref="ProfileRules.SnapProviders"/> — the
    /// profile is a ceiling the bar can only narrow.</summary>
    public SnapProviders EnabledProviders { get; init; } = SnapProviders.All;

    /// <summary>Catch distance in plan units, already screen-pixels-to-plan converted by the caller.</summary>
    public required float CatchDistance { get; init; }

    /// <summary>How far away (plan units) a guide source is still considered.</summary>
    public float GuideSearchRadius { get; init; } = 400f;

    /// <summary>Ctrl held: absolute angle steps, overriding guide crossings/single guides and the soft angle.</summary>
    public bool CtrlSteps { get; init; }

    /// <summary>Ctrl+Shift: 5° steps instead of 15°.</summary>
    public bool FineSteps { get; init; }

    /// <summary>Space held: every level is skipped, including Ctrl steps.</summary>
    public bool Disabled { get; init; }
}

/// <summary>
/// The winning snap for one frame: the position to use, which level won, a HUD tag, up to two lit guides, and
/// (independently of whether anything won the position) a leg-relative length for an equal-length tick.
/// </summary>
public readonly record struct SnapResult(
    Vector2 Position,
    SnapKind Kind,
    string Tag,
    IReadOnlyList<GuideLine> Guides,
    float? EqualLengthTickStation)
{
    public static SnapResult None(Vector2 cursor) => new(cursor, SnapKind.None, "", Array.Empty<GuideLine>(), null);
}
