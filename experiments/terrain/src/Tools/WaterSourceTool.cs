using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CitySim.TerrainSystem;
using CitySim.WaterSystem;

namespace CitySim.Tools;

/// <summary>
/// Places and edits water sources (the Water tab), after the CS2 Water Features mod. Left-click on ground places a
/// source of the current kind; left-click a source selects it and drag moves it; right-click a source removes it;
/// right-click on ground picks the target elevation (the sea level for the Sea). The settings edit the selected source,
/// else they're used for the next one. Rivers snap onto the map border when placed near it. Every change is one undo step.
/// Owned by <see cref="TerrainToolController"/>, which hands it the water tools' input.
/// </summary>
public sealed class WaterSourceTool
{
    private readonly TerrainToolController _c;
    private readonly Dictionary<WaterSourceKind, float> _radius = new()
    {
        [WaterSourceKind.Stream] = 15f, [WaterSourceKind.River] = 60f, [WaterSourceKind.Lake] = 80f, [WaterSourceKind.Sea] = 0f,
    };
    private readonly Dictionary<WaterSourceKind, float> _depth = new()
    {
        [WaterSourceKind.River] = 4f, [WaterSourceKind.Lake] = 3f,
    };
    private float _flowRate = 10f, _pollution, _maxFlow = 100f, _seaLevel = float.NaN;
    private float? _pickedLevel;
    private bool _snap = true;
    // Dragging: the source's position when the press landed, and the cursor's offset from its centre.
    private WaterSource[]? _dragBefore;
    private Vector2 _dragOffset;
    private bool _dragMoved;

    public WaterSourceTool(TerrainToolController controller) => _c = controller;

    public WaterSourceKind Kind { get; private set; } = WaterSourceKind.River;
    public int? Selected { get; private set; }
    public int? Hovered { get; private set; }
    public bool IsDragging => _dragBefore is not null;

    private Terrain? Terrain => _c.Terrain;
    private WaterSim? Sim => _c.Terrain?.Water;
    public WaterSource? SelectedSource => Sim?.Sources.FirstOrDefault(s => s.Id == Selected);

    // --- Settings (edit the selected source, else apply to the next one) ---

    public float Radius
    {
        get => _radius[Kind];
        set { _radius[Kind] = Mathf.Clamp(value, 5f, 1000f); Edit(s => s with { Radius = _radius[Kind] }); }
    }

    /// <summary>River/Lake: metres of water over the ground at the centre (unless a target elevation was picked).</summary>
    public float Depth
    {
        get => _depth.GetValueOrDefault(Kind);
        set
        {
            _depth[Kind] = Mathf.Clamp(value, 0.5f, 100f);
            _pickedLevel = null;
            Edit(s => s with { Level = GroundAt(s.X, s.Z) + _depth[Kind] });
        }
    }

    /// <summary>River/Lake: a picked absolute target elevation (right-click on ground), or null for ground + depth.</summary>
    public float? PickedLevel
    {
        get => _pickedLevel;
        set
        {
            _pickedLevel = value;
            if (value is { } level) Edit(s => s with { Level = level });
            else Edit(s => s with { Level = GroundAt(s.X, s.Z) + Depth });
        }
    }

    /// <summary>Stream: m³/s.</summary>
    public float FlowRate
    {
        get => _flowRate;
        set { _flowRate = Mathf.Clamp(value, 0.1f, 5000f); Edit(s => s with { FlowRate = _flowRate }); }
    }

    /// <summary>Stream: kg/s of pollutant it adds (a sewage outlet); 0 = clean.</summary>
    public float Pollution
    {
        get => _pollution;
        set { _pollution = value < 0.05f ? 0f : Mathf.Clamp(value, 0.1f, 1000f); Edit(s => s with { Pollution = _pollution }); }
    }

    /// <summary>Steps <see cref="Pollution"/> through 0, 0.1, 0.2, 0.4 … kg/s.</summary>
    public void StepPollution(int dir) => Pollution = dir > 0 ? (_pollution <= 0f ? 0.1f : _pollution * 2f) : _pollution / 2f;

    /// <summary>Lake: most m³/s it fills with.</summary>
    public float MaxFlow
    {
        get => _maxFlow;
        set { _maxFlow = Mathf.Clamp(value, 1f, 50000f); Edit(s => s with { MaxFlow = _maxFlow }); }
    }

