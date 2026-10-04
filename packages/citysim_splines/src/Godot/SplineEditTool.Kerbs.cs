using System;
using System.Collections.Generic;
using System.Linq;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Kerb controls (DESIGN.md → Junctions → Kerb handles; <c>docs/kerb-handles.html</c>) on a selected junction: a knob
/// in the middle of each kerb, dragged across its corner to make that kerb round at a bigger or tighter radius, and a
/// road handle on each arm where its kerbs start, dragged along the arm to scale this arm's end of the kerbs on both
/// sides (keeping their difference; pulled out alone they flare up the arm). Both edit the same radii, stored per edge
/// end and side. A double-click puts one back to the profile's kerb; the junction's radial menu has Reset kerbs.
/// </summary>
public partial class SplineEditTool
{
    private const float KerbSnap = 0.5f;

    private static bool IsKerb(HandleKind kind) => kind is HandleKind.Kerb or HandleKind.KerbKnob;

    private bool Anarchy => Host?.Anarchy == true;

    private List<int> SelectedJunctions(SplineGraph g) => _selectedNodes.Where(g.HasNode).Where(n => IsKerbJunction(g, n)).ToList();

    /// <summary>The kerb knobs and road handles of every selected junction, as handles to pick.</summary>
    private IEnumerable<Handle> KerbControlsOf(SplineGraph g)
    {
        foreach (int n in SelectedJunctions(g))
        {
            foreach (var k in Junctions.KerbKnobs(g, n, Anarchy)) yield return new Handle(HandleKind.KerbKnob, k.EdgeId, k.AtStart ? 1 : 0, k.Position);
            foreach (var h in Junctions.KerbHandles(g, n, Anarchy)) yield return new Handle(HandleKind.Kerb, h.EdgeId, h.AtStart ? 1 : 0, h.Position);
        }
    }

    private static bool Same(KerbHandle k, Handle h) => h.Kind == HandleKind.Kerb && k.EdgeId == h.Id && k.AtStart == (h.Index == 1);
    private static bool Same(KerbKnob k, Handle h) => h.Kind == HandleKind.KerbKnob && k.EdgeId == h.Id && k.AtStart == (h.Index == 1);

    private static int NodeOfKerb(SplineGraph g, Handle h) => h.Index == 1 ? g.Edge(h.Id).Start : g.Edge(h.Id).End;

    /// <summary>Whether a node is a Node junction, which has kerbs (its radial menu's top action is Reset kerbs).</summary>
    private static bool IsKerbJunction(SplineGraph g, int nodeId)
    {
        var arms = g.Arms(nodeId);
        return arms.Count >= 3 && Junctions.KindOf(arms) == JunctionKind.Node;
    }

    private static bool KerbsSet(SplineGraph g, int nodeId) =>
        IsKerbJunction(g, nodeId) && g.Arms(nodeId).Any(a => g.Edge(a.EdgeId).KerbAt(a.AtStart).IsSet);

    private static NumVector2 ArmPoint(SplineGraph g, int edgeId, bool atStart, float station)
    {
        var c = g.Edge(edgeId).Alignment.Curve;
        return c.Sample(atStart ? station : c.Length - station).Position;
    }

    private void StartKerbDrag(Handle h, NumVector2 cursor)
    {
        var g = Network!.Graph;
        int node = NodeOfKerb(g, h);
        var edges = g.Node(node).Edges.Distinct().ToList();
        if (h.Kind == HandleKind.Kerb && Junctions.KerbHandles(g, node, Anarchy).FirstOrDefault(k => Same(k, h)) is { } road)
            _drag = new Drag
            {
                Handle = h, Grab = h.At - cursor, Edges = edges, Rules = road.Rules, Pi = h.At, Target = h.At,
                Kerb = road, KerbNode = node, KerbValue = 1,
            };
        else if (h.Kind == HandleKind.KerbKnob && Junctions.KerbKnobs(g, node, Anarchy).FirstOrDefault(k => Same(k, h)) is { } knob)
            _drag = new Drag
            {
                Handle = h, Grab = h.At - cursor, Edges = edges, Rules = knob.Rules, Pi = h.At, Target = h.At,
                KerbKnob = knob, KerbNode = node, KerbValue = knob.Radii.Min,
            };
    }

