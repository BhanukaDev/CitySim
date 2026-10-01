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
/// or removes one), then drag its corner points or the radius knob in the middle of each arc; any node can be dragged.
/// Connected edges follow and keep their radii, Alt-drag slides a corner point onto its neighbours' line
/// (straighten), and the knob takes the same radius limits as drawing. Every frame of a drag is tried on a copy of the
/// graph and drawn in place of the built one, the old shape left as a faint outline. On release it's joined to the
/// network as a draw would be (<see cref="SplineGraph.Reconnect"/>) and built as one undo step, unless it has an
/// Invalid issue and Anarchy is off: then it springs back with a red flash. Delete removes the selection.
/// </summary>
public partial class SplineEditTool : Node
{
    private const float CatchPixels = 8f;
    private const float PickPixels = 12f;
    private const float DragPixels = 4f;
    private const double FlashSeconds = 3.0;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    private enum HandleKind { Node, Pi, Knob }

    /// <summary>Something to drag: a node (<see cref="Id"/> is the node), or interior PI <see cref="Index"/> of edge
    /// <see cref="Id"/> by its point or its radius knob.</summary>
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
    }

    private readonly SplineToolView _view;
    private readonly HashSet<int> _selected = new();
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

    public SplineEditTool() => _view = new SplineToolView(this);

    /// <summary>Scripted-demo override: a plan-space position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get => _view.ForcedPlanCursor; set => _view.ForcedPlanCursor = value; }
    /// <summary>Scripted-demo override: modifier keys treated as held.</summary>
    public DrawModifiers ForcedModifiers { get => _view.ForcedModifiers; set => _view.ForcedModifiers = value; }
    public IReadOnlyCollection<int> Selected => _selected;

    private bool Active => Testbed is { Tool: SplineTool.Edit };

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
        if (Network is not null) Network.Changed += () => _selected.RemoveWhere(id => !Network.Graph.HasEdge(id));
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
        if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            CancelDrag();
            if (key.ShiftPressed) Network?.Redo(); else Network?.Undo();
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed()) { CancelDrag(); Network?.Redo(); }
        else if (key.Keycode == Key.A && key.IsCommandOrControlPressed()) Testbed?.SetAnarchy(!Testbed.Anarchy);
        else if (key.Keycode is Key.Delete or Key.Backspace && _drag is null && _selected.Count > 0) DeleteSelection();
        else if (key.Keycode == Key.Escape)
        {
            // A drag first, then the selection, then back to drawing.
            if (_drag is not null) CancelDrag();
            else if (_selected.Count > 0) _selected.Clear();
            else Testbed?.SetTool(SplineTool.Draw);
        }
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && _drag is not null)
        {
            CancelDrag();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left) return;
        GetViewport().SetInputAsHandled();
        if (mb.Pressed)
        {
            _pressAt = mb.Position;
            _pressHandle = _hover;
            return;
        }
        // Released: the end of a drag, or a click.
        if (_drag is not null) Release();
        else if (_pressAt is not null) Click(mb.ShiftPressed);
        _pressAt = null;
        _pressHandle = null;
    }

    public override void _Process(double delta)
    {
        if (Network is null || _overlay is null) return;
        _view.UpdateCursor();
        if (!Active)
        {
            if (_drag is not null) CancelDrag();
            Network.ShowTrial(null);
            _overlay.ShowEdit(null);
            _issueList?.Show(Array.Empty<Issue>(), Array.Empty<Issue>(), false);
            _hover = null;
            _hoverEdge = null;
            return;
        }

        var cursor = _view.Cursor is { } c ? _view.PlanOf(c) : (NumVector2?)null;
        // A press on a handle becomes a drag once the mouse has moved a few pixels.
        if (_drag is null && _pressAt is { } down && _pressHandle is { } held && _view.MouseScreen().DistanceTo(down) > DragPixels && cursor is { } at)
            StartDrag(held, at);

        _snap = null;
        if (_drag is { } d && cursor is { } p) TryDrag(d, p);
        else if (_drag is null)
        {
            Network.ShowTrial(null);
            _hover = cursor is { } hp ? Pick(hp) : null;
            _hoverEdge = _hover is null && cursor is { } ep ? EdgeUnder(ep) : null;
        }

        double now = Time.GetTicksMsec() / 1000.0;
        _flashes.RemoveAll(f => now > f.Until);
        _overlay.ShowEdit(Frame());
        _issueList?.Show(_drag?.Issues ?? new List<Issue>(), Network.Issues, Testbed!.Anarchy);
    }

    // --- Picking ---

    /// <summary>The handle at a plan point: a selected edge's radius knob first, then its corner points, then any
    /// node; the nearest of each within the pick distance.</summary>
    private Handle? Pick(NumVector2 p)
    {
        var g = Network!.Graph;
        float within = _view.Cursor is { } c ? _view.PixelsToPlanUnits(PickPixels, c) : 2f;
        Handle? best = null;
        float bestDist = within;
        foreach (int id in _selected)
        {
            var a = g.Edge(id).Alignment;
            for (int i = 1; i < a.Pis.Count - 1; i++)
            {
                var corner = a.Corner(i);
                if (a.Pis[i].Hard || corner.Radius <= 0) continue;
                Consider(new Handle(HandleKind.Knob, id, i, corner.Mid));
            }
        }
        if (best is not null) return best;
        foreach (int id in _selected)
        {
            var a = g.Edge(id).Alignment;
            for (int i = 1; i < a.Pis.Count - 1; i++) Consider(new Handle(HandleKind.Pi, id, i, a.Pis[i].Position));
        }
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

    /// <summary>A click: on an edge selects it (Shift adds or removes it), on empty ground clears the selection.</summary>
    private void Click(bool shift)
    {
        if (_pressHandle is not null) return; // a handle clicked without moving it
        if (_hoverEdge is { } id)
        {
            if (!shift) { _selected.Clear(); _selected.Add(id); }
            else if (!_selected.Remove(id)) _selected.Add(id);
        }
        else if (!shift) _selected.Clear();
    }

    // --- Dragging ---

    private void StartDrag(Handle h, NumVector2 cursor)
    {
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

    /// <summary>Moves the held handle to the cursor (snapped, or straightened with Alt) and tries the edit.</summary>
    private void TryDrag(Drag d, NumVector2 cursor)
    {
        var built = Network!.Graph;
        var mods = _view.Modifiers();
        var raw = cursor + d.Grab;
        var h = d.Handle;
        if (h.Kind == HandleKind.Knob)
        {
            var a = built.Edge(h.Id).Alignment;
            float min = Testbed!.Anarchy ? 1f : d.Rules.MinRadius;
            if (SplineGraph.KnobRadius(a, h.Index, raw) is { } r) d.Radius = Math.Clamp(r, MathF.Min(min, a.MaxRadius(h.Index)), a.MaxRadius(h.Index));
        }
        else if (h.Kind == HandleKind.Pi && mods.HasFlag(DrawModifiers.Alt))
        {
            var pis = built.Edge(h.Id).Alignment.Pis;
            d.Target = SplineGraph.OntoLine(pis[h.Index - 1].Position, pis[h.Index + 1].Position, raw);
        }
        else
        {
            _snap = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = raw,
                Candidates = SplineToolView.Candidates(built, d.Edges.ToHashSet()),
                Rules = d.Rules,
                EnabledProviders = (Testbed?.EnabledSnaps ?? SnapProviders.All) & d.Rules.SnapProviders,
                CatchDistance = _view.Cursor is { } c ? _view.PixelsToPlanUnits(CatchPixels, c) : 1f,
                Disabled = mods.HasFlag(DrawModifiers.Space),
            });
            d.Target = _snap.Position;
            if (_snap.Kind == SnapKind.None) _snap = null;
        }

        var g = built.Clone();
        var result = Perform(g, d);
        var touched = result.Edges.Concat(result.Nodes.SelectMany(n => g.Node(n).Edges)).Distinct();
        d.Graph = g;
        d.Result = result;
        // Issues the network already had (an edge built red with Anarchy) neither show again nor block the edit.
        d.Issues = Validation.Check(g, touched, result.Nodes)
            .Where(i => !Network.Issues.Any(old => old.Code == i.Code && old.Message == i.Message && NumVector2.Distance(old.Where, i.Where) < 1f))
            .ToList();
        Network.ShowTrial(g);
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
        if (bad is not null && Testbed?.Anarchy != true)
        {
            _flashes.Add((new FlashTag(DragPoint(d), $"Can't move: {bad.Message}", Bad: true), until));
            CancelDrag();
            return;
        }
        var prior = Network.Graph.Clone(); // Apply changes the graph in place
        bool hadSelected = d.Edges.Any(_selected.Contains);
        var result = Network.Apply(g => Perform(g, d));
        _selected.RemoveWhere(id => !Network.Graph.HasEdge(id));
        if (hadSelected) _selected.UnionWith(result.Edges.Where(Network.Graph.HasEdge));
        foreach (int n in result.Nodes)
        {
            if (!Network.Graph.HasNode(n) || Junctions.Label(Network.Graph, n) is not { } label) continue;
            var at = Network.Graph.Node(n).Position;
            if (prior.NodeAt(at) is { } was && Junctions.Label(prior, was) == label) continue;
            _flashes.Add((new FlashTag(at, label), until));
        }
        _drag = null;
    }

    private void CancelDrag()
    {
        _drag = null;
        _pressHandle = null;
        _pressAt = null;
        Network?.ShowTrial(null);
    }

    private void DeleteSelection()
    {
        if (Network is null) return;
        var ids = _selected.ToList();
        Network.Apply(g =>
        {
            foreach (int id in ids)
                if (g.HasEdge(id)) g.RemoveEdge(id);
            return ids.Count;
        });
        _selected.Clear();
    }

    /// <summary>Where the held handle is now: the moved point, or the knob on the tried arc.</summary>
    private static NumVector2 DragPoint(Drag d)
    {
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
        NumVector2? hot = _hover?.At;
        bool hotKnob = _hover?.Kind == HandleKind.Knob;

        if (d is { Result: { } r, Graph: { } tg })
        {
            ghosts.AddRange(d.Edges.Select(id => Edge(built, id)));
            // The edited edges as tried, and the rest of the selection as built.
            if (d.Edges.Any(_selected.Contains)) selected.AddRange(r.Edges.Where(tg.HasEdge).Select(id => Edge(tg, id)));
            selected.AddRange(_selected.Where(id => !d.Edges.Contains(id) && tg.HasEdge(id)).Select(id => Edge(tg, id)));
            hot = DragPoint(d);
            hotKnob = d.Handle.Kind == HandleKind.Knob;
            if (d.Handle.Kind == HandleKind.Knob && KnobOf(d) is { } k)
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
        else selected.AddRange(_selected.Where(built.HasEdge).Select(id => Edge(built, id)));

        return new EditFrame
        {
            Selected = selected,
            HoverEdge = d is null && _hoverEdge is { } he && !_selected.Contains(he) ? Edge(built, he) : null,
            Nodes = g.Nodes.Select(n => n.Position).ToList(),
            HotPoint = hot,
            HotIsKnob = hotKnob,
            Ghosts = ghosts,
            Move = d is not null && d.Handle.Kind != HandleKind.Knob ? (d.Handle.At, d.Target) : null,
            Snap = _snap,
            DragTag = dragTag,
            DragTagWarn = warn,
            Issues = d?.Issues ?? (IReadOnlyList<Issue>)Array.Empty<Issue>(),
            Worst = d?.Worst,
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
        if (_drag is { } d)
        {
            hints.Add(("Release", "Build"));
            hints.Add(("RMB", "Cancel"));
            if (d.Handle.Kind == HandleKind.Pi) hints.Add(("Alt", "Straighten"));
            if (d.Handle.Kind != HandleKind.Knob) hints.Add(("Space", "No snapping"));
            return hints;
        }
        switch (_hover?.Kind)
        {
            case HandleKind.Knob: hints.Add(("Drag", "Radius")); break;
            case HandleKind.Pi: hints.Add(("Drag", "Move corner")); hints.Add(("Alt+drag", "Straighten")); break;
            case HandleKind.Node: hints.Add(("Drag", "Move node")); break;
            default:
                if (_hoverEdge is not null) { hints.Add(("LMB", "Select")); hints.Add(("Shift+click", "Add / remove")); }
                break;
        }
        if (_selected.Count > 0) { hints.Add(("Del", "Delete")); hints.Add(("Esc", "Deselect")); }
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
        ForcedModifiers = alt ? DrawModifiers.Alt : DrawModifiers.None;
        _view.UpdateCursor();
        if (_drag is { } d) TryDrag(d, to);
        return _drag is not null;
    }

    /// <summary>Lets go of the held handle (builds it, or springs back when refused).</summary>
    public void ReleaseForTest() => Release();
}

