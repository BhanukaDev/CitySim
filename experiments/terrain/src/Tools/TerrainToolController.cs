using System;
using Godot;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Sculpt;

namespace CitySim.Tools;

public enum TerrainTool { None, Shift, Level, Smooth, Slope, Paint }

/// <summary>
/// Turns mouse input into sculpt strokes on the terrain.
///
/// Shift: LMB raise, RMB lower. Level: RMB picks the target height; with none picked, each stroke
/// levels to the dominant height under the brush. Smooth: LMB. Slope: RMB sets the start point,
/// LMB-drag builds a ramp from it to where you pressed. Paint: LMB paints <see cref="PaintLayer"/>,
/// RMB erases painting (the ground goes back to the automatic layers).
/// G toggles the grid. Brush: [ / ] or Shift+wheel for size, Alt+wheel for strength. Ctrl/Cmd+Z undo, +Shift (or Ctrl+Y) redo.
/// </summary>
public partial class TerrainToolController : Node
{
    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }

    [Export(PropertyHint.Range, "1,100,1,suffix:m")] public float MinRadius { get; set; } = 8f;
    [Export(PropertyHint.Range, "50,1000,10,suffix:m")] public float MaxRadius { get; set; } = 400f;

    /// <summary>Edits run at a fixed rate so a stroke's effect doesn't depend on frame rate.</summary>
    private const float Tick = 1f / 60f;
    private const int MaxTicksPerFrame = 4;

    private TerrainTool _tool;
    private float _radius = 60f;
    private float _strength = 0.5f;
    private float? _levelTarget;
    private Vector3? _slopeAnchor;
    private bool _showContours;
    private float _maxSlope = SculptOps.DefaultMaxSlopeDegrees;
    private int _contourIndex = 2;
    private int _paintLayer = TerrainLayers.Dirt;
    private bool _showGrid;

    /// <summary>Contour spacings offered in the UI, in metres.</summary>
    public static readonly float[] ContourIntervals = [1f, 2f, 5f, 10f, 20f];

    private MouseButton _strokeButton = MouseButton.None;
    private float _strokeSign;
    private float _strokeLevelTarget;
    private Vector3 _slopeEnd;
    private float _tickAccum;
    private HeightMap? _map;

    /// <summary>Raised whenever the tool, brush settings, level target or slope anchor change.</summary>
    public event Action? StateChanged;

    public UndoStack History { get; } = new();

    /// <summary>Terrain point under the mouse, if any.</summary>
    public Vector3? Cursor { get; private set; }

    /// <summary>When set, used instead of the mouse position (for scripted screenshots).</summary>
    public Vector3? ForcedCursor { get; set; }

    public bool IsStroking => _strokeButton != MouseButton.None;

    public TerrainTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            EndStroke();
            _tool = value;
            Changed();
        }
    }

    public float BrushRadius
    {
        get => _radius;
        set { _radius = Mathf.Clamp(value, MinRadius, MaxRadius); Changed(); }
    }

    public float BrushStrength
    {
        get => _strength;
        set { _strength = Mathf.Clamp(value, 0.05f, 1f); Changed(); }
    }

    /// <summary>Level tool target height (world Y). Null means "dominant height under the brush".</summary>
    public float? LevelTarget
    {
        get => _levelTarget;
        set { _levelTarget = value; Changed(); }
    }

    /// <summary>Slope tool start point (world space).</summary>
    public Vector3? SlopeAnchor
    {
        get => _slopeAnchor;
        set { _slopeAnchor = value; Changed(); }
    }

    /// <summary>Steepest angle the Shift tool will build, in degrees.</summary>
    public float MaxSlopeDegrees
    {
        get => _maxSlope;
        set { _maxSlope = Mathf.Clamp(value, 10f, 80f); Changed(); }
    }

    /// <summary>Draws height contour lines on the terrain while a terrain tool is active.</summary>
    public bool ShowContours
    {
        get => _showContours;
        set { _showContours = value; Changed(); }
    }

    public float ContourInterval => ContourIntervals[_contourIndex];

    /// <summary>Layer the Paint tool paints (an index into <see cref="TerrainLayers.All"/>, paintable only).</summary>
    public int PaintLayer
    {
        get => _paintLayer;
        set { _paintLayer = Math.Clamp(value, 0, TerrainLayers.PaintableCount - 1); Changed(); }
    }

    /// <summary>Shows the placement grid on the terrain (any tool, or none).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; Changed(); }
    }

    /// <summary>Steps through <see cref="ContourIntervals"/>.</summary>
    public void StepContourInterval(int dir)
    {
        _contourIndex = Math.Clamp(_contourIndex + dir, 0, ContourIntervals.Length - 1);
        Changed();
    }

    private Brush CurrentBrush => new(_radius, _strength);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } key)
            HandleKey(key);
        else if (@event is InputEventMouseButton mb)
            HandleMouseButton(mb);
    }

    private void HandleKey(InputEventKey key)
    {
        bool handled = true;
        if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            if (key.ShiftPressed) Redo(); else Undo();
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed())
            Redo();
        else if (key.Keycode == Key.Bracketleft && _tool != TerrainTool.None)
            BrushRadius /= 1.15f;
        else if (key.Keycode == Key.Bracketright && _tool != TerrainTool.None)
            BrushRadius *= 1.15f;
        else if (key.Keycode == Key.Escape && _tool != TerrainTool.None)
            Tool = TerrainTool.None;
        else if (key.Keycode == Key.G && !key.IsCommandOrControlPressed() && !key.Echo)
            ShowGrid = !ShowGrid;
        else
            handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (_tool == TerrainTool.None) return;

        if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float dir = mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
            if (mb.ShiftPressed) BrushRadius *= Mathf.Pow(1.1f, dir);
            else if (mb.AltPressed) BrushStrength += 0.05f * dir;
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
        GetViewport().SetInputAsHandled();

        if (!mb.Pressed)
        {
            if (mb.ButtonIndex == _strokeButton) EndStroke();
            return;
        }
        if (IsStroking || Cursor is not { } hit) return;

        bool left = mb.ButtonIndex == MouseButton.Left;
        switch (_tool)
        {
            case TerrainTool.Shift:
                BeginStroke(mb.ButtonIndex, left ? 1f : -1f);
                break;
            case TerrainTool.Level:
                if (left) BeginStroke(mb.ButtonIndex);
                else LevelTarget = hit.Y;
                break;
            case TerrainTool.Smooth:
                if (left) BeginStroke(mb.ButtonIndex);
                break;
            case TerrainTool.Paint:
                BeginStroke(mb.ButtonIndex, left ? 1f : -1f);
                break;
            case TerrainTool.Slope:
                if (!left) SlopeAnchor = hit;
                else if (_slopeAnchor.HasValue)
                {
                    _slopeEnd = hit;
                    BeginStroke(mb.ButtonIndex);
                }
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (Terrain?.Map is not { } map) return;
        if (!ReferenceEquals(map, _map))
        {
            // Terrain was regenerated: old undo data and points no longer apply.
            EndStroke();
            History.Clear();
            _map = map;
            _slopeAnchor = null;
            Changed();
        }

        UpdateCursor();

        // The release can land on a UI panel and never reach us.
        if (IsStroking && ForcedCursor is null && !Input.IsMouseButtonPressed(_strokeButton))
            EndStroke();

        if (IsStroking && Cursor is { } hit)
        {
            _tickAccum = Mathf.Min(_tickAccum + (float)delta, Tick * MaxTicksPerFrame);
            while (_tickAccum >= Tick)
            {
                _tickAccum -= Tick;
                ApplyTick(hit, Tick);
            }
        }

        bool showBrush = _tool != TerrainTool.None && Cursor.HasValue;
        Terrain.SetBrush(Cursor ?? Vector3.Zero, _radius, showBrush);
        Terrain.SetAnchor(_tool == TerrainTool.Slope ? _slopeAnchor : null);
        Terrain.SetContours(_showContours && _tool != TerrainTool.None, ContourInterval);
        Terrain.SetGrid(_showGrid);
    }

    private void UpdateCursor()
    {
        if (ForcedCursor.HasValue)
        {
            Cursor = ForcedCursor;
            return;
        }
        Cursor = null;
        var viewport = GetViewport();
        if (Terrain is null || CityCamera?.Camera is not { } cam || viewport.GuiGetHoveredControl() is not null)
            return;
        var mouse = viewport.GetMousePosition();
        if (Terrain.Raycast(cam.ProjectRayOrigin(mouse), cam.ProjectRayNormal(mouse), out var hit))
            Cursor = hit;
    }

    private void BeginStroke(MouseButton button, float sign = 1f)
    {
        if (Terrain?.Map is not { } map || Cursor is not { } hit) return;
        _strokeButton = button;
        _strokeSign = sign;
        _tickAccum = Tick; // apply the first tick immediately
        if (_tool == TerrainTool.Level)
            _strokeLevelTarget = _levelTarget
                ?? SculptOps.DominantHeight(map, ToLocal2(hit), CurrentBrush) + Terrain.GlobalPosition.Y;
        if (_tool == TerrainTool.Paint) History.BeginStroke(null, Terrain.Splat);
        else History.BeginStroke(map);
    }

    private void EndStroke()
    {
        if (!IsStroking) return;
        _strokeButton = MouseButton.None;
        if (Terrain?.Map is { } map && Terrain.Splat is { } splat)
        {
            History.EndStroke(map, splat);
            Terrain.RefreshHeightRange();
        }
    }

    private void ApplyTick(Vector3 hit, float dt)
    {
        if (Terrain?.Map is not { } map) return;
        var c = ToLocal2(hit);
        var brush = CurrentBrush;
        if (_tool == TerrainTool.Paint)
        {
            if (Terrain.Splat is not { } splat) return;
            var painted = _strokeSign > 0f
                ? PaintOps.Paint(splat, c, brush, _paintLayer, dt)
                : PaintOps.Erase(splat, c, brush, dt);
            History.Touch(painted);
            Terrain.MarkSplatDirty(painted);
            return;
        }
        float oy = Terrain.GlobalPosition.Y;
        var rect = _tool switch
        {
            TerrainTool.Shift => SculptOps.Shift(map, c, brush, _strokeSign, dt, _maxSlope),
            TerrainTool.Level => SculptOps.Level(map, c, brush, _strokeLevelTarget - oy, dt),
            TerrainTool.Smooth => SculptOps.Smooth(map, c, brush, dt),
            TerrainTool.Slope when _slopeAnchor is { } a =>
                SculptOps.Slope(map, c, brush, ToLocal2(a), a.Y - oy, ToLocal2(_slopeEnd), _slopeEnd.Y - oy, dt),
            _ => VertexRect.Empty,
        };
        History.Touch(rect);
        Terrain.MarkDirty(rect);
    }

    public void Undo() => ApplyHistory(History.Undo);
    public void Redo() => ApplyHistory(History.Redo);

    private void ApplyHistory(Func<HeightMap, SplatMap, UndoChange> op)
    {
        if (IsStroking || Terrain?.Map is not { } map || Terrain.Splat is not { } splat) return;
        var change = op(map, splat);
        if (change.Splat) Terrain.MarkSplatDirty(change.Rect);
        if (!change.Heights) return;
        Terrain.MarkDirty(change.Rect);
        Terrain.RefreshHeightRange();
    }

    private System.Numerics.Vector2 ToLocal2(Vector3 world)
    {
        var o = Terrain?.GlobalPosition ?? Vector3.Zero;
        return new System.Numerics.Vector2(world.X - o.X, world.Z - o.Z);
    }

    private void Changed() => StateChanged?.Invoke();

    // --- Scripted demo for automated screenshots (--demo-sculpt) ---

    /// <summary>Runs one stroke of <paramref name="ticks"/> ticks, moving the brush from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void DemoStroke(TerrainTool tool, Vector2 from, Vector2 to, int ticks, float sign = 1f, MouseButton button = MouseButton.Left)
    {
        if (Terrain?.Map is not { } map) return;
        Tool = tool;
        Vector3 At(Vector2 p) => new(p.X, Terrain.GetHeight(p.X, p.Y), p.Y);
        ForcedCursor = At(from);
        UpdateCursor();
        BeginStroke(button, sign);
        for (int i = 0; i < ticks; i++)
            ApplyTick(At(from.Lerp(to, ticks > 1 ? i / (float)(ticks - 1) : 0f)), Tick);
        EndStroke();
    }

    /// <summary>Sculpts a mound, a levelled pad and a ramp near the map centre, then leaves the slope tool active.</summary>
    public void RunDemo()
    {
        if (Terrain?.Map is null) return;
        _map = Terrain.Map;
        var c = Terrain.Bounds.GetCenter();

        BrushRadius = 70f;
        BrushStrength = 0.6f;
        DemoStroke(TerrainTool.Shift, c + new Vector2(-180, 0), c + new Vector2(-180, 0), 150);
        DemoStroke(TerrainTool.Smooth, c + new Vector2(-180, 0), c + new Vector2(-180, 0), 60);
        BrushRadius = 10f;
        DemoStroke(TerrainTool.Shift, c + new Vector2(-60, -60), c + new Vector2(-60, -60), 600);
        BrushRadius = 70f;
        LevelTarget = null;
        DemoStroke(TerrainTool.Level, c + new Vector2(170, -40), c + new Vector2(170, 40), 180);

        BrushRadius = 25f;
        Tool = TerrainTool.Slope;
        var a = c + new Vector2(-60, 220);
        SlopeAnchor = new Vector3(a.X, Terrain.GetHeight(a.X, a.Y) + 20f, a.Y);
        _slopeEnd = new Vector3(c.X + 160, Terrain.GetHeight(c.X + 160, c.Y + 220), c.Y + 220);
        DemoStroke(TerrainTool.Slope, a, new Vector2(_slopeEnd.X, _slopeEnd.Z), 240);

        // Undo/redo self-check: an extra stroke must undo back to the exact previous heights.
        var map = Terrain.Map!;
        var before = map.Snapshot();
        DemoStroke(TerrainTool.Shift, c, c + new Vector2(100, 100), 60);
        var after = map.Snapshot();
        Undo();
        bool undoOk = map.Snapshot().AsSpan().SequenceEqual(before);
        Redo();
        bool redoOk = map.Snapshot().AsSpan().SequenceEqual(after);
        Undo();
        GD.Print($"Demo sculpt: undo {(undoOk ? "ok" : "FAILED")}, redo {(redoOk ? "ok" : "FAILED")}");
        Tool = TerrainTool.Slope;

        ShowContours = true;
        BrushRadius = 60f;
        var cursor = c + new Vector2(20, 120);
        ForcedCursor = new Vector3(cursor.X, Terrain.GetHeight(cursor.X, cursor.Y), cursor.Y);
    }

    /// <summary>Paints a dirt path, a sand patch and a gravel patch near the map centre, then checks undo/redo.</summary>
    public void RunPaintDemo()
    {
        if (Terrain?.Map is null || Terrain.Splat is null) return;
        _map = Terrain.Map;
        var c = Terrain.Bounds.GetCenter();

        BrushStrength = 0.8f;
        BrushRadius = 12f;
        PaintLayer = TerrainLayers.Dirt;
        DemoStroke(TerrainTool.Paint, c + new Vector2(-250, -120), c + new Vector2(250, 60), 240);
        BrushRadius = 60f;
        PaintLayer = TerrainLayers.Sand;
        DemoStroke(TerrainTool.Paint, c + new Vector2(-120, 120), c + new Vector2(-60, 140), 90);
        PaintLayer = TerrainLayers.Gravel;
        DemoStroke(TerrainTool.Paint, c + new Vector2(150, 150), c + new Vector2(150, 150), 60);
        BrushRadius = 30f;
        DemoStroke(TerrainTool.Paint, c + new Vector2(150, 150), c + new Vector2(150, 150), 40, -1f, MouseButton.Right);

        // Undo/redo self-check on an extra stroke.
        var splat = Terrain.Splat;
        var before = splat.Snapshot();
        PaintLayer = TerrainLayers.Snow;
        DemoStroke(TerrainTool.Paint, c, c + new Vector2(80, 0), 30);
        var after = splat.Snapshot();
        Undo();
        bool undoOk = splat.Snapshot().AsSpan().SequenceEqual(before);
        Redo();
        bool redoOk = splat.Snapshot().AsSpan().SequenceEqual(after);
        Undo();
        GD.Print($"Demo paint: undo {(undoOk ? "ok" : "FAILED")}, redo {(redoOk ? "ok" : "FAILED")}");

        PaintLayer = TerrainLayers.Dirt;
        BrushRadius = 40f;
        var cursor = c + new Vector2(0, -40);
        ForcedCursor = new Vector3(cursor.X, Terrain.GetHeight(cursor.X, cursor.Y), cursor.Y);
    }
}
