using System;
using System.Collections.Generic;
using CitySim.TerrainSystem;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The built network in the scene: the <see cref="SplineGraph"/>, its undo history, the validation issues, the
/// junction footprints, and the flat-ribbon visuals of every edge and footprint. Every change goes through
/// <see cref="Apply"/> as one undo step (DESIGN.md → Undo: one stack, one command per tool action). The history keeps
/// whole graph snapshots, which is simple and cheap while alignments are shared between them (S11 can revisit).
/// The Draw and Edit tools share this node.
/// </summary>
public partial class SplineNetwork : Node
{
    private const int MaxHistory = 200;

    [Export] public Terrain? Terrain { get; set; }

    private readonly Stack<SplineGraph> _undo = new();
    private readonly Stack<SplineGraph> _redo = new();
    private readonly Dictionary<string, SplineProfile> _profiles = new();
    private RibbonRenderer? _renderer;
    private INetworkVisual? _visual;
    private HashSet<int> _hidden = new();
    private bool _showingTrial;

    public SplineGraph Graph { get; private set; } = new();
    public IReadOnlyList<Issue> Issues { get; private set; } = Array.Empty<Issue>();
    public IReadOnlyDictionary<int, JunctionFootprint> Footprints { get; private set; } = new Dictionary<int, JunctionFootprint>();
    public IGround? Ground { get; private set; }

    /// <summary>After every change, undo and redo.</summary>
    public event Action? Changed;

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineNetwork needs a Terrain"); return; }
        Ground = new TerrainGround(Terrain);
        _renderer = new RibbonRenderer(Terrain, Ground);
    }

    /// <summary>Remembers a profile so its edges get its colour (edges only carry the Core rules).</summary>
    public void RegisterProfile(SplineProfile profile) => _profiles[profile.Id] = profile;

    public Color ColorOf(string profileId) => _profiles.TryGetValue(profileId, out var p) ? p.Color : Colors.Gray;

    /// <summary>One undoable change to the graph.</summary>
    public T Apply<T>(Func<SplineGraph, T> change)
    {
        _undo.Push(Graph.Clone());
        if (_undo.Count > MaxHistory) TrimHistory();
        _redo.Clear();
        var result = change(Graph);
        Refresh();
        return result;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Graph);
        Graph = _undo.Pop();
        Refresh();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Graph);
        Graph = _redo.Pop();
        Refresh();
        return true;
    }

    /// <summary>Leaves these edges out of the visuals (a draw in progress continuing them shows them in its
    /// preview instead). Redraws only when the set changes.</summary>
    public void Hide(IEnumerable<int> edges)
    {
        var set = new HashSet<int>(edges);
        if (set.SetEquals(_hidden)) return;
        _hidden = set;
        // A trial being shown keeps its own picture; the built one is drawn with these hidden when it ends.
        if (!_showingTrial) Draw(Graph, Footprints, Issues, _hidden);
    }

    /// <summary>The edges left out of the visuals (<see cref="Hide"/>).</summary>
    public IReadOnlyCollection<int> Hidden => _hidden;

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
        Draw(trial, Junctions.Footprints(trial), issues ?? Validation.Check(trial), hidden ? _hidden : null);
    }

    private void Refresh()
    {
        _showingTrial = false;
        Footprints = Junctions.Footprints(Graph);
        Issues = Validation.Check(Graph);
        _hidden.RemoveWhere(id => !Graph.HasEdge(id));
        Draw(Graph, Footprints, Issues, _hidden);
        Changed?.Invoke();
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
        IReadOnlySet<int>? hidden)
    {
        if (_visual is not null) _visual.SetNetwork(graph, footprints, issues, hidden);
        else _renderer?.SetNetwork(graph, footprints, issues, ColorOf, hidden);
    }

    private void TrimHistory()
    {
        var keep = new List<SplineGraph>(_undo);
        keep.RemoveAt(keep.Count - 1); // the oldest
        _undo.Clear();
        for (int i = keep.Count - 1; i >= 0; i--) _undo.Push(keep[i]);
    }
}