    /// <summary>What the cursor makes a held kerb control: a road handle's factor (the one that puts it at the cursor's
    /// station, snapped so its outer kerb's radius is a whole 0.5 m), or a knob's round radius (0.5 m steps), each
    /// within its range.</summary>
    private static float KerbValueAt(SplineGraph g, Drag d, NumVector2 cursor)
    {
        if (d.KerbKnob is { } k)
            return Math.Clamp(MathF.Round(k.RadiusAt(cursor) / KerbSnap) * KerbSnap, k.Min, k.Max);
        var h = d.Kerb!;
        var c = g.Edge(h.EdgeId).Alignment.Curve;
        float s = c.ClosestPoint(cursor).S;
        float f = MathF.Max(0, h.FactorAt(h.AtStart ? s : c.Length - s));
        float outer = h.Outer.Radius;
        return Math.Clamp(MathF.Round(f * outer / KerbSnap) * KerbSnap / outer, h.FactorMin, h.FactorMax);
    }

    /// <summary>The held kerb control's change on a graph.</summary>
    private static void ApplyKerb(SplineGraph g, Drag d)
    {
        if (d.KerbKnob is { } k)
        {
            g.SetKerb(k.EdgeId, k.AtStart, +1, d.KerbValue);
            g.SetKerb(k.OtherEdgeId, k.OtherAtStart, -1, d.KerbValue);
            return;
        }
        var h = d.Kerb!;
        foreach (var side in h.Sides) g.SetKerb(h.EdgeId, h.AtStart, side.Side, side.Radius * d.KerbValue);
    }

    /// <summary>Where the held kerb control is now.</summary>
    private static NumVector2 KerbPoint(SplineGraph g, Drag d) =>
        d.KerbKnob is { } k ? k.At(d.KerbValue) : ArmPoint(g, d.Kerb!.EdgeId, d.Kerb.AtStart, d.Kerb.StationOf(d.KerbValue));

    /// <summary>A double-click on a kerb control: back to the profile's kerb, one undo step.</summary>
    private void ResetKerb(Handle h)
    {
        if (Network is null) return;
        var g = Network.Graph;
        if (h.Kind == HandleKind.Kerb)
        {
            if (!g.Edge(h.Id).KerbAt(h.Index == 1).IsSet) return;
            Network.Apply(x => { x.SetKerb(h.Id, h.Index == 1, default(KerbEnds)); return 0; });
            return;
        }
        if (Junctions.KerbKnobs(g, NodeOfKerb(g, h), Anarchy).FirstOrDefault(k => Same(k, h)) is not { IsSet: true } knob) return;
        Network.Apply(x =>
        {
            x.SetKerb(knob.EdgeId, knob.AtStart, +1, null);
            x.SetKerb(knob.OtherEdgeId, knob.OtherAtStart, -1, null);
            return 0;
        });
    }

