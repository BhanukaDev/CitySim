using System;
using System.Collections.Generic;
using System.Numerics;

namespace CitySim.Splines;

/// <summary>
/// A built edge offered as a snap/guide source: every node is one of its <see cref="Alignment"/>'s two ends, and the
/// edge itself is its <see cref="Splines.Alignment.Curve"/>. <see cref="OpenStart"/>/<see cref="OpenEnd"/> say an end
/// is a dead end (no other edge there), the only ends an extension guide continues from: past a junction the road
/// already goes on.
/// </summary>
public readonly record struct SnapCandidate(Alignment Alignment, float Width, string Label = "", bool OpenStart = true, bool OpenEnd = true)
{
    /// <summary>Corners whose road point isn't offered (<see cref="SplineGraph.ShowsRoadPoint"/>: half of a bend
    /// cut by a junction, whose node is the point).</summary>
    public IReadOnlyCollection<int>? HiddenCorners { get; init; }
}

/// <summary>
/// A bend's slider (DESIGN.md → Junctions → Corner junctions): the cursor held on the track from an arc corner's road
/// point out to its PI. <see cref="Radius"/> is the radius the bend takes so the road passes through
/// <see cref="Position"/> (the built radius at the road point, 0 at the PI: a sharp corner). The junction is made there
/// by <see cref="SplineGraph.SplitBend"/>.
/// </summary>
public readonly record struct BendSlide(Alignment Alignment, int Pi, float Radius, Vector2 Position);

/// <summary>What produced a <see cref="SnapResult"/>'s position (DESIGN.md → Snapping and guides → Priority).</summary>
public enum SnapKind
{
    None,
    Node,
    Edge,
    /// <summary>The foot of the perpendicular from the leg's start onto a nearby edge: a clean 90.0° T.</summary>
    PerpendicularFoot,
    GuideCrossing,
    GuideSingle,
    Angle,
    CtrlAngle,
    Length,
    EqualLength,
}

/// <summary>The line-shaped guide kinds. Equal length is a tick, not a line (see <see cref="SnapResult.EqualLength"/>).</summary>
public enum GuideKind { Extension, NodeAlign, Parallel, Perpendicular }

/// <summary>
/// A guide: a polyline in plan space. Two points for a straight guide, several (sampled) for a parallel guide
/// following an arc. Once a snap is resolved the lit guides are trimmed for display: from their
/// <see cref="Source"/> to a little past the snapped point (parallel guides stay whole).
/// </summary>
public sealed record GuideLine(IReadOnlyList<Vector2> Points, GuideKind Kind, string Tag)
{
    /// <summary>The node, end or foot point the guide comes from.</summary>
    public Vector2 Source { get; init; }
    /// <summary>Perpendicular: the edge's tangent at the foot (for the right-angle mark).</summary>
    public Vector2 EdgeDirection { get; init; }
    /// <summary>Parallel: the edge being followed, which side (+1 left, −1 right), its half width and the gap, so the
    /// gap bracket can be drawn at any station.</summary>
    public Curve? Along { get; init; }
    public float Side { get; init; }
    public float EdgeHalfWidth { get; init; }
    public float Gap { get; init; }
}

/// <summary>What a soft angle lock is measured against.</summary>
public enum AngleReference
{
    /// <summary>Ctrl steps: absolute headings.</summary>
    Absolute,
    /// <summary>The edge the draw started on.</summary>
    StartEdge,
    /// <summary>The previous leg of this draw.</summary>
    Leg,
}

/// <summary>
/// A direction lock on the current leg. <see cref="Vertex"/> is where the ∡ is drawn, between
/// <see cref="ReferenceDirection"/> and <see cref="Direction"/>. <see cref="Degrees"/> is the angle between the two
/// edges (90 = square, 180 = straight on); for Ctrl steps it's the absolute heading (counter-clockwise from east, seen
/// from above). <see cref="Meaning"/> is the plain word: "square", "diagonal", "straight on".
/// </summary>
public readonly record struct AngleLock(
    Vector2 Vertex, Vector2 ReferenceDirection, Vector2 Direction, float Degrees, AngleReference Against, string Meaning);