    /// <summary>The map's sea level (the Sea source's, else the default for placing one).</summary>
    public float SeaLevel
    {
        get => Sea()?.Level ?? (float.IsNaN(_seaLevel) ? Terrain?.SeaLevel ?? 0f : _seaLevel);
        set
        {
            _seaLevel = value;
            if (Sea() is { } sea) Replace(sea, sea with { Level = value });
            else _c.NotifyChanged();
        }
    }

    /// <summary>River: snap onto the map border when placed near it.</summary>
    public bool Snap
    {
        get => _snap;
        set { _snap = value; _c.NotifyChanged(); }
    }

    // --- Input ---

    /// <summary>The water tool changed; a selection of another kind is dropped.</summary>
    public void SetKind(WaterSourceKind kind)
    {
        if (SelectedSource is { } s && s.Kind != kind) Selected = null;
        Kind = kind;
        _pickedLevel = null;
    }

    /// <summary>Esc: drops the selection. Returns false if nothing was selected.</summary>
    public bool Deselect()
    {
        if (Selected is null) return false;
        Selected = null;
        _c.NotifyChanged();
        return true;
    }

    public void Press(bool left, Vector3 cursor)
    {
        if (Sim is not { } sim) return;
        var p = Local(cursor);
        var hit = HitTest(p);
        if (!left)
        {
            if (hit is { } h) Remove(h);
            else if (Kind == WaterSourceKind.Sea) SeaLevel = cursor.Y;
            else if (Kind != WaterSourceKind.Stream) PickedLevel = cursor.Y;
            return;
        }
        if (hit is { } source)
        {
            Select(source);
            _dragBefore = sim.Sources.ToArray();
            _dragOffset = new Vector2(source.X, source.Z) - p;
            _dragMoved = false;
            return;
        }
        if (Kind == WaterSourceKind.Sea && Sea() is { } sea)
        {
            // One sea per map: clicking elsewhere moves its marker.
            Replace(sea, sea with { X = p.X, Z = p.Y });
            Select(sea);
            return;
        }
        Add(p);
    }

    /// <summary>Per frame: hover, dragging, and the placement preview ring.</summary>
    public void Process(Vector3? cursor, bool leftHeld)
    {
        if (Terrain is not { } terrain || Sim is not { } sim) return;
        if (IsDragging)
        {
            if (!leftHeld) EndDrag();
            else if (cursor is { } c && SelectedSource is { } s)
            {
                var to = Local(c) + _dragOffset;
                if (s.Kind == WaterSourceKind.River) to = Snapped(to, s.Radius);
                if (to.DistanceTo(new Vector2(s.X, s.Z)) > 0.01f)
                {
                    _dragMoved = true;
                    sim.SetSources(sim.Sources.Select(x => x.Id == s.Id ? s with { X = to.X, Z = to.Y } : x));
                }
            }
        }
        Hovered = IsDragging ? Selected : cursor is { } h ? HitTest(Local(h))?.Id : null;
        terrain.ShowWaterSources(true, Hovered, Selected);

        // Preview ring where a click would place a source (hidden over an existing one).
        bool preview = cursor is not null && Hovered is null && !IsDragging;
        var at = cursor is { } cur ? Local(cur) : Vector2.Zero;
        if (Kind == WaterSourceKind.River) at = Snapped(at, Radius);
        float r = Kind == WaterSourceKind.Sea ? WaterSourceMarkers.SeaMarkerRadius : Radius;
        terrain.SetBrush(new Vector3(at.X, terrain.GetHeight(at.X, at.Y), at.Y), r, preview);
    }

    /// <summary>The tool was put away: stop dragging and hide the markers.</summary>
    public void Leave()
    {
        if (IsDragging) EndDrag();
        Hovered = null;
        Terrain?.ShowWaterSources(false);
    }

    // --- Edits ---

    private void Add(Vector2 p)
    {
        if (Sim is not { } sim) return;
        if (Kind == WaterSourceKind.River) p = Snapped(p, Radius);
        float level = Kind switch
        {
            WaterSourceKind.Sea => SeaLevel,
            WaterSourceKind.Stream => 0f,
            _ => _pickedLevel ?? GroundAt(p.X, p.Y) + Depth,
        };
        var s = new WaterSource(sim.NextSourceId(), Kind, p.X, p.Y, Kind == WaterSourceKind.Sea ? 0f : Radius, level,
            Kind == WaterSourceKind.Stream ? _flowRate : 0f, Kind == WaterSourceKind.Lake ? _maxFlow : 0f,
            Kind == WaterSourceKind.Stream ? _pollution : 0f);
        Commit(sim.Sources.Append(s).ToArray());
        Selected = s.Id;
        _c.NotifyChanged();
    }

