using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.TerrainSystem;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The built network in the scene: the <see cref="SplineGraph"/>, its undo history, the validation issues, the
/// junction footprints, and the flat-ribbon visuals of every edge and footprint. Every change goes through
/// <see cref="Apply"/> as one undo step (DESIGN.md → Undo: one stack, one command per tool action). The history keeps
/// whole graph snapshots, which is simple and cheap while alignments are shared between them (S11 can revisit).
/// The Draw and Edit tools share this node.
/// <para>After every change each node and edge gets its height (<see cref="Vertical.Conform"/>), and the ground round the
/// new and changed ones is shaped to them (<see cref="GroundShaping"/>) in one terrain edit; undoing the change undoes
/// that edit too. The splines never move with the ground: when something else changes it (a terrain tool, a script),
/// the ground round the splines there is shaped back once the stroke is over, as its own terrain undo step.</para>
/// </summary>
public partial class SplineNetwork : Node
{
    private const int MaxHistory = 200;

    [Export] public Terrain? Terrain { get; set; }

    /// <summary>A graph to go back to, and whether going there also undoes (or redoes) one terrain edit.</summary>
    private sealed class Step(SplineGraph graph)
    {
        public SplineGraph Graph { get; } = graph;
        public bool Shaped { get; set; }
    }

    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private bool _ownTerrainChange;
    private VertexRect _groundChanged = VertexRect.Empty;
    private readonly Dictionary<string, SplineProfile> _profiles = new();
    private RibbonRenderer? _renderer;
    private INetworkVisual? _visual;
    private Dictionary<int, EdgeSpan> _hidden = new();
    private bool _showingTrial;

    public SplineGraph Graph { get; private set; } = new();
    public IReadOnlyList<Issue> Issues { get; private set; } = Array.Empty<Issue>();
    public IReadOnlyDictionary<int, JunctionFootprint> Footprints { get; private set; } = new Dictionary<int, JunctionFootprint>();
    public IGround? Ground { get; private set; }