/// <summary>The leg the current leg matched in length: its two ends, and the length.</summary>
public readonly record struct LegMatch(Vector2 A, Vector2 B, float Length);

/// <summary>Everything <see cref="SnapEngine"/> needs to resolve one frame's cursor position.</summary>
public sealed record SnapQuery
{
    public required Vector2 Cursor { get; init; }

    /// <summary>PIs placed so far this draw (<see cref="DrawSession.Pis"/>). Empty before the first click.</summary>
    public IReadOnlyList<Pi> SessionPis { get; init; } = Array.Empty<Pi>();

    /// <summary>Heading of the edge the draw started on, if the first PI snapped onto a node or edge. It's one of the
    /// two soft-angle references (the other is the previous leg), for every leg of the draw.</summary>
    public Vector2? StartHeading { get; init; }

    /// <summary>The leg must leave the last PI along this direction (a curve's bend continuing a road on its tangent):
    /// the cursor is held on that ray, and guides and lengths still snap along it.</summary>
    public Vector2? TangentLock { get; init; }

    public IReadOnlyList<SnapCandidate> Candidates { get; init; } = Array.Empty<SnapCandidate>();
    public required ProfileRules Rules { get; init; }

    /// <summary>The options bar's toggle bitmask; ANDed with <see cref="ProfileRules.SnapProviders"/> — the
    /// profile is a ceiling the bar can only narrow.</summary>
    public SnapProviders EnabledProviders { get; init; } = SnapProviders.All;

    /// <summary>Catch distance in plan units, already screen-pixels-to-plan converted by the caller.</summary>
    public required float CatchDistance { get; init; }

    /// <summary>How far away (plan units) a guide source is still considered.</summary>
    public float GuideSearchRadius { get; init; } = 400f;

    /// <summary>Ctrl held: absolute angle steps instead of the soft angle.</summary>
    public bool CtrlSteps { get; init; }

    /// <summary>Ctrl+Shift: 5° steps instead of 15°.</summary>
    public bool FineSteps { get; init; }

    /// <summary>Space held: every level is skipped, including Ctrl steps.</summary>
    public bool Disabled { get; init; }

    /// <summary>An arc corner's road point is a slider (<see cref="BendSlide"/>) out to its PI, not just a point: the
    /// Draw tool's modes. Off, it snaps to the road point only (the Edit tool).</summary>
    public bool BendSliders { get; init; }
}

/// <summary>
/// The resolved snap for one frame. <see cref="Position"/> is where a click would land. <see cref="Tag"/> names the
/// snap and goes next to <see cref="TagAt"/>; <see cref="Guides"/> are the lit guides (at most two, trimmed for
/// display); the optional parts say what else to draw: an angle lock (∡), the length in snap steps, the leg matched
/// for equal length, and the tangent of the edge a node/edge snap landed on (the next draw's start-edge reference).
/// </summary>
public sealed record SnapResult
{
    public required Vector2 Position { get; init; }
    public SnapKind Kind { get; init; }
    public string Tag { get; init; } = "";
    public Vector2 TagAt { get; init; }
    public IReadOnlyList<GuideLine> Guides { get; init; } = Array.Empty<GuideLine>();
    public AngleLock? Angle { get; init; }
    /// <summary>The leg's length in whole <see cref="ProfileRules.SnapLength"/> steps, when it snapped to one.</summary>
    public int? LengthSteps { get; init; }
    public LegMatch? EqualLength { get; init; }
    public Vector2? EdgeTangent { get; init; }
    /// <summary>The cursor is on a bend's slider: the junction goes where it says, reshaping the bend.</summary>
    public BendSlide? Bend { get; init; }

    public static SnapResult None(Vector2 cursor) => new() { Position = cursor };
}