    private void Remove(WaterSource s)
    {
        if (Sim is not { } sim) return;
        if (Selected == s.Id) Selected = null;
        Commit(sim.Sources.Where(x => x.Id != s.Id).ToArray());
    }

    private void Replace(WaterSource old, WaterSource now)
    {
        if (Sim is not { } sim || old == now) return;
        Commit(sim.Sources.Select(x => x.Id == old.Id ? now : x).ToArray());
    }

    /// <summary>Applies a settings change to the selected source (if it's of the current kind).</summary>
    private void Edit(Func<WaterSource, WaterSource> change)
    {
        if (SelectedSource is { } s && s.Kind == Kind) Replace(s, change(s));
        else _c.NotifyChanged();
    }

    private void Select(WaterSource s)
    {
        Selected = s.Id;
        // Show its values in the settings, and switch to its tool.
        Kind = s.Kind;
        if (s.Kind != WaterSourceKind.Sea) _radius[s.Kind] = s.Radius;
        if (s.Kind == WaterSourceKind.Stream)
        {
            _flowRate = s.FlowRate;
            _pollution = s.Pollution;
        }
        if (s.Kind == WaterSourceKind.Lake) _maxFlow = s.MaxFlow;
        if (s.Kind is WaterSourceKind.River or WaterSourceKind.Lake)
        {
            _depth[s.Kind] = MathF.Max(s.Level - GroundAt(s.X, s.Z), 0.5f);
            _pickedLevel = null;
        }
        _c.SelectWaterTool(s.Kind);
        _c.NotifyChanged();
    }

    private void EndDrag()
    {
        if (_dragBefore is { } before && _dragMoved && Sim is { } sim) PushUndo(sim, before, sim.Sources.ToArray());
        _dragBefore = null;
        _c.NotifyChanged();
    }

    private void Commit(WaterSource[] after)
    {
        if (Sim is not { } sim) return;
        var before = sim.Sources.ToArray();
        sim.SetSources(after);
        PushUndo(sim, before, after);
        _c.NotifyChanged();
    }

    private void PushUndo(WaterSim sim, WaterSource[] before, WaterSource[] after)
    {
        _c.History.PushAction(
            () => { if (Sim == sim) { sim.SetSources(before); _c.NotifyChanged(); } },
            () => { if (Sim == sim) { sim.SetSources(after); _c.NotifyChanged(); } });
    }

    // --- Helpers ---

    private WaterSource? Sea() => Sim?.Sources.FirstOrDefault(s => s.Kind == WaterSourceKind.Sea);

    /// <summary>The source under a local position: inside its ring (the sea's marker ring), nearest centre first.</summary>
    private WaterSource? HitTest(Vector2 p)
    {
        if (Sim is null) return null;
        WaterSource? best = null;
        float bestD = float.MaxValue;
        foreach (var s in Sim.Sources)
        {
            float r = s.Kind == WaterSourceKind.Sea ? WaterSourceMarkers.SeaMarkerRadius : MathF.Max(s.Radius, 10f);
            float d = p.DistanceTo(new Vector2(s.X, s.Z));
            if (d <= r && d < bestD) { best = s; bestD = d; }
        }
        return best;
    }

    /// <summary>Near the border (within the radius plus 30 m), moves onto it, like the mod's border river.</summary>
    private Vector2 Snapped(Vector2 p, float radius)
    {
        if (!_snap || Terrain?.Map is not { } map) return p;
        float sx = map.SizeX, sz = map.SizeZ, reach = radius + 30f;
        float west = p.X, east = sx - p.X, north = p.Y, south = sz - p.Y;
        float m = MathF.Min(MathF.Min(west, east), MathF.Min(north, south));
        if (m > reach) return p;
        if (m == west) return new Vector2(0f, p.Y);
        if (m == east) return new Vector2(sx, p.Y);
        if (m == north) return new Vector2(p.X, 0f);
        return new Vector2(p.X, sz);
    }

    private float GroundAt(float x, float z) => Terrain is { } t ? t.GetHeight(x + t.GlobalPosition.X, z + t.GlobalPosition.Z) : 0f;

    private Vector2 Local(Vector3 world)
    {
        var o = Terrain?.GlobalPosition ?? Vector3.Zero;
        return new Vector2(world.X - o.X, world.Z - o.Z);
    }
}