    /// <summary>After every change, undo and redo.</summary>
    public event Action? Changed;
    /// <summary>The ground round the splines was shaped back after something else changed it (one terrain undo step,
    /// pushed right after that change's). A terrain tool undoing its stroke undoes this step with it.</summary>
    public event Action? GroundShapedBack;

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineNetwork needs a Terrain"); return; }
        Ground = new TerrainGround(Terrain);
        _renderer = new RibbonRenderer(Terrain, Ground);
        Terrain.HeightsChanged += OnHeightsChanged;
        Terrain.PaintChanged += OnPaintChanged;
    }

    public override void _ExitTree()
    {
        if (Terrain is null) return;
        Terrain.HeightsChanged -= OnHeightsChanged;
        Terrain.PaintChanged -= OnPaintChanged;
    }

    public override void _Process(double delta)
    {
        if (_groundChanged.IsEmpty || Terrain is null || Terrain.History.InStroke) return;
        var rect = _groundChanged;
        _groundChanged = VertexRect.Empty;
        float cs = Terrain.Map?.CellSize ?? 1f;
        if (ShapeGround(grid => GroundShaping.ShapeArea(grid, Graph, Footprints,
                new System.Numerics.Vector2(rect.MinX * cs, rect.MinZ * cs), new System.Numerics.Vector2(rect.MaxX * cs, rect.MaxZ * cs))))
            GroundShapedBack?.Invoke();
    }

    private void OnHeightsChanged(VertexRect rect)
    {
        // Our own shaping (or its undo) is the ground the splines want; anything else gets shaped back round them.
        if (_ownTerrainChange) { _ownTerrainChange = false; return; }
        // That change is on top of the terrain's history now, so undoing a step here would undo it instead of the
        // step's own shaping: the splines' history ends at a ground change from outside.
        _undo.Clear();
        _redo.Clear();
        _groundChanged = _groundChanged.IsEmpty ? rect : _groundChanged.Union(rect);
    }

    /// <summary>A paint stroke is a step on the terrain's history too, so (as for heights) ours ends there: undoing a
    /// shaped step would undo the paint instead.</summary>
    private void OnPaintChanged(VertexRect rect)
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Remembers a profile so its edges get its colour (edges only carry the Core rules).</summary>
    public void RegisterProfile(SplineProfile profile) => _profiles[profile.Id] = profile;

    public Color ColorOf(string profileId) => _profiles.TryGetValue(profileId, out var p) ? p.Color : Colors.Gray;

    /// <summary>One undoable change to the graph.</summary>
    public T Apply<T>(Func<SplineGraph, T> change)
    {
        var step = new Step(Graph.Clone());
        _undo.Push(step);
        if (_undo.Count > MaxHistory) TrimHistory();
        _redo.Clear();
        var result = change(Graph);
        step.Shaped = Refresh(shape: true);
        return result;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var step = _undo.Pop();
        _redo.Push(new Step(Graph) { Shaped = step.Shaped });
        Graph = step.Graph;
        if (step.Shaped && Terrain is { History.CanUndo: true })
        {
            _ownTerrainChange = true;
            Terrain.Undo();
        }
        Refresh(shape: false);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var step = _redo.Pop();
        _undo.Push(new Step(Graph) { Shaped = step.Shaped });
        Graph = step.Graph;
        if (step.Shaped && Terrain is { History.CanRedo: true })
        {
            _ownTerrainChange = true;
            Terrain.Redo();
        }
        Refresh(shape: false);
        return true;
    }

    /// <summary>Leaves these edges out of the visuals.</summary>
    public void Hide(IEnumerable<int> edges) => Hide(edges.ToDictionary(id => id, _ => EdgeSpan.None));

    /// <summary>Draws these edges only over their spans (a draw in progress continuing them keeps that part as built
    /// and shows the rest in its preview). Redraws only when they change.</summary>
    public void Hide(IReadOnlyDictionary<int, EdgeSpan> edges)
    {
        if (edges.Count == _hidden.Count && edges.All(kv => _hidden.TryGetValue(kv.Key, out var s) && s == kv.Value)) return;
        _hidden = new Dictionary<int, EdgeSpan>(edges);
        // A trial being shown keeps its own picture; the built one is drawn with these hidden when it ends.
        if (!_showingTrial) Draw(Graph, Footprints, Issues, _hidden);
    }

    /// <summary>The edges left out of the visuals but for their spans (<see cref="Hide"/>).</summary>
    public IReadOnlyDictionary<int, EdgeSpan> Hidden => _hidden;

    /// <summary>Gives a changed copy of the graph (a tool's trial) its heights, so validating it sees grades and cut/fill.</summary>
    public void Conform(SplineGraph trial)
    {
        if (Ground is not null) Vertical.Conform(trial, Junctions.Footprints(trial), Ground);
    }

    /// <summary>Draws a changed copy of the graph in place of the built one (an Edit drag in progress, before it's
    /// built), with its footprints and issue halos; null goes back to the built graph. With <paramref name="hidden"/>
    /// the <see cref="Hidden"/> edges stay out of it too.</summary>
    public void ShowTrial(SplineGraph? trial, IReadOnlyList<Issue>? issues = null, bool hidden = false)
    {
        if (trial is null)
        {
            if (!_showingTrial) return;
            _showingTrial = false;
            Draw(Graph, Footprints, Issues, _hidden);
            return;
        }
        _showingTrial = true;
        var footprints = Junctions.Footprints(trial);
        if (Ground is not null) Vertical.Conform(trial, footprints, Ground);
        Draw(trial, footprints, issues ?? Validation.Check(trial), hidden ? _hidden : null);
    }

    /// <summary>Heights, footprints, issues and visuals for the current graph; with <paramref name="shape"/> the ground
    /// round what changed is shaped too. True if the ground was changed (one terrain undo step).</summary>
    private bool Refresh(bool shape)
    {
        _showingTrial = false;
        Footprints = Junctions.Footprints(Graph);
        bool shaped = false;
        if (Ground is not null)
        {
            var (edges, nodes) = Vertical.Conform(Graph, Footprints, Ground);
            if (shape) shaped = ShapeGround(grid => GroundShaping.Shape(grid, Graph, Footprints, edges, nodes));
        }
        Issues = Validation.Check(Graph);
        foreach (int id in _hidden.Keys.Where(id => !Graph.HasEdge(id)).ToList()) _hidden.Remove(id);
        Draw(Graph, Footprints, Issues, _hidden);
        Changed?.Invoke();
        return shaped;
    }

    /// <summary>One terrain edit running <paramref name="shape"/>; nothing is recorded if it wrote nothing. False when the
    /// terrain is busy (another edit or a tool stroke is open) or there's no map.</summary>
    private bool ShapeGround(Func<IHeightGrid, bool> shape)
    {
        if (Terrain is not { Map: not null } terrain || terrain.History.InStroke) return false;
        var edit = terrain.BeginEdit();
        if (!shape(new TerrainHeightGrid(edit, terrain)))
        {
            edit.Cancel();
            return false;
        }
        edit.Commit();
        _ownTerrainChange = true;
        return true;
    }

    /// <summary>The consumer's visuals for the built network, in place of the flat ribbons (null = ribbons). Setting it
    /// redraws.</summary>
    public INetworkVisual? Visual
    {
        get => _visual;
        set
        {
            _visual = value;
            _renderer?.SetNetwork(new SplineGraph(), Footprints, Array.Empty<Issue>(), ColorOf); // clears the ribbons
            Draw(Graph, Footprints, Issues, _hidden);
        }
    }

    private void Draw(SplineGraph graph, IReadOnlyDictionary<int, JunctionFootprint> footprints, IReadOnlyList<Issue> issues,
        IReadOnlyDictionary<int, EdgeSpan>? hidden)
    {
        if (_visual is not null) _visual.SetNetwork(graph, footprints, issues, hidden);
        else _renderer?.SetNetwork(graph, footprints, issues, ColorOf, hidden);
    }

    private void TrimHistory()
    {
        var keep = new List<Step>(_undo);
        keep.RemoveAt(keep.Count - 1); // the oldest
        _undo.Clear();
        for (int i = keep.Count - 1; i >= 0; i--) _undo.Push(keep[i]);
    }
}
