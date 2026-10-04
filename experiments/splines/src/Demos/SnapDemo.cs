using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-snap</c> (S3): checks <see cref="SnapEngine"/> directly on synthetic candidates and queries — node,
/// edge, guide crossing, the four single guides, the soft/Ctrl angle locks, length/equal-length, the provider mask,
/// and Space. Pure Core, no scene or camera needed (catch distance is a literal plan-unit constant standing in
/// for "8px worth of plan units" — the pixel→plan conversion itself lives in <c>SplineDrawTool</c>, untested here).
/// Prints each case, then <c>Demo snap: all ok</c> or <c>FAILED</c>.
/// </summary>
public partial class SnapDemo : Node
{
    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-snap")) Run();
    }

    private void Run()
    {
        NodeAndEdge();
        AngleSteps();
        LengthSteps();
        Guides();
        StoryboardRules();
        SpaceAndMask();
        foreach (var f in _failures) GD.PrintErr($"Demo snap: FAILED {f}");
        GD.Print($"Demo snap: {(_failures.Count == 0 ? "all ok" : $"FAILED ({_failures.Count})")}");
    }

    private static Vector2 V(float x, float z) => new(x, z);
    private static Alignment Line(Vector2 a, Vector2 b) => new(new[] { new Pi(a), new Pi(b) });
    private static SnapCandidate Candidate(Alignment a, float width = 10f, string label = "") => new(a, width, label);

    private static ProfileRules Rules(float width = 10f, float snapLength = 8f) =>
        new() { Width = width, SnapLength = snapLength, SnapProviders = SnapProviders.All };

    private void NodeAndEdge()
    {
        var edge = Line(V(0, 0), V(100, 0));

        // Cursor close to the start node (within max(catch, width/2)=5): node wins even though the edge's own
        // closest point (2, 0) is numerically nearer — level 1 always beats level 2.
        var atNode = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(2, 3), Rules = Rules(), CatchDistance = 2f, Candidates = new[] { Candidate(edge) },
        });
        Check("node snap kind", atNode.Kind == SnapKind.Node);
        Check("node beats edge", atNode.Position, V(0, 0));

        // Far from both ends, close to the middle of the edge: edge wins.
        var onEdge = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(50, 3), Rules = Rules(), CatchDistance = 2f, Candidates = new[] { Candidate(edge) },
        });
        Check("edge snap kind", onEdge.Kind == SnapKind.Edge);
        Check("edge snap position", onEdge.Position, V(50, 0));
    }

    private void AngleSteps()
    {
        // Reference heading tilted 20° off the plan axes, so an absolute Ctrl step lands somewhere different
        // from the soft-angle target — proving Ctrl actually overrides rather than agreeing by coincidence.
        float refDeg = 20f;
        var start = V(0, 0);
        var last = start + SplineMath.Direction(Rad(refDeg)) * 10f;
        var pis = new List<Pi> { new(start), new(last) };

        float targetDeg = refDeg + 90f; // the soft-90° direction, absolute
        var cursor90 = last + SplineMath.Direction(Rad(targetDeg)) * 20f;

        var soft = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = cursor90, SessionPis = pis, Rules = Rules(), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Angle,
        });
        Check("soft 90 kind", soft.Kind == SnapKind.Angle);
        Check("soft 90 direction", soft.Position, last + SplineMath.Direction(Rad(targetDeg)) * 20f, tol: 0.05f);

        // Ctrl counts its steps from the previous leg (20° off the plan axes here), not from east: 68° off the leg is
        // between soft targets, steps to 75° off it (absolute steps would give 90°), and the length to whole lots.
        var cursor68 = last + SplineMath.Direction(Rad(refDeg + 68f)) * 20f;
        var ctrl = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = cursor68, SessionPis = pis, Rules = Rules(), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Angle, CtrlSteps = true,
        });
        Check("ctrl overrides soft angle", ctrl.Kind == SnapKind.CtrlAngle);
        Check("ctrl steps from the leg", ctrl.Position, last + SplineMath.Direction(Rad(refDeg + 75f)) * 16f, tol: 0.05f);
        Check("ctrl whole lots", ctrl.LengthSteps ?? 0, 2);
        Check("ctrl degrees between roads", ctrl.Angle?.Degrees ?? 0, 105f, tol: 0.01f);

        var fine = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = cursor68, SessionPis = pis, Rules = Rules(), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Angle, CtrlSteps = true, FineSteps = true,
        });
        Check("ctrl fine steps kind", fine.Kind == SnapKind.CtrlAngle);
        Check("ctrl fine steps direction", fine.Position, last + SplineMath.Direction(Rad(refDeg + 70f)) * 16f, tol: 0.05f);

        // Nearly straight on stays exactly on the leg's line, and a short pull is still one whole lot.
        var straight = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = last + SplineMath.Direction(Rad(refDeg + 4f)) * 3f, SessionPis = pis, Rules = Rules(),
            CatchDistance = 1f, EnabledProviders = SnapProviders.Angle, CtrlSteps = true,
        });
        Check("ctrl straight on", straight.Position, last + SplineMath.Direction(Rad(refDeg)) * 8f, tol: 1e-3f);

        // With no leg or start edge to count from, the steps are absolute headings.
        var first = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = start + SplineMath.Direction(Rad(refDeg)) * 20f, SessionPis = new List<Pi> { new(start) },
            Rules = Rules(), CatchDistance = 1f, EnabledProviders = SnapProviders.Angle, CtrlSteps = true,
        });
        Check("ctrl absolute on a first leg", first.Position, start + SplineMath.Direction(Rad(15f)) * 16f, tol: 0.05f);

        // 45°, a separate soft target, confirmed by the same construction.
        float target45 = refDeg + 45f;
        var cursor45 = last + SplineMath.Direction(Rad(target45)) * 20f;
        var soft45 = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = cursor45, SessionPis = pis, Rules = Rules(), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Angle,
        });
        Check("soft 45 kind", soft45.Kind == SnapKind.Angle);
        Check("soft 45 direction", soft45.Position, last + SplineMath.Direction(Rad(target45)) * 20f, tol: 0.05f);
    }

    private void LengthSteps()
    {
        // One PI placed: no reference heading, so only levels 6-7 can act. rawLen 8.3 sits 0.3 m from the
        // 8 m step (round(8.3/8)=1) and far from any equal-length candidate.
        var pis = new List<Pi> { new(V(0, 0)) };
        var stepResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(8.3f, 0), SessionPis = pis, Rules = Rules(snapLength: 8f), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Length | SnapProviders.EqualLength,
        });
        Check("length step kind", stepResult.Kind == SnapKind.Length);
        Check("length step position", stepResult.Position, V(8, 0));

        // Previous leg is 13 m; the new leg's raw length (13.3 m) is close to it but far from any 8 m multiple.
        var eqPis = new List<Pi> { new(V(0, 0)), new(V(13, 0)) };
        var eqResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(13 + 13.3f, 0), SessionPis = eqPis, Rules = Rules(snapLength: 8f), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Length | SnapProviders.EqualLength,
        });
        Check("equal length kind", eqResult.Kind == SnapKind.EqualLength);
        Check("equal length position", eqResult.Position, V(13 + 13f, 0));
        Check("equal length matched leg", eqResult.EqualLength?.Length ?? -1f, 13f);

        // Equal length against a nearby built spline's leg (37 m), not only the previous leg of this draw.
        var built = Line(V(0, 100), V(37, 100));
        var builtEq = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(37.4f, 0), SessionPis = pis, Rules = Rules(snapLength: 8f), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Length | SnapProviders.EqualLength, Candidates = new[] { Candidate(built) },
        });
        Check("equal length to built leg kind", builtEq.Kind == SnapKind.EqualLength);
        Check("equal length to built leg position", builtEq.Position, V(37, 0));
        Check("equal length to built leg ends", builtEq.EqualLength?.A ?? V(-1, -1), V(0, 100));
    }

    private void Guides()
    {
        // Extension: continuing a straight edge past its end.
        var straight = Line(V(0, 0), V(100, 0));
        var extResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(150, 2), Rules = Rules(), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Extension, Candidates = new[] { Candidate(straight) },
        });
        Check("extension straight kind", extResult.Kind == SnapKind.GuideSingle);
        Check("extension straight tag", extResult.Tag == "extension");
        Check("extension straight position", extResult.Position, V(150, 0));

        // Extension along an arc's end tangent (the arc-only alignment from S1's clamped-end-legs case).
        var arcAlign = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(30, 0), 100), new Pi(V(30, 30)) });
        var endSample = arcAlign.Curve.Sample(arcAlign.Curve.Length);
        var arcCursor = endSample.Position + SplineMath.Left(endSample.Tangent) * 2f + endSample.Tangent * 20f;
        var arcExtResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = arcCursor, Rules = Rules(), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Extension, Candidates = new[] { Candidate(arcAlign) },
        });
        Check("extension arc kind", arcExtResult.Kind == SnapKind.GuideSingle);
        Check("extension arc position", arcExtResult.Position, endSample.Position + endSample.Tangent * 20f);

        // Node alignment: a line through another alignment's node, square to its edge direction.
        var vertical = Line(V(0, 0), V(0, 100));
        var alignResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(52, 102), Rules = Rules(), CatchDistance = 3f,
            EnabledProviders = SnapProviders.NodeAlign, Candidates = new[] { Candidate(vertical) },
        });
        Check("node align kind", alignResult.Kind == SnapKind.GuideSingle);
        Check("node align tag", alignResult.Tag.StartsWith("aligned ·"));
        Check("node align position", alignResult.Position, V(52, 100));

        // Parallel: alongside a straight edge at gap 0 (edge-to-edge).
        var parallelResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(50, -10.5f), Rules = Rules(width: 10f), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Parallel, Candidates = new[] { Candidate(straight, width: 10f) },
        });
        Check("parallel kind", parallelResult.Kind == SnapKind.GuideSingle);
        Check("parallel tag", parallelResult.Tag.StartsWith("parallel ·"));
        Check("parallel position", parallelResult.Position, V(50, -10));

        // Parallel along an arc stays concentric: two points near the inside offset, at different angles along
        // the sweep, both land exactly (radius - 10) from the original arc's centre.
        var corner = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(100, 0), 20), new Pi(V(100, 100)) });
        var arc = corner.Curve.Segments.OfType<ArcSegment>().Single();
        const float insetRadius = 10f; // arc.ArcRadius (20) - halfWidths (10)
        Vector2 NearArc(float t, float radial)
        {
            var dir = SplineMath.Direction(arc.StartAngle + arc.Sweep * t);
            return arc.Centre + dir * (insetRadius + radial);
        }
        foreach (var (t, radial, label) in new[] { (0.3f, 1f, "a"), (0.7f, -1f, "b") })
        {
            var r = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = NearArc(t, radial), Rules = Rules(width: 10f), CatchDistance = 3f,
                EnabledProviders = SnapProviders.Parallel, Candidates = new[] { Candidate(corner, width: 10f) },
            });
            Check($"parallel arc concentric kind {label}", r.Kind == SnapKind.GuideSingle);
            // The guide is a sampled polyline (2 m spacing), not the exact arc, so a small chord sag is expected.
            Check($"parallel arc concentric radius {label}", Vector2.Distance(r.Position, arc.Centre), insetRadius, tol: 0.1f);
        }

        // Perpendicular: a candidate heading for the current leg, square to a nearby edge, anchored at the leg's start.
        var perpPis = new List<Pi> { new(V(50, 50)) };
        var perpResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(52, 20), SessionPis = perpPis, Rules = Rules(), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Perpendicular, Candidates = new[] { Candidate(straight) },
        });
        Check("perpendicular kind", perpResult.Kind == SnapKind.GuideSingle);
        Check("perpendicular tag", perpResult.Tag == "90° to edge");
        Check("perpendicular position", perpResult.Position, V(50, 20));

        // A crossing of two extension guides beats either single guide.
        var a = Line(V(0, 0), V(100, 0));   // end (100,0), extends east
        var b = Line(V(150, -50), V(150, 0)); // end (150,0), extends south (away from its start)
        var crossResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(149, 1), Rules = Rules(), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Extension | SnapProviders.Crossing,
            Candidates = new[] { Candidate(a), Candidate(b) },
        });
        Check("guide crossing kind", crossResult.Kind == SnapKind.GuideCrossing);
        Check("guide crossing beats single", crossResult.Position, V(150, 0));
        Check("guide crossing guide count", crossResult.Guides.Count, 2);

        // At most two guides are ever lit, even with three sources in catch range.
        var c1 = Line(V(0, 0), V(100, 0));
        var c2 = Line(V(0, 5), V(100, 5));
        var c3 = Line(V(0, 10), V(100, 10));
        var manyResult = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(150, 5), Rules = Rules(), CatchDistance = 20f,
            EnabledProviders = SnapProviders.Extension,
            Candidates = new[] { Candidate(c1), Candidate(c2), Candidate(c3) },
        });
        Check("max two guides lit", manyResult.Guides.Count <= 2);
    }

    /// <summary>The storyboard's rules: angles rank above guides (a guide only picks where along a locked direction
    /// the point lands), the perpendicular foot is a snap, parallel steps any number of lots, and the soft angle is
    /// measured against the road the draw started on or the previous leg, whichever is closer.</summary>
    private void StoryboardRules()
    {
        var road = Line(V(0, 0), V(100, 0)); // extension runs east along z = 0
        var start = new List<Pi> { new(V(150, 50)) };

        // Started on a horizontal road, heading roughly square to it, near the extension: "extension · ∡ 90°", and
        // the leg stays at exactly 90.0° (the guide doesn't pull it to the cursor's x).
        var locked = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(151.5f, 1), SessionPis = start, StartHeading = V(1, 0), Rules = Rules(), CatchDistance = 3f,
            Candidates = new[] { Candidate(road) },
        });
        Check("angle + guide kind", locked.Kind == SnapKind.GuideSingle);
        Check("angle + guide tag", locked.Tag == "extension · ∡ 90°");
        Check("angle + guide position", locked.Position, V(150, 0));
        Check("angle + guide exact 90.0", MathF.Abs(Vector2.Dot(Vector2.Normalize(locked.Position - V(150, 50)), V(1, 0))), 0f, tol: 1e-5f);
        var unlocked = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(151.5f, 1), SessionPis = start, StartHeading = V(1, 0), Rules = Rules(), CatchDistance = 3f,
            Candidates = new[] { Candidate(road) }, EnabledProviders = SnapProviders.All & ~SnapProviders.Angle,
        });
        Check("guide alone follows the cursor", unlocked.Position, V(151.5f, 0));

        // Angle only (no guide near): "∡ 90° · square to edge".
        var squareToRoad = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(152, 20), SessionPis = start, StartHeading = V(1, 0), Rules = Rules(), CatchDistance = 1f,
            EnabledProviders = SnapProviders.Angle,
        });
        Check("square to edge tag", squareToRoad.Tag == "∡ 90° · square to edge");
        Check("square to edge position", squareToRoad.Position, V(150, 20));

        // Straight on after a leg: "∡ 180° · straight on".
        var straightOn = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(100, 3), SessionPis = new List<Pi> { new(V(0, 0)), new(V(50, 0)) }, Rules = Rules(),
            CatchDistance = 1f, EnabledProviders = SnapProviders.Angle,
        });
        Check("straight on tag", straightOn.Tag == "∡ 180° · straight on");
        Check("straight on position", straightOn.Position, V(100, 0), tol: 0.01f);

        // Road reference vs previous leg: whichever target is closer wins.
        var legs = new List<Pi> { new(V(-100, 0)), new(V(0, 0)) }; // previous leg heads east (0°)
        Vector2 At(float deg) => SplineMath.Direction(Rad(deg)) * 50f;
        AngleLock? Lock(float cursorDeg, float roadDeg) => SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = At(cursorDeg), SessionPis = legs, StartHeading = SplineMath.Direction(Rad(roadDeg)), Rules = Rules(),
            CatchDistance = 0.5f, EnabledProviders = SnapProviders.Angle,
        }).Angle;
        var roadWins = Lock(118, 30);   // leg: 118° is 17° off 135; road: 88° off a 30° road, 2° off square
        Check("road reference wins", roadWins?.Against == AngleReference.StartEdge);
        Check("road reference direction", SplineMath.Angle(roadWins?.Direction ?? default) * 180f / MathF.PI, 120f, tol: 0.01f);
        var legWins = Lock(91, 30);     // leg: 1° off square; road: 61°, 16° off diagonal
        Check("leg reference wins", legWins?.Against == AngleReference.Leg);
        Check("leg reference direction", SplineMath.Angle(legWins?.Direction ?? default) * 180f / MathF.PI, 90f, tol: 0.01f);
        var closer = Lock(92, 3);       // leg: 2° off square; road: 89°, 1° off square — the road is closer
        Check("closer reference wins", closer?.Against == AngleReference.StartEdge);

        // Perpendicular foot: landing on the foot from the leg's start is a snap, above the plain edge.
        var foot = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(51, 1), SessionPis = new List<Pi> { new(V(50, 50)) }, Rules = Rules(), CatchDistance = 3f,
            Candidates = new[] { Candidate(road) },
        });
        Check("perpendicular foot kind", foot.Kind == SnapKind.PerpendicularFoot);
        Check("perpendicular foot position", foot.Position, V(50, 0));
        Check("perpendicular foot tag", foot.Tag == "90° to edge");

        // A road wrapping round the leg's start (a P's end coming back up to its own first leg): the first leg has its
        // own foot, though the road's closest point to the start is elsewhere; a cursor over the road, off the centre
        // line, still catches it.
        var wrap = new Alignment(new[] { new Pi(V(0, 0)), new Pi(V(200, 0), 16), new Pi(V(200, 140), 16), new Pi(V(84, 140)) });
        var wrapFoot = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(101.5f, 4), SessionPis = new List<Pi> { new(V(100, 140)) }, Rules = Rules(), CatchDistance = 2f,
            Candidates = new[] { Candidate(wrap) },
        });
        Check("foot on a wrapping road", wrapFoot.Kind == SnapKind.PerpendicularFoot);
        Check("foot on a wrapping road: position", wrapFoot.Position, V(100, 0));

        // Parallel at any number of lots: a 24 m wide road, a 12 m new one, cursor 41 m clear → 40 m = 5 lots.
        var parallel = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(50, -(18 + 41)), Rules = Rules(width: 12f), CatchDistance = 3f,
            EnabledProviders = SnapProviders.Parallel, Candidates = new[] { Candidate(road, width: 24f) },
        });
        Check("parallel lots tag", parallel.Tag == "parallel · 40 m gap (5 lots)");
        Check("parallel lots position", parallel.Position, V(50, -58));
    }

    private void SpaceAndMask()
    {
        var edge = Line(V(0, 0), V(100, 0));

        var disabled = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(0.1f, 0.1f), Rules = Rules(), CatchDistance = 5f, Disabled = true,
            Candidates = new[] { Candidate(edge) },
        });
        Check("space disables everything", disabled.Kind == SnapKind.None);
        Check("space returns raw cursor", disabled.Position, V(0.1f, 0.1f));

        var masked = SnapEngine.Evaluate(new SnapQuery
        {
            Cursor = V(0.1f, 0.1f), Rules = Rules(), CatchDistance = 5f,
            EnabledProviders = SnapProviders.All & ~SnapProviders.Node,
            Candidates = new[] { Candidate(edge) },
        });
        Check("provider mask skips node", masked.Kind == SnapKind.Edge);
    }

    private static float Rad(float deg) => deg * MathF.PI / 180f;

    private void Check(string name, float got, float want, float tol = 1e-3f)
    {
        bool ok = MathF.Abs(got - want) <= tol;
        GD.Print($"  {name}: {got:0.####} (want {want:0.####}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, Vector2 got, Vector2 want, float tol = 1e-3f)
    {
        bool ok = Vector2.Distance(got, want) <= tol;
        GD.Print($"  {name}: ({got.X:0.###}, {got.Y:0.###}) (want ({want.X:0.###}, {want.Y:0.###})) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, int got, int want)
    {
        bool ok = got == want;
        GD.Print($"  {name}: {got} (want {want}) {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }

    private void Check(string name, bool ok)
    {
        GD.Print($"  {name}: {(ok ? "ok" : "FAILED")}");
        if (!ok) _failures.Add(name);
    }
}
