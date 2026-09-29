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
/// Tools (Draw now, Edit in S5) share this node.
/// </summary>
public partial class SplineNetwork : Node
{
    private const int MaxHistory = 200;

    [Export] public Terrain? Terrain { get; set; }

    private readonly Stack<SplineGraph> _undo = new();
    private readonly Stack<SplineGraph> _redo = new();
    private readonly Dictionary<string, SplineProfile> _profiles = new();
    private RibbonRenderer? _renderer;
    private HashSet<int> _hidden = new();

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
        _renderer?.SetNetwork(Graph, Footprints, Issues, ColorOf, _hidden);
    }

    private void Refresh()
    {
        Footprints = Junctions.Footprints(Graph);
        Issues = Validation.Check(Graph);
        _hidden.RemoveWhere(id => !Graph.HasEdge(id));
        _renderer?.SetNetwork(Graph, Footprints, Issues, ColorOf, _hidden);
        Changed?.Invoke();
    }

    private void TrimHistory()
    {
        var keep = new List<SplineGraph>(_undo);
        keep.RemoveAt(keep.Count - 1); // the oldest
        _undo.Clear();
        for (int i = keep.Count - 1; i >= 0; i--) _undo.Push(keep[i]);
    }
}
