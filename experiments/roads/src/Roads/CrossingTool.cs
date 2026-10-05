using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.Splines;
using CitySim.Splines.Godot;
using CitySim.TerrainSystem;
using CitySim.UI;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Roads;

/// <summary>
/// The Crossings tool (Roads → Services → Crossings). LMB on a junction (or any node with a crossing place) selects it,
/// and only its arms' crossing places show; LMB on one of them makes its crossing Yes, RMB makes it No. Both stay
/// (<see cref="CrossingMode"/>). LMB anywhere along a road away from a node splits it there with a node, puts a crossing
/// on it centred under the mouse and selects it; RMB on that crossing takes it and the node away again (the road joins
/// back up). RMB or LMB on nothing, or Esc, clears the selection. Every change is one undo step on the network. Only
/// the selected node is drawn, so the overlay costs the same however big the network is. The arm outlines: white for an
/// automatic crossing, green for one set to Yes, red for No, dashed where an automatic arm has none (too close to
/// another or no room). Needs to come after the splines tools and the HUD in the scene, so it sees clicks and Esc first.
/// </summary>
public partial class CrossingTool : Node
{
    [Export] public GameHud? Hud { get; set; }
    [Export] public RoadToolHost? Host { get; set; }
    [Export] public SplineNetwork? Network { get; set; }
    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }

    private static readonly Color Auto = new(1f, 1f, 1f, 0.9f);
    private static readonly Color Off = new(1f, 1f, 1f, 0.45f);
    /// <summary>Room kept between a crossing placed along a road and the road's ends, for the stop lines.</summary>
    private const float EndRoom = 3f;
    /// <summary>How far outside an arm's crossing place the mouse still picks it.</summary>
    private const float Catch = 1.5f;
    /// <summary>The closest a crossing placed along a road may be to another crossing, zebra to zebra.</summary>
    private const float MinSpacing = 12f;

    private readonly SplineToolView _view;
    private Control _overlay = null!;
    private Control _tags = null!; // the mouse tag, on a layer above the HUD so the tray never hides it
    private string? _tag;
    /// <summary>The input drawn as icons before <see cref="_tag"/>'s first line (<see cref="KeyGlyphs"/>), or null.</summary>
    private string? _tagKey;
    private Target? _hover;
    /// <summary><see cref="Refusal"/> per place, until the network changes.</summary>
    private readonly Dictionary<string, string?> _refusals = new();

    /// <summary>What the mouse is on: a node to select (<see cref="ArmEdge"/> &lt; 0), an arm of the selected node's
    /// crossing place, or a point along a road where a new one would go (the edge is split at <see cref="SplitAt"/>,
    /// so the crossing's middle is under the mouse).</summary>
    private sealed record Target(int Node, int ArmEdge, bool AtStart, NumVector2[]? Quad, float? SplitAt);

    public CrossingTool() => _view = new SplineToolView(this);

    /// <summary>Scripted-demo override for the mouse, in plan space (map metres).</summary>
    public NumVector2? ForcedPlanCursor { get => _view.ForcedPlanCursor; set => _view.ForcedPlanCursor = value; }

    /// <summary>The node whose crossings are shown and edited.</summary>
    public int? Selected { get; private set; }

    public void Select(int? node) => Selected = node;

    private bool Active => Hud?.PickedRoadTool?.Tool == RoadTool.CrossingsTool && Network is not null && Host?.Visual is not null;

    public override void _Ready()
    {
        if (Terrain is null || Network is null) { GD.PushError("CrossingTool needs a Terrain and a Network"); return; }
        _view.Terrain = Terrain;
        _view.CityCamera = CityCamera;
        _view.Ground = new TerrainGround(Terrain);
        var layer = new CanvasLayer { Name = "CrossingToolHud" };
        AddChild(layer);
        _overlay = new Control { Name = "CrossingOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.Draw += DrawOverlay;
        layer.AddChild(_overlay);
        Network.Changed += _refusals.Clear;
        var top = new CanvasLayer { Name = "CrossingToolTags", Layer = 2 };
        AddChild(top);
        _tags = new Control { Name = "CrossingTags", MouseFilter = Control.MouseFilterEnum.Ignore };
        _tags.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _tags.Draw += DrawTag;
        top.AddChild(_tags);
    }

    public override void _Process(double delta)
    {
        _hover = null;
        if (!Active) Selected = null;
        else
        {
            // Undo, or a removed crossing along a road, can take the selected node away.
            if (Selected is { } n && !Network!.Graph.HasNode(n)) Selected = null;
            _view.UpdateCursor();
            if (_view.Cursor is { } hit) _hover = TargetAt(_view.PlanOf(hit));
        }
        _overlay.QueueRedraw();
        _tags.QueueRedraw();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        // Esc clears the selection first; with none, it goes on to the HUD (unpicks the tool).
        if (!Active || Selected is null || @event is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Selected = null;
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
        // macOS turns Ctrl+click into a right click; Ctrl means nothing here, so keep it a left click.
        bool add = mb.ButtonIndex == MouseButton.Left || (mb.CtrlPressed && OS.GetName() == "macOS");
        Act(_hover, add);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>What a click at a plan point does (for scripts): LMB with <paramref name="add"/>, else RMB. False when
    /// the point is on nothing (the selection is cleared, as a click there would).</summary>
    public bool ClickAt(NumVector2 plan, bool add)
    {
        if (!Active) return false;
        var t = TargetAt(plan);
        Act(t, add);
        return t is not null;
    }

    private void Act(Target? t, bool add)
    {
        if (t is null || (t.ArmEdge < 0 && t.SplitAt is null && !add)) { Selected = null; return; }
        // A crossing that can't be built isn't: the ghost says why.
        if (add && t.ArmEdge >= 0 && Refusal(t) is not null) return;
        if (t.SplitAt is { } s)
        {
            if (!add) { Selected = null; return; }
            Selected = Network!.Apply(gr =>
            {
                var (node, _, right) = gr.SplitEdge(t.ArmEdge, s);
                if (right is { } r) gr.SetEndData(r, true, new RoadEnd(CrossingMode.Yes));
                return node;
            });
            return;
        }
        if (t.ArmEdge < 0) { Selected = t.Node; return; }

        var g = Network!.Graph;
        var e = g.Edge(t.ArmEdge);
        var mode = Crossings.ModeOf(e, t.AtStart);
        var want = add ? CrossingMode.Yes : CrossingMode.No;
        if (mode == want) return;
        int node = t.Node;
        Network.Apply(gr =>
        {
            // A crossing along a road (a node of its own, nothing else at it): removing it takes the node away.
            if (!add && !Crossings.AtJunction(e, t.AtStart, Network.Footprints) && gr.Arms(node) is { Count: 2 } arms
                && arms.All(a => (a.EdgeId == e.Id && a.AtStart == t.AtStart) || Crossings.ModeOf(gr.Edge(a.EdgeId), a.AtStart) != CrossingMode.Yes))
            {
                var kept = arms.Select(a => (a.EdgeId, a.AtStart, Data: gr.Edge(a.EdgeId).DataAt(a.AtStart))).ToList();
                foreach (var a in kept) gr.SetEndData(a.EdgeId, a.AtStart, null);
                if (gr.TryMerge(node) is not null) return 0;
                foreach (var a in kept) gr.SetEndData(a.EdgeId, a.AtStart, a.Data);
            }
            gr.SetEndData(e.Id, t.AtStart, RoadEnd.Of(gr.Edge(e.Id), t.AtStart) with { Crossing = want });
            return 0;
        });
    }

    /// <summary>
    /// Why a crossing can't go where <paramref name="t"/> is (LMB on an arm, or along a road), or null when it can. Tried
    /// on a copy of the network first: refused when the change raises an issue the road didn't have (a piece too short
    /// between two crossings shows as an overlap), or when the new crossing wouldn't fit or would push a crossing set by
    /// the player off its road. Automatic crossings may give way, as their rules say.
    /// </summary>
    private string? Refusal(Target t)
    {
        if (Crossings.ModeOf(Network!.Graph.Edge(t.ArmEdge), t.AtStart) == CrossingMode.Yes) return null;
        string key = $"{t.ArmEdge}:{t.AtStart}:{t.SplitAt:0.0}";
        if (_refusals.TryGetValue(key, out var cached)) return cached;
        var g = Network.Graph;
        var visual = Host!.Visual!;
        var trial = g.Clone();
        var changed = new List<int> { t.ArmEdge };
        if (t.SplitAt is { } s)
        {
            var (_, left, right) = trial.SplitEdge(t.ArmEdge, s);
            if (left is not { } l || right is not { } r) return _refusals[key] = "Too close to a node";
            trial.SetEndData(r, true, new RoadEnd(CrossingMode.Yes));
            changed = [l, r];
        }
        else trial.SetEndData(t.ArmEdge, t.AtStart, RoadEnd.Of(trial.Edge(t.ArmEdge), t.AtStart) with { Crossing = CrossingMode.Yes });
        Network.Conform(trial);

        string? why = null;
        if (t.SplitAt is not null)
        {
            var had = Network.Issues.Where(i => i.EdgeId == t.ArmEdge).Select(i => i.Code).ToHashSet();
            var nodes = changed.SelectMany(id => new[] { trial.Edge(id).Start, trial.Edge(id).End }).Distinct();
            if (Validation.Check(trial, changed, nodes).Any(i => !had.Contains(i.Code))) why = "Too close to another crossing or a junction";
        }
        if (why is null)
        {
            // Every crossing set by the player keeps its zebra, and the new one gets one.
            var marks = Crossings.Resolve(trial, Junctions.Footprints(trial), visual.SectionOf, visual.SectionStyle);
            int Forced(SplineGraph gr, IReadOnlyDictionary<(int, bool), EndMarks> m) => gr.Edges
                .SelectMany(e => new[] { (e, true), (e, false) })
                .Count(x => Crossings.ModeOf(x.e, x.Item2) == CrossingMode.Yes && m.TryGetValue((x.e.Id, x.Item2), out var k) && k.Zebra);
            if (Forced(trial, marks) != Forced(g, visual.Marks) + 1) why = "No room for a crossing here";
            else if (t.SplitAt is { } at)
            {
                // Not right next to another crossing that stays (automatic ones that close give way, by their rules).
                var fp = Junctions.Footprints(trial);
                var mine = g.Edge(t.ArmEdge).Alignment.Curve.Sample(at + visual.SectionStyle.CrossingWidth / 2).Position;
                foreach (var ((id, atStart), m) in marks)
                {
                    if (!m.Zebra) continue;
                    var e = trial.Edge(id);
                    float u = Crossings.CutOf(e, atStart, fp) + m.ZebraFrom + visual.SectionStyle.CrossingWidth / 2;
                    var c = e.Alignment.Curve.Sample(atStart ? u : e.Alignment.Length - u).Position;
                    float d = NumVector2.Distance(c, mine);
                    if (d > 0.5f && d < MinSpacing) { why = "Too close to another crossing"; break; }
                }
            }
        }
        return _refusals[key] = why;
    }

    /// <summary>What's under a plan point: on the road nearest it, the node whose crossing places it's on or inside
    /// (an arm of it when that node is selected), else a new crossing along that road where there's room.</summary>
    private Target? TargetAt(NumVector2 p)
    {
        var g = Network!.Graph;
        var visual = Host!.Visual!;
        var st = visual.SectionStyle;
        var fp = Network.Footprints;
        Target? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var e in g.Edges)
        {
            var sec = visual.SectionOf(e);
            if (!Crossings.Crossable(sec)) continue;
            var cp = e.Alignment.Curve.ClosestPoint(p);
            float d = NumVector2.Distance(cp.Position, p);
            if (d > sec.HalfWidth || d >= bestDist) continue;
            float len = e.Alignment.Length;
            var (c0, c1) = Junctions.CutBacks(e, fp);
            Target? hit = null;
            foreach (bool atStart in new[] { true, false })
            {
                int node = atStart ? e.Start : e.End;
                if (g.Node(node).Edges.Count < 2) continue;
                var (u0, u1) = Place(e, atStart, st);
                float u = atStart ? cp.S : len - cp.S;
                if (u > u1 + Catch) continue;
                hit = Selected == node && u >= u0 - Catch
                    ? new Target(node, e.Id, atStart, Quad(e, atStart, u0, u1, sec.HalfCarriageway), null)
                    : new Target(node, -1, false, null, null);
            }
            if (hit is null)
            {
                // Along the road: the zebra centred on the mouse, with room for the stop lines on both sides.
                float s0 = cp.S - st.CrossingWidth / 2, s1 = cp.S + st.CrossingWidth / 2;
                if (s0 < c0 + EndRoom || s1 > len - c1 - EndRoom) continue;
                hit = new Target(-1, e.Id, false, Quad(e, true, s0, s1, sec.HalfCarriageway), s0);
            }
            best = hit;
            bestDist = d;
        }
        return best;
    }

    /// <summary>Where an arm's zebra goes, as stations from the node along the arm.</summary>
    private (float From, float To) Place(GraphEdge e, bool atStart, Geometry.SectionStyle st)
    {
        var fp = Network!.Footprints;
        float from = Crossings.CutOf(e, atStart, fp) + Crossings.ZebraFrom(Crossings.AtJunction(e, atStart, fp), st);
        return (from, from + st.CrossingWidth);
    }

    /// <summary>The corners of a stretch across the carriageway, from <paramref name="u0"/> to <paramref name="u1"/>
    /// measured from one end.</summary>
    private static NumVector2[] Quad(GraphEdge e, bool fromStart, float u0, float u1, float half)
    {
        var c = e.Alignment.Curve;
        float len = c.Length;
        var pts = new List<NumVector2>();
        foreach (var (u, side) in new[] { (u0, 1), (u1, 1), (u1, -1), (u0, -1) })
        {
            var sample = c.Sample(Math.Clamp(fromStart ? u : len - u, 0, len));
            pts.Add(sample.Position + SplineMath.Left(sample.Tangent) * (half * side));
        }
        return pts.ToArray();
    }

    private void DrawOverlay()
    {
        _tag = _tagKey = null;
        if (!Active) return;
        var g = Network!.Graph;
        var visual = Host!.Visual!;
        if (_hover is { Node: >= 0 } hn && hn.Node != Selected) Ring(hn.Node, Auto, dashed: true);
        if (Selected is { } sel)
        {
            Ring(sel, SplineOverlay.Accent, dashed: false);
            foreach (var arm in g.Arms(sel))
            {
                var e = g.Edge(arm.EdgeId);
                var sec = visual.SectionOf(e);
                if (!Crossings.Crossable(sec)) continue;
                var mode = Crossings.ModeOf(e, arm.AtStart);
                bool zebra = visual.Marks.TryGetValue((e.Id, arm.AtStart), out var m) && m.Zebra;
                bool hovered = _hover is { ArmEdge: >= 0, SplitAt: null } h && h.ArmEdge == e.Id && h.AtStart == arm.AtStart;
                var (u0, u1) = Place(e, arm.AtStart, visual.SectionStyle);
                var quad = Quad(e, arm.AtStart, u0, u1, sec.HalfCarriageway);
                var color = mode switch
                {
                    CrossingMode.Yes => zebra ? SplineOverlay.Good : SplineOverlay.Warn,
                    CrossingMode.No => SplineOverlay.Bad,
                    _ => zebra ? Auto : Off,
                };
                if (hovered) Fill(quad, color with { A = 0.25f });
                Outline(quad, color, dashed: !zebra && mode != CrossingMode.No, width: hovered ? 3.5f : 2f);
                if (mode == CrossingMode.No) Cross(quad, color);
            }
        }

        if (_hover is not { } t) return;
        string text;
        string? key = null;
        if (t.SplitAt is not null)
        {
            var why = Refusal(t);
            var c = why is null ? SplineOverlay.Accent : SplineOverlay.Bad;
            Fill(t.Quad!, c with { A = 0.35f });
            Outline(t.Quad!, c, dashed: false, width: 3f);
            (key, text) = why is null ? ("LMB", "Add crossing") : (null, why);
        }
        else if (t.ArmEdge < 0)
        {
            if (t.Node == Selected) return;
            (key, text) = ("LMB", "Select");
        }
        else
        {
            var e = g.Edge(t.ArmEdge);
            var mode = Crossings.ModeOf(e, t.AtStart);
            bool zebra = visual.Marks.TryGetValue((e.Id, t.AtStart), out var m) && m.Zebra;
            string state = mode switch
            {
                CrossingMode.Yes => zebra ? "Crossing" : "Crossing · no room",
                CrossingMode.No => "No crossing",
                _ => zebra ? "Crossing (auto)" : "No crossing (auto)",
            };
            // The other side of a crossing along a road: a crossing there too makes one twice as wide.
            bool widen = mode == CrossingMode.Auto && !Crossings.AtJunction(e, t.AtStart, Network.Footprints)
                && g.Arms(t.Node).Any(a => a.EdgeId != e.Id && visual.Marks.TryGetValue((a.EdgeId, a.AtStart), out var o) && o.Zebra);
            if (mode != CrossingMode.Yes && Refusal(t) is { } why) text = $"{state}\n{why}";
            else if (widen) (key, text) = ("LMB", "Widen crossing");
            else text = state;
        }
        _tag = text;
        _tagKey = key;
    }

    private void DrawTag()
    {
        if (_tag is null || !Active) return;
        var mouse = _view.MouseScreen() + new Vector2(18, 14);
        var font = ThemeDB.FallbackFont;
        var lines = _tag.Split('\n');
        float keyWidth = _tagKey is null ? 0 : KeyGlyphs.Width(_tagKey, font, 13, 18) + 6;
        float w = lines.Select((l, i) => font.GetStringSize(l, HorizontalAlignment.Left, -1, 13).X + (i == 0 ? keyWidth : 0)).Max() + 16;
        _tags.DrawRect(new Rect2(mouse, new Vector2(w, lines.Length * 18 + 8)), new Color("#1C2629", 0.92f));
        if (_tagKey is not null) KeyGlyphs.Draw(_tags, mouse + new Vector2(8, 19), _tagKey, font, 13, 18, SplineOverlay.Accent);
        for (int i = 0; i < lines.Length; i++)
            _tags.DrawString(font, mouse + new Vector2(8 + (i == 0 ? keyWidth : 0), 19 + i * 18), lines[i], HorizontalAlignment.Left, -1, 13, new Color("#F4F1E6"));
    }

    /// <summary>A ring on the ground round a node, out past its arms' crossing places.</summary>
    private void Ring(int nodeId, Color color, bool dashed)
    {
        var g = Network!.Graph;
        var visual = Host!.Visual!;
        float r = g.Arms(nodeId).Select(a => MathF.Max(visual.SectionOf(g.Edge(a.EdgeId)).HalfWidth,
            Place(g.Edge(a.EdgeId), a.AtStart, visual.SectionStyle).To)).DefaultIfEmpty(8).Max() + 2;
        var centre = g.Node(nodeId).Position;
        const int k = 48;
        Vector2? prev = null;
        for (int i = 0; i <= k; i++)
        {
            if (_view.ProjectPlan(centre + SplineMath.Direction(MathF.Tau * i / k) * r) is not { } p) { prev = null; continue; }
            if (prev is { } a && (!dashed || i % 2 == 0)) _overlay.DrawLine(a, p, color, 2.5f, antialiased: true);
            prev = p;
        }
    }

    private Vector2[]? Screen(NumVector2[] quad)
    {
        var pts = new Vector2[quad.Length];
        for (int i = 0; i < quad.Length; i++)
        {
            if (_view.ProjectPlan(quad[i]) is not { } p) return null;
            pts[i] = p;
        }
        return pts;
    }

    private void Outline(NumVector2[] quad, Color color, bool dashed, float width)
    {
        if (Screen(quad) is not { } pts) return;
        for (int i = 0; i < pts.Length; i++)
        {
            var (a, b) = (pts[i], pts[(i + 1) % pts.Length]);
            if (dashed) _overlay.DrawDashedLine(a, b, color, width, 6f);
            else _overlay.DrawLine(a, b, color, width, antialiased: true);
        }
    }

    private void Fill(NumVector2[] quad, Color color)
    {
        if (Screen(quad) is { } pts) _overlay.DrawColoredPolygon(pts, color);
    }

    private void Cross(NumVector2[] quad, Color color)
    {
        if (Screen(quad) is not { } pts) return;
        _overlay.DrawLine(pts[0], pts[2], color, 2f, antialiased: true);
        _overlay.DrawLine(pts[1], pts[3], color, 2f, antialiased: true);
    }
}