    /// <summary>The drag's tag: the kerbs' radii before → now (<c>kerb R 6 → 10 m</c>, <c>kerbs R 6 · 6 → 6–14 · 6–14 m</c>,
    /// a lopsided kerb as its two ends), amber at either end of the track with what stops it, red below the minimum (only
    /// with Anarchy).</summary>
    private (FlashTag Tag, bool Warn) KerbTag(Drag d, SplineGraph tried)
    {
        IReadOnlyList<(float Min, float Max)> before, after;
        bool atMax, atMin;
        KerbLimit limit;
        ProfileRules rules;
        if (d.KerbKnob is { } k)
        {
            before = new[] { k.Radii };
            after = new[] { (d.KerbValue, d.KerbValue) };
            (atMax, atMin, limit, rules) = (d.KerbValue >= k.Max - 1e-3f, d.KerbValue <= k.Min + 1e-3f, k.MaxLimit, k.Rules);
        }
        else
        {
            var h = d.Kerb!;
            before = h.Radii;
            after = Junctions.KerbHandles(tried, d.KerbNode, Anarchy).FirstOrDefault(x => x.EdgeId == h.EdgeId && x.AtStart == h.AtStart)?.Radii ?? h.Radii;
            (atMax, atMin, limit, rules) = (d.KerbValue >= h.FactorMax - 1e-4f, d.KerbValue <= h.FactorMin + 1e-4f, h.MaxLimit, h.Rules);
        }
        string text = $"{(after.Count > 1 ? "kerbs" : "kerb")} R {Radii(before)} → {Radii(after)} m";
        bool bad = after.Any(r => r.Min < rules.MinKerbRadius - 0.05f), warn = false;
        if (atMax)
        {
            warn = true;
            text += limit switch
            {
                KerbLimit.Fit => " · most that fits",
                KerbLimit.Radius => $" · max R {rules.MaxKerbRadius:0.#} m",
                _ => " · max flare",
            };
        }
        else if (atMin && !bad)
        {
            warn = true;
            text += " · tightest";
        }
        if (bad) text += $" · min {rules.MinKerbRadius:0.#} m · Ctrl+A allows";
        return (new FlashTag(KerbPoint(tried, d), text, Bad: bad), warn);

        static string Radii(IReadOnlyList<(float Min, float Max)> r) =>
            string.Join(" · ", r.Select(x => x.Max - x.Min < 0.05f ? $"{x.Min:0.#}" : $"{x.Min:0.#}–{x.Max:0.#}"));
    }

    /// <summary>The kerb controls to draw: the selected junctions' (as tried while one is held, which keeps the track it
    /// was picked up with), none during any other drag or the menu.</summary>
    private List<KerbMark> KerbMarks(SplineGraph built, Drag? d)
    {
        bool held = d is not null && IsKerb(d.Handle.Kind);
        if ((d is not null && !held) || _menu is not null) return new List<KerbMark>();
        var g = held ? d!.Graph ?? built : built;
        var marks = new List<KerbMark>();
        foreach (int n in SelectedJunctions(g))
        {
            foreach (var k in Junctions.KerbKnobs(g, n, Anarchy))
            {
                var track = held && d!.KerbKnob is { } kk && Same(k, d.Handle)
                    ? new[] { kk.At(kk.Min), kk.At(kk.Max) } : new[] { k.At(k.Min), k.At(k.Max) };
                marks.Add(new KerbMark(k.Position, track, Hot(h => Same(k, h)), k.IsSet, Knob: true));
            }
            foreach (var r in Junctions.KerbHandles(g, n, Anarchy))
            {
                var track = held && d!.Kerb is { } kh && Same(r, d.Handle) ? kh.Track : r.Track;
                marks.Add(new KerbMark(r.Position, track, Hot(h => Same(r, h)), r.IsSet, Knob: false));
            }
        }
        return marks;

        bool Hot(Func<Handle, bool> same) => held ? same(d!.Handle) : _hover is { } hv && same(hv);
    }

    /// <summary>The held junction's kerbs as built, a faint outline while a kerb control is dragged.</summary>
    private static List<IReadOnlyList<NumVector2>> KerbGhosts(SplineGraph built, Drag? d) =>
        d is not null && IsKerb(d.Handle.Kind) && Junctions.Footprint(built, d.KerbNode) is { } f
            ? f.Curbs.Select(c => (IReadOnlyList<NumVector2>)Junctions.ArcPoints(c)).ToList()
            : new List<IReadOnlyList<NumVector2>>();
}
