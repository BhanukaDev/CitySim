using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Edit tool (<c>M</c>; DESIGN.md → Edit tool, ROADMAP.md S5): click a built edge to select it (Shift+click adds
/// or removes one; on a road with corners, just the stretch between two of its dots: see <c>.Stretch.cs</c>), then drag its corner points or the radius knob in the middle of each arc; any node can be dragged.
/// Connected edges follow and keep their radii, Alt-drag slides a corner point onto its neighbours' line
/// (straighten), and the knob takes the same radius limits as drawing. Every frame of a drag is tried on a copy of the
/// graph and drawn in place of the built one, the old shape left as a faint outline. On release it's joined to the
/// network as a draw would be (<see cref="SplineGraph.Reconnect"/>) and built as one undo step, unless it has an
/// Invalid issue and Anarchy is off: then it springs back with a red flash. Delete removes the selection.
/// RMB on a corner point or a node opens the radial menu (Smooth · Hard · Straight · Delete), each action tried live
/// while hovered. A drag on empty ground box-selects edges and nodes (Shift adds); dragging a selected edge or node
/// moves the whole selection, edges with both ends in it rigidly and the rest stretching. A selected junction shows
/// a kerb handle on each arm (<c>.Kerbs.cs</c>).
/// </summary>
public partial class SplineEditTool : Node
{
    private const float CatchPixels = 8f;
    private const float PickPixels = 12f;
    private const float DragPixels = 4f;
    private const double FlashSeconds = 3.0;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    /// <summary>The consumer's UI node; it must implement <see cref="ISplineToolHost"/>.</summary>
    [Export] public Node? HostNode { get; set; }
    private ISplineToolHost? Host => HostNode as ISplineToolHost;
    [Export] public SplineNetwork? Network { get; set; }

    private const float MenuInnerPixels = 16f;
    private const float MenuOuterPixels = 60f;

    private enum HandleKind { Node, Pi, Knob, Group, Stretch, Kerb, KerbKnob }

    /// <summary>The radial menu's actions, in the storyboard's order round the circle (top, right, bottom, left).</summary>
    private enum MenuAction { Smooth, Hard, Straighten, Delete }
    private static readonly (MenuAction Action, string Label, string Verb, float Angle)[] MenuItems =
    {
        (MenuAction.Smooth, "Smooth", "smooth", -90), (MenuAction.Hard, "Hard", "make hard", 0),
        (MenuAction.Straighten, "Straight", "straighten", 90), (MenuAction.Delete, "Delete", "delete", 180),
    };

    /// <summary>The open radial menu: the corner point or node it's for, which actions apply, and the hovered one
    /// tried on a copy of the graph.</summary>
    private sealed class Menu
    {
        public required Handle Target { get; init; }
        public required bool[] Enabled { get; init; }
        /// <summary>RMB still down from opening it: letting go over an action chooses it.</summary>
        public bool Held { get; set; } = true;
        public int? Hovered { get; set; }
        public int? TriedFor { get; set; }
        public SplineGraph? Graph { get; set; }
        public EditResult? Result { get; set; }
        public List<Issue> Issues { get; set; } = new();
    }

    /// <summary>Something to drag: a node (<see cref="Id"/> is the node), or interior PI <see cref="Index"/> of edge
    /// <see cref="Id"/> by its point or its radius knob. A <see cref="HandleKind.Stretch"/> (a radial menu's target) is
    /// stretch <see cref="Index"/> of edge <see cref="Id"/>. A <see cref="HandleKind.Kerb"/> is the road handle at edge
    /// <see cref="Id"/>'s start (<see cref="Index"/> 1) or end (0); a <see cref="HandleKind.KerbKnob"/> the knob of the
    /// kerb on that end's right-hand side.</summary>
    private readonly record struct Handle(HandleKind Kind, int Id, int Index, NumVector2 At);

    /// <summary>A drag in progress: what's held, where it's going, and the tried result.</summary>
    private sealed class Drag
    {
        public required Handle Handle { get; init; }
        /// <summary>From the cursor to the handle when it was picked up, so it doesn't jump to the cursor.</summary>
        public required NumVector2 Grab { get; init; }
        /// <summary>The built edges it changes (ids in the built graph).</summary>
        public required IReadOnlyList<int> Edges { get; init; }
        public required ProfileRules Rules { get; init; }
        /// <summary>The held corner's point (a knob drag finds its corner by it after a reconnect renumbers edges).</summary>
        public NumVector2 Pi { get; init; }
        public NumVector2 Target { get; set; }
        public float Radius { get; set; }
        public SplineGraph? Graph { get; set; }
        public EditResult? Result { get; set; }
        public List<Issue> Issues { get; set; } = new();
        public Severity? Worst => Validation.Worst(Issues);
        /// <summary>A group move: the selected nodes and edges it moves (the held node, if any, is <see cref="Handle"/>'s id).</summary>
        public IReadOnlyList<int> GroupNodes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> GroupEdges { get; init; } = Array.Empty<int>();
        public IReadOnlyList<Stretch> GroupStretches { get; init; } = Array.Empty<Stretch>();
        public NumVector2 Delta => Target - Handle.At;
        /// <summary>A kerb control drag: the road handle (its factor now) or knob (its radius now) as picked up, and its junction.</summary>
        public KerbHandle? Kerb { get; init; }
        public KerbKnob? KerbKnob { get; init; }
        public int KerbNode { get; init; }
        public float KerbValue { get; set; }
    }

    private readonly SplineToolView _view;
    private readonly HashSet<int> _selected = new();
    private readonly HashSet<int> _selectedNodes = new();
    private readonly List<(FlashTag Tag, double Until)> _flashes = new();
    private SplineOverlay? _overlay;
    private SplineIssueList? _issueList;
    private Handle? _hover;
    private int? _hoverEdge;
    private SnapResult? _snap;
    // LMB held: where it went down, and the handle under it (a drag starts once the mouse moves).
    private Vector2? _pressAt;
    private Handle? _pressHandle;
    private Drag? _drag;
    private int? _pressEdge;
    private bool _pressShift;
    // A box select in progress: where it started on screen, and what it would select now.
    private Vector2? _boxFrom;
    private List<int> _boxEdges = new(), _boxNodes = new();
    private readonly List<Stretch> _boxStretches = new();
    private Menu? _menu;
    private int? _forcedMenuItem;

    public SplineEditTool() => _view = new SplineToolView(this);

    /// <summary>Scripted-demo override: a plan-space position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get => _view.ForcedPlanCursor; set => _view.ForcedPlanCursor = value; }
    /// <summary>Scripted-demo override: modifier keys treated as held.</summary>
    public DrawModifiers ForcedModifiers { get => _view.ForcedModifiers; set => _view.ForcedModifiers = value; }
    public IReadOnlyCollection<int> Selected => _selected;
    public IReadOnlyCollection<int> SelectedNodes => _selectedNodes;

    private bool Active => Host is { Tool: SplineTool.Edit };

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineEditTool needs a Terrain"); return; }
        _view.Terrain = Terrain;
        _view.CityCamera = CityCamera;
        _view.Ground = new TerrainGround(Terrain);
        var layer = new CanvasLayer { Name = "SplineEditHud" };
        AddChild(layer);
        _overlay = new SplineOverlay { Project = _view.ProjectPlan };
        layer.AddChild(_overlay);
        _issueList = new SplineIssueList();
        layer.AddChild(_issueList);
        if (Network is not null)
            Network.Changed += () =>
            {
                _selected.RemoveWhere(id => !Network.Graph.HasEdge(id));
                _selectedNodes.RemoveWhere(id => !Network.Graph.HasNode(id));
                _stretches.RemoveWhere(st => !Valid(Network.Graph, st));
            };
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (@event is InputEventKey { Pressed: true } key) HandleKey(key);
        else if (@event is InputEventMouseButton mb) HandleMouseButton(mb);
    }

    private void HandleKey(InputEventKey key)
    {
        bool handled = true;
        if (_menu is not null && key.Keycode == Key.Escape) CloseMenu();
        else if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            CloseMenu();
            CancelDrag();
            if (key.ShiftPressed) Network?.Redo(); else Network?.Undo();
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed()) { CancelDrag(); Network?.Redo(); }
        else if (key.Keycode == Key.A && key.IsCommandOrControlPressed()) Host?.SetAnarchy(!Host.Anarchy);
        else if (key.Keycode is Key.Delete or Key.Backspace && _drag is null && _menu is null && HasSelection) DeleteSelection();
        else if (key.Keycode == Key.Escape)
        {
            // A drag or box first, then the selection, then back to drawing.
            if (_drag is not null || _boxFrom is not null) CancelDrag();
            else if (HasSelection) ClearSelection();
            else Host?.SetTool(SplineTool.Draw);
        }
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (_menu is { } menu)
        {
            if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            GetViewport().SetInputAsHandled();
            if (mb.ButtonIndex == MouseButton.Right && !mb.Pressed)
            {
                // Press, slide onto an action and let go chooses it; a plain right-click leaves it open for a click.
                if (menu.Held && menu.Hovered is { } held) Choose(menu, held);
                menu.Held = false;
            }
            else if (mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Left && menu.Hovered is { } i) Choose(menu, i);
                else CloseMenu();
            }
            return;
        }
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
        {
            if (_drag is not null || _boxFrom is not null) CancelDrag();
            else if (_hover is { Kind: not HandleKind.Group } h) OpenMenu(h);
            else if (_hoverEdge is { } he && Network is not null)
            {
                var st = _hoverStretch ?? new Stretch(he, 0);
                OpenMenu(new Handle(HandleKind.Stretch, st.EdgeId, st.Index, MidOf(Network.Graph, st)));
            }
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left) return;
        GetViewport().SetInputAsHandled();
        if (mb.Pressed && mb.DoubleClick && _hover is { Kind: HandleKind.Kerb or HandleKind.KerbKnob } kerb)
        {
            ResetKerb(kerb);
            _pressAt = null;
            return;
        }
        if (mb.Pressed)
        {
            _pressAt = mb.Position;
            _pressHandle = _hover;
            _pressEdge = _hoverEdge;
            _pressStretch = _hoverStretch;
            _pressShift = mb.ShiftPressed;
            return;
        }
        // Released: the end of a drag or a box, or a click.
        if (_drag is not null) Release();
        else if (_boxFrom is not null) FinishBox();
        else if (_pressAt is not null) Click(mb.ShiftPressed);
        _pressAt = null;
        _pressHandle = null;
        _pressEdge = null;
        _pressStretch = null;
    }

    public override void _Process(double delta)
    {
        if (Network is null || _overlay is null) return;
        _view.UpdateCursor();
        if (!Active)
        {
            if (_drag is not null || _boxFrom is not null) CancelDrag();
            CloseMenu(); // a drag or menu clears its own trial; the Draw tool's Grid shows one too
            _overlay.ShowEdit(null);
            _issueList?.Show(Array.Empty<Issue>(), Array.Empty<Issue>(), false);
            _hover = null;
            _hoverEdge = null;
            _hoverStretch = null;
            return;
        }

        var cursor = _view.Cursor is { } c ? _view.PlanOf(c) : (NumVector2?)null;
        // A press becomes a drag once the mouse has moved a few pixels: of the handle under it, of the selection
        // (a selected node, or any edge's body), or a box on empty ground.
        if (_drag is null && _boxFrom is null && _menu is null && _pressAt is { } down && _view.MouseScreen().DistanceTo(down) > DragPixels && cursor is { } at)
        {
            if (_pressHandle is { Kind: HandleKind.Node } n && _selectedNodes.Contains(n.Id)) StartGroupDrag(n.At, n.Id);
            else if (_pressHandle is { } held) StartDrag(held, at);
            else if (_pressStretch is { } ps)
            {
                if (!_stretches.Contains(ps))
                {
                    if (!_pressShift) ClearSelection();
                    _stretches.Add(ps);
                }
                StartGroupDrag(at, null);
            }
            else if (_pressEdge is { } pe)
            {
                if (!_selected.Contains(pe))
                {
                    if (!_pressShift) ClearSelection();
                    _selected.Add(pe);
                }
                StartGroupDrag(at, null);
            }
            else _boxFrom = down;
        }

        _snap = null;
        if (_menu is { } menu) UpdateMenu(menu);
        else if (_drag is { } d && cursor is { } p) TryDrag(d, p);
        else if (_boxFrom is { } from) UpdateBox(from, _view.MouseScreen());
        else if (_drag is null)
        {
            Network.ShowTrial(null);
            _hover = cursor is { } hp ? Pick(hp) : null;
            _hoverEdge = _hover is null && cursor is { } ep ? EdgeUnder(ep) : null;
            _hoverStretch = _hoverEdge is { } he && cursor is { } sp ? StretchOf(Network.Graph, he, sp) : null;
        }

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        _overlay.ShowEdit(Frame());
        _issueList?.Show(_menu?.Issues ?? _drag?.Issues ?? new List<Issue>(), Network.Issues, Host!.Anarchy);
    }

    // --- Picking ---

    /// <summary>The handle at a plan point: a selected junction's kerb knob or road handle first, then a selected edge's or
    /// stretch's radius knob, then its corner points, then any node; the nearest of each within the pick distance.</summary>
    private Handle? Pick(NumVector2 p)
    {
        var g = Network!.Graph;
        float within = _view.Cursor is { } c ? _view.PixelsToPlanUnits(PickPixels, c) : 2f;
        Handle? best = null;
        float bestDist = within;
        foreach (var k in KerbControlsOf(g)) Consider(k);
        if (best is not null) return best;
        var corners = SelectedCorners(g).ToList();
        foreach (var (id, i) in corners)
        {
            var a = g.Edge(id).Alignment;
            var corner = a.Corner(i);
            if (a.Pis[i].Hard || corner.Radius <= 0) continue;
            Consider(new Handle(HandleKind.Knob, id, i, corner.Mid));
        }
        if (best is not null) return best;
        foreach (var (id, i) in corners) Consider(new Handle(HandleKind.Pi, id, i, g.Edge(id).Alignment.Pis[i].Position));
        if (best is not null) return best;
        foreach (var n in g.Nodes) Consider(new Handle(HandleKind.Node, n.Id, 0, n.Position));
        return best;

        void Consider(Handle h)
        {
            float d = NumVector2.Distance(h.At, p);
            if (d >= bestDist) return;
            bestDist = d;
            best = h;
        }
    }

    /// <summary>The built edge whose corridor is under a point.</summary>
    private int? EdgeUnder(NumVector2 p)
    {
        int? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (var e in Network!.Graph.Edges)
        {
            float d = NumVector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p);
            if (d > e.Rules.Width / 2 + 1f || d >= bestDist) continue;
            bestDist = d;
            best = e.Id;
        }
        return best;
    }

    /// <summary>A click: on a node, a stretch of a road with corners, or a road with none selects it (Shift adds or
    /// removes it), on empty ground clears the selection. A corner point or knob clicked without moving it does nothing.</summary>
    private void Click(bool shift)
    {
        if (_pressHandle is { Kind: HandleKind.Node } n) Toggle(_selectedNodes, n.Id);
        else if (_pressHandle is not null) return;
        else if (_hoverStretch is { } st) Toggle(_stretches, st);
        else if (_hoverEdge is { } id) Toggle(_selected, id);
        else if (!shift) ClearSelection();

        void Toggle<T>(HashSet<T> set, T id)
        {
            if (shift) { if (!set.Remove(id)) set.Add(id); return; }
            ClearSelection();
            set.Add(id);
        }
    }

    private bool HasSelection => _selected.Count > 0 || _selectedNodes.Count > 0 || _stretches.Count > 0;

    private void ClearSelection()
    {
        _selected.Clear();
        _selectedNodes.Clear();
        _stretches.Clear();
    }

    // --- Box select ---

    /// <summary>What a box from <paramref name="from"/> to <paramref name="to"/> (screen) takes: the nodes inside it,
    /// the edges wholly inside it, and on a road with corners that isn't, the stretches wholly inside it.</summary>
    private void UpdateBox(Vector2 from, Vector2 to)
    {
        var box = new Rect2(from, Vector2.Zero).Expand(to);
        var g = Network!.Graph;
        _boxNodes = g.Nodes.Where(n => Inside(n.Position)).Select(n => n.Id).ToList();
        _boxEdges.Clear();
        _boxStretches.Clear();
        foreach (var e in g.Edges)
        {
            var a = e.Alignment;
            if (AllInside(a, 0, a.Length)) { _boxEdges.Add(e.Id); continue; }
            for (int k = 0; a.StretchCount > 1 && k < a.StretchCount; k++)
                if (AllInside(a, a.RoadStation(k), a.RoadStation(k + 1))) _boxStretches.Add(new Stretch(e.Id, k));
        }

        bool AllInside(Alignment a, float s0, float s1)
        {
            int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 4f));
            for (int i = 0; i <= n; i++)
                if (!Inside(a.Curve.Sample(s0 + (s1 - s0) * i / n).Position)) return false;
            return true;
        }

        bool Inside(NumVector2 p) => _view.ProjectPlan(p) is { } s && box.HasPoint(s);
    }

    /// <summary>Lets go of the box: it selects what it holds (Shift adds to the selection).</summary>
    private void FinishBox()
    {
        if (!_pressShift) ClearSelection();
        _selected.UnionWith(_boxEdges);
        _selectedNodes.UnionWith(_boxNodes);
        _stretches.UnionWith(_boxStretches);
        _boxFrom = null;
        _boxEdges.Clear();
        _boxNodes.Clear();
        _boxStretches.Clear();
    }

    // --- Dragging ---

    private void StartDrag(Handle h, NumVector2 cursor)
    {
        if (IsKerb(h.Kind)) { StartKerbDrag(h, cursor); return; }
        var g = Network!.Graph;
        var edges = h.Kind == HandleKind.Node ? g.Node(h.Id).Edges.Distinct().ToList() : new List<int> { h.Id };
        if (edges.Count == 0) return;
        float radius = h.Kind == HandleKind.Knob ? g.Edge(h.Id).Alignment.EffectiveRadius(h.Index) : 0;
        _drag = new Drag
        {
            Handle = h, Grab = h.At - cursor, Edges = edges, Rules = g.Edge(edges[0]).Rules,
            Pi = h.Kind == HandleKind.Node ? h.At : g.Edge(h.Id).Alignment.Pis[h.Index].Position,
            Target = h.At, Radius = radius,
        };
    }

    /// <summary>Picks up the selection to move it, held at <paramref name="at"/> (a selected node's position, which then
    /// snaps like a node drag, or a point on an edge's body).</summary>
    private void StartGroupDrag(NumVector2 at, int? node)
    {
        var g = Network!.Graph;
        var nodes = _selectedNodes.Where(g.HasNode).ToList();
        var edges = _selected.Where(g.HasEdge).ToList();
        var stretches = _stretches.Where(st => Valid(g, st)).ToList();
        var moved = nodes.Concat(edges.SelectMany(id => new[] { g.Edge(id).Start, g.Edge(id).End }))
            .Concat(stretches.SelectMany(st => EndNodes(g, st))).Distinct();
        var changed = moved.SelectMany(n => g.Node(n).Edges).Concat(stretches.Select(st => st.EdgeId)).Distinct().ToList();
        if (changed.Count == 0) return;
        var cursor = _view.Cursor is { } c ? _view.PlanOf(c) : at;
        _drag = new Drag
        {
            Handle = new Handle(HandleKind.Group, node ?? 0, node is null ? 0 : 1, at), Grab = at - cursor, Edges = changed,
            Rules = g.Edge(changed[0]).Rules, Pi = at, Target = at, GroupNodes = nodes, GroupEdges = edges,
            GroupStretches = stretches,
        };
    }

    /// <summary>Moves the held handle to the cursor (snapped, or straightened with Alt) and tries the edit.</summary>
    private void TryDrag(Drag d, NumVector2 cursor)
    {
        var built = Network!.Graph;
        var mods = _view.Modifiers();
        var raw = cursor + d.Grab;
        var h = d.Handle;
        if (IsKerb(h.Kind)) d.KerbValue = KerbValueAt(built, d, raw);
        else if (h.Kind == HandleKind.Knob)
        {
            var a = built.Edge(h.Id).Alignment;
            float min = Host!.Anarchy ? 1f : d.Rules.MinRadius;
            if (SplineGraph.KnobRadius(a, h.Index, raw) is { } r) d.Radius = Math.Clamp(r, MathF.Min(min, a.MaxRadius(h.Index)), a.MaxRadius(h.Index));
        }
        else if (h.Kind == HandleKind.Pi && mods.HasFlag(DrawModifiers.Alt))
        {
            var pis = built.Edge(h.Id).Alignment.Pis;
            d.Target = SplineGraph.OntoLine(pis[h.Index - 1].Position, pis[h.Index + 1].Position, raw);
        }
        else if (h.Kind == HandleKind.Group && h.Index == 0) d.Target = raw; // held by an edge's body: no snapping
        else
        {
            _snap = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = raw,
                Candidates = DragCandidates(built, d),
                SessionPis = DragLeg(built, d),
                Rules = d.Rules,
                EnabledProviders = (Host?.EnabledSnaps ?? SnapProviders.All) & d.Rules.SnapProviders,
                CatchDistance = _view.Cursor is { } c ? _view.PixelsToPlanUnits(CatchPixels, c) : 1f,
                Disabled = mods.HasFlag(DrawModifiers.Space),
            });
            d.Target = _snap.Position;
            if (_snap.Kind == SnapKind.None) _snap = null;
        }

        var g = built.Clone();
        var result = Perform(g, d);
        d.Graph = g;
        d.Result = result;
        d.Issues = NewIssues(g, result);
        Network.ShowTrial(g);
    }

    /// <summary>
    /// Snap sources for a drag: every edge it doesn't change and, of each edge whose end a held node moves, the part
    /// that stays put (up to where the corner next to that end starts), so a loose end can be snapped back onto its own
    /// road (a P closed onto its stem).
    /// </summary>
    private static List<SnapCandidate> DragCandidates(SplineGraph g, Drag d)
    {
        var list = SplineToolView.Candidates(g, d.Edges.ToHashSet());
        if (d.Handle.Kind != HandleKind.Node) return list;
        foreach (int id in d.Edges)
        {
            var e = g.Edge(id);
            var a = e.Alignment;
            if (e.Start == e.End || a.Pis.Count < 3) continue;
            bool atStart = e.Start == d.Handle.Id;
            var (s0, s1) = a.CornerStations(atStart ? 1 : a.Pis.Count - 2);
            if (atStart ? a.Length - s1 < 1f : s0 < 1f) continue;
            var still = atStart ? AlignmentOps.Between(a, s1, a.Length) : AlignmentOps.Between(a, 0, s0);
            int far = atStart ? e.End : e.Start;
            bool open = g.Node(far).Edges.Count == 1;
            list.Add(new SnapCandidate(still, e.Rules.Width, OpenStart: !atStart && open, OpenEnd: atStart && open));
        }
        return list;
    }

    /// <summary>A dead end held by its node is guided like the end of a draw: its road's points up to the one before it,
    /// in order, so the leg gets the draw's square foot, angle locks against the previous leg and lengths.</summary>
    private static IReadOnlyList<Pi> DragLeg(SplineGraph g, Drag d)
    {
        if (d.Handle.Kind != HandleKind.Node || g.Node(d.Handle.Id).Edges is not { Count: 1 } ids) return Array.Empty<Pi>();
        var e = g.Edge(ids[0]);
        if (e.Start == e.End) return Array.Empty<Pi>();
        var pis = e.End == d.Handle.Id ? e.Alignment.Pis : AlignmentOps.Reversed(e.Alignment).Pis;
        return pis.Take(pis.Count - 1).ToList();
    }

    /// <summary>The held edit on a graph: move, then reconnect.</summary>
    private static EditResult Perform(SplineGraph g, Drag d)
    {
        var h = d.Handle;
        switch (h.Kind)
        {
            case HandleKind.Node:
                return g.Reconnect(g.MoveNode(h.Id, d.Target), h.Id);
            case HandleKind.Pi:
                g.MovePi(h.Id, h.Index, d.Target);
                return g.Reconnect(new[] { h.Id });
            case HandleKind.Group:
                return g.Reconnect(g.MoveGroup(d.GroupNodes, d.GroupEdges, d.Delta, d.GroupStretches), h.Index == 1 ? h.Id : null);
            case HandleKind.Kerb or HandleKind.KerbKnob:
                ApplyKerb(g, d);
                return new EditResult(g.Node(d.KerbNode).Edges.Distinct().ToList(), new[] { d.KerbNode });
            default:
                g.SetRadius(h.Id, h.Index, d.Radius);
                return g.Reconnect(new[] { h.Id });
        }
    }

    /// <summary>Builds the held edit as one undo step, or springs back with a red flash when it's refused. The
    /// selection follows the edited edges to their new ids, and new junctions flash their tags.</summary>
    private void Release()
    {
        if (_drag is not { Result: not null } d || Network is null) { CancelDrag(); return; }
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        var bad = d.Issues.FirstOrDefault(i => i.Severity == Severity.Invalid);
        if (bad is not null && Host?.Anarchy != true)
        {
            _flashes.Add((new FlashTag(DragPoint(d), $"Can't move: {bad.Message}", Bad: true), until));
            CancelDrag();
            return;
        }
        var prior = Network.Graph.Clone(); // Apply changes the graph in place
        bool hadSelected = d.Edges.Any(_selected.Contains);
        // A group's selection is found again where it moved to (a reconnect renumbers what it touches).
        var movedEdges = d.Handle.Kind == HandleKind.Group ? _selected.Select(id => Moved(prior.Edge(id).Alignment, d.Delta)).ToList() : null;
        var movedNodes = d.Handle.Kind == HandleKind.Group ? _selectedNodes.Select(id => prior.Node(id).Position + d.Delta).ToList() : null;
        // A stretch is found again by its middle (moved with a group; a corner drag may shift it a little).
        var stretchMids = _stretches.Where(st => Valid(prior, st))
            .Select(st => MidOf(prior, st) + (d.Handle.Kind == HandleKind.Group ? d.Delta : NumVector2.Zero)).ToList();
        var result = Network.Apply(g => Perform(g, d));
        var graph = Network.Graph;
        _selected.RemoveWhere(id => !graph.HasEdge(id));
        _selectedNodes.RemoveWhere(id => !graph.HasNode(id));
        _stretches.Clear();
        if (movedEdges is not null && movedNodes is not null)
        {
            ClearSelection();
            _selected.UnionWith(result.Edges.Where(id => graph.HasEdge(id) && movedEdges.Any(a => Along(a, graph.Edge(id).Alignment))));
            foreach (var p in movedNodes)
                if (graph.NodeAt(p) is { } n) _selectedNodes.Add(n);
        }
        else if (hadSelected) _selected.UnionWith(result.Edges.Where(graph.HasEdge));
        Reselect(graph, stretchMids);
        FlashNewJunctions(prior, result, until);
        _drag = null;
    }

    /// <summary>Flashes the tag of each junction an edit made or changed.</summary>
    private void FlashNewJunctions(SplineGraph prior, EditResult result, double until)
    {
        var graph = Network!.Graph;
        foreach (int n in result.Nodes)
        {
            if (!graph.HasNode(n) || Junctions.Label(graph, n) is not { } label) continue;
            var at = graph.Node(n).Position;
            if (prior.NodeAt(at) is { } was && Junctions.Label(prior, was) == label) continue;
            _flashes.Add((new FlashTag(at, label), until));
        }
    }

    private static Alignment Moved(Alignment a, NumVector2 delta) =>
        new(a.Pis.Select(p => p with { Position = p.Position + delta }));

    /// <summary>Whether <paramref name="part"/> lies along <paramref name="whole"/> (a piece of it after a reconnect).</summary>
    private static bool Along(Alignment whole, Alignment part)
    {
        var mid = part.Curve.Sample(part.Length / 2).Position;
        return NumVector2.Distance(whole.Curve.ClosestPoint(mid).Position, mid) < SplineGraph.NodeTolerance;
    }

    private void CancelDrag()
    {
        _drag = null;
        _pressHandle = null;
        _pressEdge = null;
        _pressStretch = null;
        _pressAt = null;
        _boxFrom = null;
        _boxEdges.Clear();
        _boxNodes.Clear();
        _boxStretches.Clear();
        Network?.ShowTrial(null);
    }

    /// <summary>Deletes the selected stretches (the rest of their roads stays), the selected edges, and every edge at
    /// a selected node.</summary>
    private void DeleteSelection()
    {
        if (Network is null) return;
        var g0 = Network.Graph;
        // By alignment and position, since removing an edge may merge others and renumber them.
        var cuts = _stretches.Where(st => Valid(g0, st)).GroupBy(st => st.EdgeId)
            .Select(gr => (g0.Edge(gr.Key).Alignment, gr.Select(st => st.Index).ToList())).ToList();
        var edges = _selected.Where(g0.HasEdge).Select(id => g0.Edge(id).Alignment).ToList();
        var nodes = _selectedNodes.Where(g0.HasNode).Select(id => g0.Node(id).Position).ToList();
        Network.Apply(g =>
        {
            foreach (var (a, ks) in cuts)
                if (g.Edges.FirstOrDefault(e => e.Alignment == a) is { } e) g.RemoveStretches(e.Id, ks);
            foreach (var a in edges)
                if (g.Edges.FirstOrDefault(e => e.Alignment == a) is { } e) g.RemoveEdge(e.Id);
            foreach (var p in nodes)
                if (g.NodeAt(p) is { } n) g.RemoveNode(n);
            return 0;
        });
        ClearSelection();
    }

    // --- Radial menu ---

    /// <summary>Opens the radial menu on a corner point (a knob opens its corner's), a node, or a stretch of road.</summary>
    private void OpenMenu(Handle h)
    {
        CancelDrag();
        if (h.Kind == HandleKind.Knob) h = h with { Kind = HandleKind.Pi, At = Network!.Graph.Edge(h.Id).Alignment.Pis[h.Index].Position };
        if (IsKerb(h.Kind)) // a kerb control opens its junction's menu
        {
            int n = NodeOfKerb(Network!.Graph, h);
            h = new Handle(HandleKind.Node, n, 0, Network.Graph.Node(n).Position);
        }
        _menu = new Menu { Target = h, Enabled = MenuItems.Select(m => Applies(Network!.Graph, h, m.Action)).ToArray() };
    }

    private void CloseMenu()
    {
        if (_menu is null) return;
        _menu = null;
        Network?.ShowTrial(null);
    }

    /// <summary>Whether an action does anything at the target: a straight corner has nothing to smooth, harden or
    /// straighten; a node only smooths or straightens a joint of two edges, and never goes hard (it already is); a
    /// junction's Smooth is Reset kerbs, when a kerb handle is set. A stretch takes an action when one of its corners does.</summary>
    private static bool Applies(SplineGraph g, Handle h, MenuAction action)
    {
        if (action == MenuAction.Delete) return true;
        if (h.Kind == HandleKind.Node && action == MenuAction.Smooth && IsKerbJunction(g, h.Id)) return KerbsSet(g, h.Id);
        if (h.Kind == HandleKind.Node) return action != MenuAction.Hard && g.JointTurn(h.Id) > 0.1f;
        if (h.Kind == HandleKind.Stretch) return CornersOf(g.Edge(h.Id).Alignment, h.Index).Any(i => AppliesAt(g, h.Id, i, action));
        return AppliesAt(g, h.Id, h.Index, action);
    }

    private static bool AppliesAt(SplineGraph g, int edgeId, int i, MenuAction action)
    {
        var e = g.Edge(edgeId);
        var a = e.Alignment;
        var pi = a.Pis[i];
        if (MathF.Abs(a.Corner(i).TurnDegrees) < 0.1f) return false;
        return action switch
        {
            MenuAction.Smooth => pi.Hard || a.EffectiveRadius(i) < a.MaxRadius(i) * 0.999f,
            MenuAction.Hard => e.Rules.AllowHardCorners && !pi.Hard,
            _ => true,
        };
    }

    /// <summary>An action on a graph: change, then reconnect like a draw. On a stretch, Delete takes it out of its road
    /// and the others act on each of its corners they apply to.</summary>
    private static EditResult Perform(SplineGraph g, Handle h, MenuAction action)
    {
        if (h.Kind == HandleKind.Node)
        {
            switch (action)
            {
                case MenuAction.Smooth when IsKerbJunction(g, h.Id):
                    g.ResetKerbs(h.Id);
                    return new EditResult(g.Node(h.Id).Edges.Distinct().ToList(), new[] { h.Id });
                case MenuAction.Smooth: return g.SmoothNode(h.Id) ?? new EditResult(Array.Empty<int>(), Array.Empty<int>());
                case MenuAction.Straighten:
                    if (g.StraightenedNode(h.Id) is { } to) return g.Reconnect(g.MoveNode(h.Id, to), h.Id);
                    break;
                case MenuAction.Delete:
                    g.RemoveNode(h.Id);
                    break;
            }
            return new EditResult(Array.Empty<int>(), Array.Empty<int>());
        }
        if (h.Kind == HandleKind.Stretch)
        {
            if (action == MenuAction.Delete) return new EditResult(g.RemoveStretches(h.Id, new[] { h.Index }), Array.Empty<int>());
            foreach (int i in CornersOf(g.Edge(h.Id).Alignment, h.Index).Where(i => AppliesAt(g, h.Id, i, action)).ToList())
                ApplyAt(g, h.Id, i, action);
            return g.Reconnect(new[] { h.Id });
        }
        ApplyAt(g, h.Id, h.Index, action);
        return g.Reconnect(new[] { h.Id });
    }

    /// <summary>An action on interior corner <paramref name="i"/> of an edge (its id and PI count stay, but Delete).</summary>
    private static void ApplyAt(SplineGraph g, int edgeId, int i, MenuAction action)
    {
        var a = g.Edge(edgeId).Alignment;
        switch (action)
        {
            case MenuAction.Smooth: g.SetRadius(edgeId, i, a.MaxRadius(i)); break;
            case MenuAction.Hard: g.SetHard(edgeId, i); break;
            case MenuAction.Straighten: g.MovePi(edgeId, i, SplineGraph.OntoLine(a.Pis[i - 1].Position, a.Pis[i + 1].Position, a.Pis[i].Position)); break;
            default: g.RemovePi(edgeId, i); break;
        }
    }

    /// <summary>The action under the mouse (by its direction from the menu's centre), tried live as a drag is.</summary>
    private void UpdateMenu(Menu menu)
    {
        var built = Network!.Graph;
        menu.Hovered = _forcedMenuItem ?? HoveredItem(menu);
        if (menu.Hovered == menu.TriedFor) return;
        menu.TriedFor = menu.Hovered;
        menu.Graph = null;
        menu.Result = null;
        menu.Issues = new List<Issue>();
        if (menu.Hovered is not { } i || !menu.Enabled[i]) { Network.ShowTrial(null); return; }
        var g = built.Clone();
        var result = Perform(g, menu.Target, MenuItems[i].Action);
        menu.Graph = g;
        menu.Result = result;
        menu.Issues = NewIssues(g, result);
        Network.ShowTrial(g);
    }

    private int? HoveredItem(Menu menu)
    {
        if (_view.ProjectPlan(menu.Target.At) is not { } centre) return null;
        var off = _view.MouseScreen() - centre;
        float dist = off.Length();
        if (dist < MenuInnerPixels || dist > MenuOuterPixels + 40f) return null;
        float deg = Mathf.RadToDeg(off.Angle());
        int best = 0;
        for (int i = 1; i < MenuItems.Length; i++)
            if (AngleGap(deg, MenuItems[i].Angle) < AngleGap(deg, MenuItems[best].Angle)) best = i;
        return best;

        static float AngleGap(float a, float b) => MathF.Abs(Mathf.Wrap(a - b, -180f, 180f));
    }

    /// <summary>Builds the chosen action as one undo step, or flashes why it's refused (an Invalid result without Anarchy).</summary>
    private void Choose(Menu menu, int i)
    {
        if (!menu.Enabled[i] || Network is null) return;
        if (menu.TriedFor != i) { _forcedMenuItem = i; UpdateMenu(menu); _forcedMenuItem = null; }
        var (action, _, verb, _) = MenuItems[i];
        var target = menu.Target;
        if (action == MenuAction.Smooth && target.Kind == HandleKind.Node && IsKerbJunction(Network.Graph, target.Id)) verb = "reset kerbs";
        double until = Time.GetTicksMsec() / 1000.0 + FlashSeconds;
        CloseMenu();
        if (menu.Issues.FirstOrDefault(x => x.Severity == Severity.Invalid) is { } bad && Host?.Anarchy != true)
        {
            _flashes.Add((new FlashTag(target.At, $"Can't {verb}: {bad.Message}", Bad: true), until));
            return;
        }
        var prior = Network.Graph.Clone();
        bool hadSelected = target.Kind == HandleKind.Pi && _selected.Contains(target.Id);
        var stretchMids = _stretches.Where(st => Valid(prior, st))
            .Where(st => !(action == MenuAction.Delete && target.Kind == HandleKind.Stretch && st == new Stretch(target.Id, target.Index)))
            .Select(st => MidOf(prior, st)).ToList();
        var result = Network.Apply(g => Perform(g, target, action));
        _selected.RemoveWhere(id => !Network.Graph.HasEdge(id));
        _selectedNodes.RemoveWhere(id => !Network.Graph.HasNode(id));
        _stretches.Clear();
        Reselect(Network.Graph, stretchMids);
        if (hadSelected) _selected.UnionWith(result.Edges.Where(Network.Graph.HasEdge));
        FlashNewJunctions(prior, result, until);
    }

    /// <summary>The issues an edit brings: ones the network already had (an edge built red with Anarchy) neither
    /// show again nor block it.</summary>
    private List<Issue> NewIssues(SplineGraph g, EditResult result)
    {
        Network!.Conform(g);
        var touched = result.Edges.Concat(result.Nodes.SelectMany(n => g.Node(n).Edges)).Distinct();
        return Validation.Check(g, touched, result.Nodes)
            .Where(i => !Network!.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
    }

    /// <summary>Where the held handle is now: the moved point, or the knob on the tried arc.</summary>
    private static NumVector2 DragPoint(Drag d)
    {
        if (IsKerb(d.Handle.Kind) && d.Graph is { } g) return KerbPoint(g, d);
        if (d.Handle.Kind != HandleKind.Knob) return d.Target;
        return KnobOf(d) is { } k ? k.Alignment.Corner(k.Index).Mid : d.Handle.At;
    }

    /// <summary>The tried edge and corner the knob drag is changing (the PI that sits where the held one was).</summary>
    private static (Alignment Alignment, int Index)? KnobOf(Drag d)
    {
        if (d.Graph is not { } g || d.Result is not { } r) return null;
        foreach (int id in r.Edges)
        {
            if (!g.HasEdge(id)) continue;
            var a = g.Edge(id).Alignment;
            for (int i = 1; i < a.Pis.Count - 1; i++)
                if (NumVector2.Distance(a.Pis[i].Position, d.Pi) < 1e-3f) return (a, i);
        }
        return null;
    }

    // --- Feedback ---

    private EditFrame Frame()
    {
        var built = Network!.Graph;
        var d = _drag;
        var g = d?.Graph ?? built;
        var selected = new List<EditEdge>();
        var ghosts = new List<EditEdge>();
        FlashTag? dragTag = null;
        bool warn = false;
        NumVector2? hot = _hover is { } hv && !IsKerb(hv.Kind) ? hv.At : null; // a kerb control draws its own
        bool hotKnob = _hover?.Kind == HandleKind.Knob;

        if (d is { Result: { } r, Graph: { } tg })
        {
            if (!IsKerb(d.Handle.Kind)) ghosts.AddRange(d.Edges.Select(id => Edge(built, id)));
            if (d.Handle.Kind == HandleKind.Group)
            {
                // The selection where it moved to (the edges that only stretch aren't in it).
                var moved = _selected.Where(built.HasEdge).Select(id => Moved(built.Edge(id).Alignment, d.Delta)).ToList();
                selected.AddRange(r.Edges.Where(id => tg.HasEdge(id) && moved.Any(a => Along(a, tg.Edge(id).Alignment))).Select(id => Edge(tg, id)));
            }
            else
            {
                // The edited edges as tried, and the rest of the selection as built.
                if (d.Edges.Any(_selected.Contains)) selected.AddRange(r.Edges.Where(tg.HasEdge).Select(id => Edge(tg, id)));
                selected.AddRange(_selected.Where(id => !d.Edges.Contains(id) && tg.HasEdge(id)).Select(id => Edge(tg, id)));
            }
            hot = IsKerb(d.Handle.Kind) ? null : DragPoint(d);
            hotKnob = d.Handle.Kind == HandleKind.Knob;
            if (IsKerb(d.Handle.Kind)) (dragTag, warn) = KerbTag(d, tg);
            else if (d.Handle.Kind == HandleKind.Knob && KnobOf(d) is { } k)
            {
                var corner = k.Alignment.Corner(k.Index);
                float was = built.Edge(d.Handle.Id).Alignment.EffectiveRadius(d.Handle.Index);
                bool below = corner.Radius < d.Rules.MinRadius - 1e-2f;
                // At the most that fits: the knob won't go further out.
                warn = corner.Clamped || d.Radius >= k.Alignment.MaxRadius(k.Index) * 0.999f;
                dragTag = new FlashTag(corner.Mid, $"R {was:0} → {corner.Radius:0} m", Bad: below);
            }
            else
            {
                float before = d.Edges.Sum(id => built.Edge(id).Alignment.Length);
                float after = r.Edges.Where(tg.HasEdge).Sum(id => tg.Edge(id).Alignment.Length);
                float change = after - before;
                dragTag = new FlashTag(d.Target, $"{(change >= 0 ? "+" : "−")}{MathF.Abs(change):0} m");
            }
        }
        else if (_menu is { Graph: { } mg, Result: { } mr } menu)
        {
            // The hovered action as tried: the old shape faint, the selection as it would be.
            g = mg;
            var target = menu.Target;
            ghosts.AddRange(target.Kind == HandleKind.Node
                ? built.Node(target.Id).Edges.Distinct().Select(id => Edge(built, id))
                : new[] { Edge(built, target.Id) });
            if (target.Kind == HandleKind.Pi && _selected.Contains(target.Id)) selected.AddRange(mr.Edges.Where(mg.HasEdge).Select(id => Edge(mg, id)));
            selected.AddRange(_selected.Where(id => id != target.Id && mg.HasEdge(id)).Select(id => Edge(mg, id)));
        }
        else selected.AddRange(_selected.Where(built.HasEdge).Select(id => Edge(built, id)));

        // Selected stretches: outlined, with their corners' handles (as tried while a group moves them).
        var stretches = new List<EditEdge>();
        var corners = new List<(Alignment, int)>();
        if (d is { Handle.Kind: HandleKind.Group, Graph: { } sg })
            foreach (var st in _stretches.Where(st => Valid(built, st)))
            {
                var p = MidOf(built, st) + d.Delta;
                var near = sg.Edges.OrderBy(e => NumVector2.Distance(e.Alignment.Curve.ClosestPoint(p).Position, p)).FirstOrDefault();
                if (near is not null && StretchOf(sg, near.Id, p) is { } moved) stretches.Add(StretchEdge(sg, moved));
            }
        else
        {
            stretches.AddRange(_stretches.Where(st => Valid(built, st)).Select(st => StretchEdge(built, st)));
            if (d is null && _menu is null)
                foreach (var st in _stretches.Where(st => Valid(built, st)))
                    corners.AddRange(CornersOf(built.Edge(st.EdgeId).Alignment, st.Index).Select(i => (built.Edge(st.EdgeId).Alignment, i)));
        }
        EditEdge? hoverEdge = null;
        if (d is null && _menu is null && _boxFrom is null)
        {
            if (_hoverStretch is { } hs && Valid(built, hs)) hoverEdge = _stretches.Contains(hs) ? null : StretchEdge(built, hs);
            else if (_hoverEdge is { } he && !_selected.Contains(he)) hoverEdge = Edge(built, he);
        }

        var shift = d is { Handle.Kind: HandleKind.Group } gd ? gd.Delta : NumVector2.Zero;
        RadialMenu? radial = null;
        if (_menu is { } m && _view.ProjectPlan(m.Target.At) is { } centre)
        {
            hot = m.Target.At;
            hotKnob = false;
            bool junction = m.Target.Kind == HandleKind.Node && built.HasNode(m.Target.Id) && IsKerbJunction(built, m.Target.Id);
            radial = new RadialMenu(centre, MenuItems.Select((it, i) => new RadialItem(junction && it.Action == MenuAction.Smooth ? "Reset kerbs" : it.Label,
                it.Angle, m.Enabled[i], it.Action == MenuAction.Delete)).ToList(), m.Hovered);
        }

        return new EditFrame
        {
            Selected = selected,
            SelectedNodes = _selectedNodes.Where(built.HasNode).Select(id => built.Node(id).Position + shift).ToList(),
            HoverEdge = hoverEdge,
            Stretches = stretches,
            Corners = corners,
            BoxEdges = _boxEdges.Where(built.HasEdge).Select(id => Edge(built, id))
                .Concat(_boxStretches.Where(st => Valid(built, st)).Select(st => StretchEdge(built, st))).ToList(),
            BoxNodes = _boxNodes.Where(built.HasNode).Select(id => built.Node(id).Position).ToList(),
            Box = _boxFrom is { } bf ? new Rect2(bf, Vector2.Zero).Expand(_view.MouseScreen()) : null,
            Menu = radial,
            Nodes = g.Nodes.Select(n => n.Position).ToList(),
            Points = SplineToolView.RoadPoints(g).ToList(),
            HotPoint = hot,
            HotIsKnob = hotKnob,
            Ghosts = ghosts,
            Move = d is not null && d.Handle.Kind is not (HandleKind.Knob or HandleKind.Kerb or HandleKind.KerbKnob) ? (d.Handle.At, d.Target) : null,
            KerbHandles = KerbMarks(built, d),
            KerbGhosts = KerbGhosts(built, d),
            Snap = _snap,
            DragTag = dragTag,
            DragTagWarn = warn,
            Issues = _menu?.Issues ?? d?.Issues ?? (IReadOnlyList<Issue>)Array.Empty<Issue>(),
            Worst = _menu is { } wm ? Validation.Worst(wm.Issues) : d?.Worst,
            Flashes = _flashes.Select(f => f.Tag).ToList(),
            Mouse = _view.MouseScreen(),
            Hints = Hints(),
        };

        static EditEdge Edge(SplineGraph graph, int id) => new(graph.Edge(id).Alignment, graph.Edge(id).Rules.Width);
    }

    /// <summary>The mouse hints: what each input does right now.</summary>
    private List<(string, string)> Hints()
    {
        var hints = new List<(string, string)>();
        if (_menu is { } menu)
        {
            hints.Add((menu.Held ? "Release" : "LMB", "Choose"));
            hints.Add(("Esc", "Close"));
            return hints;
        }
        if (_boxFrom is not null)
        {
            hints.Add(("Release", "Select"));
            hints.Add(("RMB", "Cancel"));
            return hints;
        }
        if (_drag is { } d)
        {
            hints.Add(("Release", "Build"));
            hints.Add(("RMB", "Cancel"));
            if (d.Handle.Kind == HandleKind.Pi) hints.Add(("Alt", "Straighten"));
            if (d.Handle.Kind is HandleKind.Node or HandleKind.Pi || d.Handle is { Kind: HandleKind.Group, Index: 1 }) hints.Add(("Space", "No snapping"));
            return hints;
        }
        switch (_hover?.Kind)
        {
            case HandleKind.Knob: hints.Add(("Drag", "Radius")); hints.Add(("RMB", "Menu")); break;
            case HandleKind.KerbKnob: hints.Add(("Drag", "Kerb radius")); hints.Add(("Double-click", "Reset")); hints.Add(("RMB", "Junction menu")); break;
            case HandleKind.Kerb: hints.Add(("Drag", "This road's kerbs")); hints.Add(("Double-click", "Reset")); hints.Add(("RMB", "Junction menu")); break;
            case HandleKind.Pi: hints.Add(("Drag", "Move corner")); hints.Add(("Alt+drag", "Straighten")); hints.Add(("RMB", "Menu")); break;
            case HandleKind.Node:
                hints.Add(("Drag", _selectedNodes.Contains(_hover.Value.Id) ? "Move selection" : "Move node"));
                hints.Add(("LMB", "Select"));
                hints.Add(("RMB", "Menu"));
                break;
            default:
                if (_hoverEdge is { } he)
                {
                    bool chosen = _hoverStretch is { } hs ? _stretches.Contains(hs) : _selected.Contains(he);
                    hints.Add(("LMB", "Select"));
                    hints.Add(("Drag", chosen ? "Move selection" : "Move"));
                    hints.Add(("Shift+click", "Add / remove"));
                    hints.Add(("RMB", "Menu"));
                }
                else hints.Add(("Drag", "Box select"));
                break;
        }
        if (HasSelection) { hints.Add(("Del", "Delete")); hints.Add(("Esc", "Deselect")); }
        return hints;
    }

    // --- Direct-API test hooks for --storyboard (no simulated InputEvents) ---

    /// <summary>Selects the edge under a plan point (Shift+click with <paramref name="add"/>).</summary>
    public void SelectForTest(NumVector2 at, bool add = false)
    {
        if (Network is null || EdgeUnder(at) is not { } id) return;
        if (!add) _selected.Clear();
        _selected.Add(id);
    }

    /// <summary>Picks up the handle at <paramref name="from"/> and holds it at <paramref name="to"/> (the drag stays
    /// open, so a screenshot shows it in progress). With <paramref name="alt"/>, Alt is held.</summary>
    public bool DragForTest(NumVector2 from, NumVector2 to, bool alt = false)
    {
        if (Network is null) return false;
        ForcedPlanCursor = from;
        _view.UpdateCursor();
        if (Pick(from) is not { } h) return false;
        StartDrag(h, h.At);
        ForcedPlanCursor = to;
        if (alt) ForcedModifiers |= DrawModifiers.Alt;
        _view.UpdateCursor();
        if (_drag is { } d) TryDrag(d, to);
        return _drag is not null;
    }

    /// <summary>A plan point on screen (a scripted frame's mouse position).</summary>
    public Vector2 ScreenForTest(NumVector2 at) => _view.ProjectPlan(at) ?? Vector2.Zero;

    /// <summary>Lets go of the held handle (builds it, or springs back when refused).</summary>
    public void ReleaseForTest() => Release();

    /// <summary>Opens the radial menu on the corner point or node at <paramref name="at"/>, with action
    /// <paramref name="item"/> (0 Smooth, 1 Hard, 2 Straight, 3 Delete) hovered; <paramref name="choose"/> picks it.</summary>
    public bool MenuForTest(NumVector2 at, int? item, bool choose = false)
    {
        if (Network is null) return false;
        ForcedPlanCursor = at;
        _view.UpdateCursor();
        if (Pick(at) is not { } h) return false;
        OpenMenu(h);
        _forcedMenuItem = item;
        UpdateMenu(_menu!);
        if (choose && item is { } i) Choose(_menu!, i);
        _forcedMenuItem = choose ? null : _forcedMenuItem;
        return true;
    }

    /// <summary>Drags a box from <paramref name="from"/> to <paramref name="to"/> (plan points); with
    /// <paramref name="release"/> it selects, else it stays open for a screenshot.</summary>
    public void BoxForTest(NumVector2 from, NumVector2 to, bool release, bool add = false)
    {
        if (_view.ProjectPlan(from) is not { } a) return;
        ForcedPlanCursor = to;
        _view.UpdateCursor();
        _boxFrom = a;
        _pressShift = add;
        UpdateBox(a, _view.MouseScreen());
        if (release) FinishBox();
    }

    /// <summary>Picks up the selection at <paramref name="from"/> (a selected node, or an edge's body) and holds it
    /// at <paramref name="to"/>.</summary>
    public bool MoveSelectionForTest(NumVector2 from, NumVector2 to)
    {
        if (Network is null) return false;
        ForcedPlanCursor = from;
        _view.UpdateCursor();
        var h = Pick(from);
        if (h is { Kind: HandleKind.Node } n && _selectedNodes.Contains(n.Id)) StartGroupDrag(n.At, n.Id);
        else StartGroupDrag(from, null);
        ForcedPlanCursor = to;
        _view.UpdateCursor();
        if (_drag is { } d) TryDrag(d, to);
        return _drag is not null;
    }
}

