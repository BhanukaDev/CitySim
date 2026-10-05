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
/// The Lane Links tool (Roads → Services → Lane Links). LMB on a junction selects it: a dot shows at every lane at each
/// mouth (filled = coming in, ring = going out) and a curve for every lane link (<see cref="LaneLinks"/>). LMB on a link
/// picks it; RMB or Del then removes the picked link (never the hovered one). Dragging from a lane's dot to a lane on
/// the other side of the link (in to out, or out to in) and letting go links them; let go anywhere else and nothing
/// changes. Esc steps back: drag, link, selection. Every change is one undo step on the network. Only the selected junction is drawn, so the overlay costs
/// the same however big the network is. Needs to come after the splines tools and the HUD in the scene, so it sees
/// clicks and keys first.
/// </summary>
public partial class LaneLinkTool : Node
{
    [Export] public GameHud? Hud { get; set; }
    [Export] public RoadToolHost? Host { get; set; }
    [Export] public SplineNetwork? Network { get; set; }
    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }

    private static readonly Color Line = new(1f, 1f, 1f, 0.85f);
    private static readonly Color Faint = new(1f, 1f, 1f, 0.22f);
    private static readonly Color Under = new(0.05f, 0.07f, 0.08f, 0.55f);
    /// <summary>How far from a lane dot (m) the mouse still picks it.</summary>
    private const float DotCatch = 1.2f;
    /// <summary>How far from a link's curve (m) the mouse still picks it.</summary>
    private const float LinkCatch = 0.9f;

    private readonly SplineToolView _view;
    private Control _overlay = null!;
    private Control _tags = null!;
    private string? _tag;
    private string? _tagKey;
    private Target? _hover;

    /// <summary>A lane end at a junction: an arm's end and a lane, kerbside first.</summary>
    public readonly record struct LaneEnd(int Edge, bool AtStart, int Lane);

    /// <summary>A link named by its ends, so it's still found after the network rebuilds.</summary>
    public readonly record struct LinkKey(LaneEnd From, LaneEnd To);

    private enum Kind { Node, In, Out, Link }

    /// <summary>What the mouse is on: a junction to select, a lane dot of the selected one, or one of its links.</summary>
    private sealed record Target(Kind Kind, int Node, LaneEnd Lane = default, LinkKey Link = default);

    /// <summary>The selected junction's lanes and links, as of this frame.</summary>
    private sealed record View(int Node, List<ArmLanes> Arms, List<LaneLink> Links, bool Edited);

    private View? _sel;

    public LaneLinkTool() => _view = new SplineToolView(this);

    /// <summary>Scripted-demo override for the mouse, in plan space (map metres).</summary>
    public NumVector2? ForcedPlanCursor { get => _view.ForcedPlanCursor; set => _view.ForcedPlanCursor = value; }

    /// <summary>The junction whose links are shown and edited.</summary>
    public int? Selected { get; private set; }
    /// <summary>The link RMB / Del removes.</summary>
    public LinkKey? Picked { get; private set; }
    /// <summary>The lane a drag started on while LMB is held (going out with <see cref="DragFromOut"/>, else coming in).</summary>
    public LaneEnd? DragFrom { get; private set; }
    public bool DragFromOut { get; private set; }

    public void Select(int? node)
    {
        Selected = node;
        Picked = null;
        DragFrom = null;
        Refresh();
    }

    private bool Active => Hud?.PickedRoadTool?.Tool == RoadTool.LaneLinksTool && Network is not null && Host?.Visual is not null;

    public override void _Ready()
    {
        if (Terrain is null || Network is null) { GD.PushError("LaneLinkTool needs a Terrain and a Network"); return; }
        _view.Terrain = Terrain;
        _view.CityCamera = CityCamera;
        _view.Ground = new TerrainGround(Terrain);
        var layer = new CanvasLayer { Name = "LaneLinkToolHud" };
        AddChild(layer);
        _overlay = new Control { Name = "LaneLinkOverlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.Draw += DrawOverlay;
        layer.AddChild(_overlay);
        var top = new CanvasLayer { Name = "LaneLinkToolTags", Layer = 2 };
        AddChild(top);
        _tags = new Control { Name = "LaneLinkTags", MouseFilter = Control.MouseFilterEnum.Ignore };
        _tags.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _tags.Draw += DrawTag;
        top.AddChild(_tags);
    }

    public override void _Process(double delta)
    {
        _hover = null;
        if (!Active) { Selected = null; Picked = null; DragFrom = null; _sel = null; }
        else
        {
            Refresh();
            _view.UpdateCursor();
            if (_view.Cursor is { } hit) _hover = TargetAt(_view.PlanOf(hit));
        }
        _overlay.QueueRedraw();
        _tags.QueueRedraw();
    }

    /// <summary>Reads the selected junction's lanes and links again; drops a selection, pick or lane that's gone (undo,
    /// a deleted road).</summary>
    private void Refresh()
    {
        _sel = null;
        if (Selected is not { } n || Network is null || Host?.Visual is not { } visual) return;
        var g = Network.Graph;
        if (!g.HasNode(n) || !Network.Footprints.TryGetValue(n, out var f) || !IsJunction(f) || visual.LanesAt(g, f) is not { } arms)
        {
            Selected = null;
            Picked = null;
            DragFrom = null;
            return;
        }
        var links = LaneLinks.Resolve(g, arms);
        _sel = new View(n, arms, links, LaneLinks.Edited(g, arms));
        if (Picked is { } p && !links.Any(l => KeyOf(arms, l) == p)) Picked = null;
        if (DragFrom is { } d && (Index(arms, d) is not { } k || d.Lane >= (DragFromOut ? arms[k].Out : arms[k].In).Count)) DragFrom = null;
    }

    /// <summary>A junction of three or more roads, or a road changing width or moving sideways (a transition), where lanes
    /// merge or branch.</summary>
    private static bool IsJunction(JunctionFootprint f) => f.Continuous || (!f.Bend && f.Cuts.Count >= 3);

    private static int? Index(List<ArmLanes> arms, LaneEnd e)
    {
        int i = arms.FindIndex(a => a.Edge == e.Edge && a.AtStart == e.AtStart);
        return i < 0 ? null : i;
    }

    private static LinkKey KeyOf(List<ArmLanes> arms, LaneLink l) => new(
        new LaneEnd(arms[l.From].Edge, arms[l.From].AtStart, l.FromLane), new LaneEnd(arms[l.To].Edge, arms[l.To].AtStart, l.ToLane));

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventKey { Pressed: true, Echo: false } key || key.IsCommandOrControlPressed()) return;
        switch (key.Keycode)
        {
            case Key.Escape when Selected is not null:
                // Steps back one level; with nothing selected, Esc goes on to the HUD (unpicks the tool).
                if (DragFrom is not null) DragFrom = null;
                else if (Picked is not null) Picked = null;
                else Selected = null;
                break;
            case Key.Delete or Key.Backspace when Picked is not null:
                RemovePicked();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventMouseButton mb) return;
        if (!mb.Pressed)
        {
            // Letting go of a drag links the lanes under the mouse.
            if (mb.ButtonIndex != MouseButton.Left || DragFrom is null) return;
            Drop(_hover);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
        // macOS turns Ctrl+click into a right click; Ctrl means nothing here, so keep it a left click.
        bool left = mb.ButtonIndex == MouseButton.Left || (mb.CtrlPressed && OS.GetName() == "macOS");
        Act(_hover, left);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>A drag from one plan point to another (for scripts). False when it didn't start on a lane's dot.</summary>
    public bool DragAt(NumVector2 from, NumVector2 to)
    {
        if (!StartDragAt(from)) return false;
        Drop(TargetAt(to));
        return true;
    }

    /// <summary>LMB held down at a plan point (for scripts): true when that starts a drag from a lane's dot.</summary>
    public bool StartDragAt(NumVector2 at)
    {
        if (!Active) return false;
        Refresh();
        Act(TargetAt(at), true);
        return DragFrom is not null;
    }

    /// <summary>What a click at a plan point does (for scripts): LMB with <paramref name="left"/>, else RMB. False when
    /// the point is on nothing.</summary>
    public bool ClickAt(NumVector2 plan, bool left)
    {
        if (!Active) return false;
        Refresh();
        var t = TargetAt(plan);
        Act(t, left);
        return t is not null;
    }

    private void Act(Target? t, bool left)
    {
        if (!left)
        {
            // RMB acts on the picked link, wherever the mouse is; never on a hovered one.
            if (DragFrom is not null) DragFrom = null;
            else if (Picked is not null) RemovePicked();
            return;
        }
        switch (t?.Kind)
        {
            case null:
                if (Picked is not null) Picked = null;
                else Selected = null;
                break;
            case Kind.Node:
                Select(t.Node);
                break;
            case Kind.In or Kind.Out:
                // A drag starts; it links on release (Drop).
                DragFrom = t.Lane;
                DragFromOut = t.Kind == Kind.Out;
                Picked = null;
                break;
            case Kind.Link:
                Picked = Picked == t.Link ? null : t.Link;
                DragFrom = null;
                break;
        }
    }

    /// <summary>The end of a drag: on a lane on the other side of a link from where it started, links the two.</summary>
    private void Drop(Target? t)
    {
        if (DragFrom is not { } from) return;
        DragFrom = null;
        if (DropKey(from, t) is { } key) Link(key);
    }

    /// <summary>The link a drag from <paramref name="from"/> would make if let go on <paramref name="t"/>, or null.</summary>
    private LinkKey? DropKey(LaneEnd from, Target? t) => t?.Kind switch
    {
        Kind.Out when !DragFromOut => new LinkKey(from, t.Lane),
        Kind.In when DragFromOut => new LinkKey(t.Lane, from),
        _ => null,
    };

    /// <summary>Adds a link (one undo step); picks it if it's already there.</summary>
    public void Link(LinkKey key)
    {
        if (_sel is not { } s || Index(s.Arms, key.From) is not { } i || Index(s.Arms, key.To) is not { } j) return;
        if (s.Links.Any(l => KeyOf(s.Arms, l) == key)) { Picked = key; DragFrom = null; return; }
        var links = s.Links.Append(new LaneLink(i, key.From.Lane, j, key.To.Lane, LaneLinks.MoveOf(s.Arms[i], s.Arms[j]))).ToList();
        Network!.Apply(g => { LaneLinks.Set(g, s.Arms, links); return 0; });
        Refresh();
    }

    /// <summary>Removes the picked link (one undo step).</summary>
    public void RemovePicked()
    {
        if (_sel is not { } s || Picked is not { } p) return;
        var links = s.Links.Where(l => KeyOf(s.Arms, l) != p).ToList();
        Picked = null;
        Network!.Apply(g => { LaneLinks.Set(g, s.Arms, links); return 0; });
        Refresh();
    }

    /// <summary>The links of the selected junction as their keys (for scripts and the demo).</summary>
    public List<(LinkKey Key, Move Move)> Links() =>
        _sel is { } s ? s.Links.Select(l => (KeyOf(s.Arms, l), l.Move)).ToList() : new();

    /// <summary>The middle of a link's curve (for scripts).</summary>
    public NumVector2? LinkPoint(LinkKey key)
    {
        if (_sel is not { } s || s.Links.FindIndex(l => KeyOf(s.Arms, l) == key) is not (>= 0 and var i)) return null;
        var pts = LaneLinks.Path(s.Arms, s.Links[i]).ToList();
        return pts.Count == 0 ? null : pts[pts.Count / 2].P;
    }

    /// <summary>Where a lane's dot is (for scripts): the lane coming in, or with <paramref name="outgoing"/> going out.</summary>
    public NumVector2? LanePoint(LaneEnd lane, bool outgoing)
    {
        if (_sel is not { } s || Index(s.Arms, lane) is not { } i) return null;
        var list = outgoing ? s.Arms[i].Out : s.Arms[i].In;
        return lane.Lane < list.Count ? list[lane.Lane] : null;
    }

    /// <summary>What's under a plan point: on the selected junction a lane dot, else a link; else a junction.</summary>
    private Target? TargetAt(NumVector2 p)
    {
        var g = Network!.Graph;
        if (_sel is { } s)
        {
            Target? best = null;
            float bestD = DotCatch;
            for (int i = 0; i < s.Arms.Count; i++)
            {
                var a = s.Arms[i];
                for (int k = 0; k < a.In.Count; k++)
                    if (NumVector2.Distance(a.In[k], p) is var d && d < bestD) (best, bestD) = (new Target(Kind.In, s.Node, new LaneEnd(a.Edge, a.AtStart, k)), d);
                for (int k = 0; k < a.Out.Count; k++)
                    if (NumVector2.Distance(a.Out[k], p) is var d && d < bestD) (best, bestD) = (new Target(Kind.Out, s.Node, new LaneEnd(a.Edge, a.AtStart, k)), d);
            }
            // While dragging only lanes are targets.
            if (best is not null || DragFrom is not null) return best;
            bestD = LinkCatch;
            foreach (var l in s.Links)
            {
                var key = KeyOf(s.Arms, l);
                var pts = LaneLinks.Path(s.Arms, l).Select(x => x.P).ToList();
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (SegmentDistance(p, pts[i], pts[i + 1]) is var d && d < bestD) (best, bestD) = (new Target(Kind.Link, s.Node, Link: key), d);
            }
            if (best is not null) return best;
        }
        // A junction: the mouse inside its ring.
        foreach (var (node, f) in Network.Footprints)
        {
            if (!IsJunction(f) || node == Selected) continue;
            if (NumVector2.Distance(g.Node(node).Position, p) < RingRadius(f)) return new Target(Kind.Node, node);
        }
        return null;
    }

    private static float SegmentDistance(NumVector2 p, NumVector2 a, NumVector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-9f ? 0 : Math.Clamp(NumVector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return NumVector2.Distance(p, a + ab * t);
    }

    private float RingRadius(JunctionFootprint f)
    {
        var centre = Network!.Graph.Node(f.NodeId).Position;
        return f.Outline.Select(q => NumVector2.Distance(q, centre)).DefaultIfEmpty(10).Max() * 0.8f + 1;
    }

    private void DrawOverlay()
    {
        _tag = _tagKey = null;
        if (!Active) return;
        if (_hover is { Kind: Kind.Node } hn) Ring(hn.Node, Line, dashed: true);
        if (_sel is { } s)
        {
            Ring(s.Node, SplineOverlay.Accent, dashed: false);
            LinkKey? hovered = _hover is { Kind: Kind.Link } hl ? hl.Link : null;
            LinkKey? ghost = DragFrom is { } from ? DropKey(from, _hover) : null;
            foreach (var l in s.Links)
            {
                var key = KeyOf(s.Arms, l);
                if (key == Picked) continue;
                bool dim = DragFrom is { } d && (DragFromOut ? key.To : key.From) != d;
                Curve(LaneLinks.Path(s.Arms, l), dim ? Faint : Line, key == hovered ? 3.5f : 2f, dashed: false);
            }
            if (Picked is { } pk && s.Links.FindIndex(l => KeyOf(s.Arms, l) == pk) is >= 0 and var pi)
            {
                Curve(LaneLinks.Path(s.Arms, s.Links[pi]), Under, 7f, dashed: false);
                Curve(LaneLinks.Path(s.Arms, s.Links[pi]), SplineOverlay.Accent, 4f, dashed: false);
            }
            if (ghost is { } gk && Index(s.Arms, gk.From) is { } gi && Index(s.Arms, gk.To) is { } gj)
            {
                Curve(LaneLinks.Path(s.Arms, gi, gk.From.Lane, gj, gk.To.Lane), SplineOverlay.Accent, 3f, dashed: true);
            }
            else if (DragFrom is { } df && Index(s.Arms, df) is { } di && _view.Cursor is { } cur)
            {
                // Not over a lane it can link to: the drag runs straight to the mouse.
                var arm = s.Arms[di];
                var start = DragFromOut ? arm.Out[df.Lane] : arm.In[df.Lane];
                var at = _view.PlanOf(cur);
                if (NumVector2.Distance(start, at) > 0.5f)
                    Curve(Enumerable.Range(0, 25).Select(i => (NumVector2.Lerp(start, at, i / 24f), NumVector2.Zero)), SplineOverlay.Accent with { A = 0.7f }, 3f, dashed: true);
            }
            Dots(s);
        }

        if (_hover is not { } t)
        {
            if (_sel is { } st && _view.Cursor is { } c && Network!.Footprints.TryGetValue(st.Node, out var f)
                && NumVector2.Distance(_view.PlanOf(c), Network.Graph.Node(st.Node).Position) < RingRadius(f))
                _tag = $"{(st.Edited ? "Edited" : "Auto")} · {st.Links.Count} links";
            return;
        }
        switch (t.Kind)
        {
            case Kind.Node:
                (_tagKey, _tag) = ("LMB", "Select");
                break;
            case Kind.In or Kind.Out when DragFrom is { } from:
                if (DropKey(from, t) is { } key)
                    _tag = _sel!.Links.Any(l => KeyOf(_sel.Arms, l) == key) ? "Linked" : $"Link · {MoveName(key.From, key.To)}";
                break;
            case Kind.In:
                int n = _sel!.Links.Count(l => KeyOf(_sel.Arms, l).From == t.Lane);
                (_tagKey, _tag) = ("drag", n == 0 ? "Link\nNo links" : "Link");
                break;
            case Kind.Out:
                (_tagKey, _tag) = ("drag", "Link");
                break;
            case Kind.Link:
                string name = MoveName(t.Link.From, t.Link.To);
                (_tagKey, _tag) = t.Link == Picked ? ("RMB · Del", $"Remove · {name}") : ("LMB", $"Pick · {name}");
                break;
        }
    }

    private string MoveName(LaneEnd from, LaneEnd to)
    {
        if (_sel is not { } s || Index(s.Arms, from) is not { } i || Index(s.Arms, to) is not { } j) return "";
        return LaneLinks.MoveOf(s.Arms[i], s.Arms[j]) switch
        {
            Move.Straight => "Straight",
            Move.Right => "Right",
            Move.Left => "Left",
            _ => "U-turn",
        };
    }

    /// <summary>A dot per lane at each mouth: filled for a lane coming in (red with no links left, accent when picked),
    /// a ring for a lane going out (accent when it can be linked to).</summary>
    private void Dots(View s)
    {
        foreach (var a in s.Arms)
        {
            for (int k = 0; k < a.In.Count; k++)
            {
                if (_view.ProjectPlan(a.In[k]) is not { } p) continue;
                var lane = new LaneEnd(a.Edge, a.AtStart, k);
                bool picked = DragFrom == lane && !DragFromOut, hovered = _hover is { Kind: Kind.In } h && h.Lane == lane;
                bool target = DragFrom is not null && DragFromOut;
                bool dead = !s.Links.Any(l => KeyOf(s.Arms, l).From == lane);
                var fill = picked ? SplineOverlay.Accent : dead ? SplineOverlay.Bad : Colors.White;
                float r = picked || hovered ? 7.5f : 6f;
                _overlay.DrawCircle(p, r + 2, target ? SplineOverlay.Accent : Under with { A = 0.9f });
                _overlay.DrawCircle(p, r, fill);
            }
            for (int k = 0; k < a.Out.Count; k++)
            {
                if (_view.ProjectPlan(a.Out[k]) is not { } p) continue;
                var lane = new LaneEnd(a.Edge, a.AtStart, k);
                bool target = DragFrom is not null && !DragFromOut, hovered = _hover is { Kind: Kind.Out } h && h.Lane == lane;
                bool source = DragFrom == lane && DragFromOut;
                float r = hovered && target || source ? 8f : 6f;
                _overlay.DrawCircle(p, r + 1, source ? SplineOverlay.Accent : Under);
                _overlay.DrawArc(p, r, 0, MathF.Tau, 24, target ? SplineOverlay.Accent : Colors.White, hovered && target ? 3f : 2f, antialiased: true);
            }
        }
    }

    /// <summary>A link's curve on screen with an arrowhead at its end.</summary>
    private void Curve(IEnumerable<(NumVector2 P, NumVector2 D)> path, Color color, float width, bool dashed)
    {
        var pts = new List<Vector2>();
        foreach (var (p, _) in path)
            if (_view.ProjectPlan(p) is { } s) pts.Add(s);
        if (pts.Count < 2) return;
        if (dashed)
            for (int i = 0; i + 1 < pts.Count; i += 2) _overlay.DrawLine(pts[i], pts[i + 1], color, width, antialiased: true);
        else _overlay.DrawPolyline(pts.ToArray(), color, width, antialiased: true);
        var tip = pts[^1];
        var dir = (tip - pts[^2]).Normalized();
        var side = new Vector2(-dir.Y, dir.X);
        float len = 6f + width * 1.5f;
        _overlay.DrawColoredPolygon([tip + dir * 2, tip - dir * len + side * len * 0.55f, tip - dir * len - side * len * 0.55f], color);
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
            _tags.DrawString(font, mouse + new Vector2(8 + (i == 0 ? keyWidth : 0), 19 + i * 18), lines[i], HorizontalAlignment.Left, -1, 13,
                i == 0 ? new Color("#F4F1E6") : SplineOverlay.Bad);
    }

    /// <summary>A ring on the ground round a junction.</summary>
    private void Ring(int nodeId, Color color, bool dashed)
    {
        if (!Network!.Footprints.TryGetValue(nodeId, out var f)) return;
        float r = RingRadius(f) + 3;
        var centre = Network.Graph.Node(nodeId).Position;
        const int k = 48;
        Vector2? prev = null;
        for (int i = 0; i <= k; i++)
        {
            if (_view.ProjectPlan(centre + SplineMath.Direction(MathF.Tau * i / k) * r) is not { } p) { prev = null; continue; }
            if (prev is { } a && (!dashed || i % 2 == 0)) _overlay.DrawLine(a, p, color, 2.5f, antialiased: true);
            prev = p;
        }
    }
}
