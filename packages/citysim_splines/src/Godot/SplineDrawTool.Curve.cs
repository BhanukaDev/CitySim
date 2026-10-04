using System;
using System.Linq;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Curve mode (key 2; DESIGN.md → Modes, 2): CS-style start, bend, end. The bend is the PI, rounded at the largest
/// radius that fits, so one click gives the gentlest arc; Shift+wheel or <c>[</c> <c>]</c> brings it in. The third
/// click builds the leg (one undo step) and the chain goes on from its end: two more clicks make the next arc. A bend
/// that continues a road is held on its tangent (Space frees it), so the joint runs straight through and the arcs
/// meet seamlessly; where the tangent meets the chain's own start line it snaps to close the loop smoothly (a circle
/// in 3–4 curves, as in CS). Everything else (snapping, trials, refusal, undo) is the Draw mode's.
/// </summary>
public partial class SplineDrawTool
{
    /// <summary>The bend's radius when brought in below the fit (null: the fit).</summary>
    private float? _bendRadius;

    /// <summary>The preview's bend is at the most that fits (its pill says so).</summary>
    private bool BendAtFit => _session.Bend is not null && _bendRadius is null;

    /// <summary>A click in Draw or Curve mode. In Curve, the second click of each leg places the bend.</summary>
    private void Click(NumVector2 at, bool hard)
    {
        if (Mode == DrawMode.Curve && !_session.IsEmpty && _session.Bend is null)
        {
            if (NumVector2.Distance(at, _session.Pis[0].Position) < SplineGraph.NodeTolerance) return;
            _session.SetBend(at);
            _bendRadius = null;
            return;
        }
        Place(at, hard);
    }

    /// <summary>
    /// The bend's radius for a leg ending at <paramref name="end"/> (the snapped cursor by default): the largest that
    /// fits, or less when brought in. A corner may use the whole of a leg to an end, but only half of one to a corner,
    /// and the leg's start is a corner when it continues a dead end, as is its end when <paramref name="endContinues"/>.
    /// 0 when there's no bend.
    /// </summary>
    private float BendRadius(ProfileRules rules, bool endContinues, NumVector2? end = null)
    {
        if (_session.Bend is not { } bend || (end ?? _snap?.Position) is not { } e) return 0;
        float fit = BendFit(_session.Pis[0].Position, bend.Position, e, StartTurns(bend.Position), endContinues && EndTurns(bend.Position, e));
        if (!float.IsFinite(fit)) return 0;
        return _bendRadius is { } r ? MathF.Min(r, fit) : fit;
    }

    private static float BendFit(NumVector2 start, NumVector2 bend, NumVector2 end, bool startIsCorner, bool endIsCorner)
    {
        var legIn = bend - start;
        var legOut = end - bend;
        float lenIn = legIn.Length(), lenOut = legOut.Length();
        if (lenIn < SplineMath.Epsilon || lenOut < SplineMath.Epsilon) return float.PositiveInfinity;
        float delta = MathF.Abs(SplineMath.Turn(legIn / lenIn, legOut / lenOut));
        if (delta < 1e-4f || delta > MathF.PI - 1e-3f) return float.PositiveInfinity;
        float t = MathF.Min(startIsCorner ? lenIn / 2 : lenIn, endIsCorner ? lenOut / 2 : lenOut);
        return t / MathF.Tan(delta / 2);
    }

    /// <summary>Shift+wheel / <c>[</c> <c>]</c> with a bend placed: scales the bend's radius, up to the fit (back to
    /// "fit" once it reaches it) and down to the profile's minimum (about 0 with Anarchy).</summary>
    private void AdjustBendRadius(float factor, SplineProfile profile)
    {
        var rules = profile.ToRules();
        bool continues = _snap is { Kind: SnapKind.Node } s && ContinuesAt(s.Position, rules);
        if (_session.Bend is not { } bend || _snap is null) return;
        float fit = BendFit(_session.Pis[0].Position, bend.Position, _snap.Position, StartTurns(bend.Position), continues && EndTurns(bend.Position, _snap.Position));
        if (!float.IsFinite(fit)) return;
        float r = MathF.Max(MathF.Min(_bendRadius ?? fit, fit) * factor, Host!.Anarchy ? 1f : profile.MinRadius);
        _bendRadius = r >= fit - 1e-3f ? null : r;
    }

    /// <summary>The direction a bend placed now is held to: on along the road the leg continues (Curve mode, before the
    /// bend), or null.</summary>
    private NumVector2? BendTangent() =>
        Mode == DrawMode.Curve && !_session.IsEmpty && _session.Bend is null && _session.StartIsCorner ? RoadTangent(_session.Pis[0].Position) : null;

    /// <summary>The direction on out of a dead end, along its road's last PI leg (so a point on it is exactly straight
    /// through, as <see cref="Alignment"/> sees it), or null.</summary>
    private NumVector2? RoadTangent(NumVector2 p)
    {
        var g = Network!.Graph;
        if (g.NodeAt(p) is not { } n || g.Arms(n) is not { Count: 1 } arms) return null;
        var pis = g.Edge(arms[0].EdgeId).Alignment.Pis;
        var d = arms[0].AtStart ? pis[0].Position - pis[1].Position : pis[^1].Position - pis[^2].Position;
        return d.LengthSquared() > SplineMath.Epsilon ? NumVector2.Normalize(d) : null;
    }

    /// <summary>The leg's start continues a road and turns there (a bend off the tangent, Space held): a corner, which
    /// takes half the leg. On the tangent it runs straight through and the bend gets the whole leg.</summary>
    private bool StartTurns(NumVector2 bend) =>
        _session.StartIsCorner && Turns(RoadTangent(_session.Pis[0].Position), bend - _session.Pis[0].Position);

    /// <summary>The leg's end continues a dead end and turns there.</summary>
    private bool EndTurns(NumVector2 bend, NumVector2 end) => Turns(RoadTangent(end) is { } t ? -t : null, end - bend);

    private static bool Turns(NumVector2? along, NumVector2 leg) =>
        along is not { } a || leg.LengthSquared() < SplineMath.Epsilon || MathF.Abs(SplineMath.Turn(a, NumVector2.Normalize(leg))) >= 1e-4f;

    /// <summary>A bend held on the tangent that lands on the line out of the chain's own other end closes the loop
    /// smoothly there: retags the snap <c>close loop · tangent</c>.</summary>
    private void TagLoopClose(ProfileRules rules)
    {
        if (_snap is not { Kind: SnapKind.GuideSingle } snap || BendTangent() is null) return;
        var g = Network!.Graph;
        if (g.DeadEndAt(_session.Pis[0].Position, rules) is not { } start) return;
        var e = g.Edge(start.EdgeId);
        var other = g.Node(start.AtStart ? e.End : e.Start);
        if (other.Edges.Count != 1) return;
        if (snap.Guides.Any(x => x.Kind == GuideKind.Extension && x.Tag != "tangent" && NumVector2.Distance(x.Source, other.Position) < 1e-3f))
            _snap = snap with { Tag = "close loop · tangent" };
    }
}
