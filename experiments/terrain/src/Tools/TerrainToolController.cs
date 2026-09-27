using System;
using Godot;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;
using CitySim.TerrainSystem.Sculpt;
using CitySim.WaterSystem;

namespace CitySim.Tools;

public enum TerrainTool { None, Shift, Level, Smooth, Slope, Paint, WaterStream, WaterRiver, WaterLake, WaterSea }

/// <summary>How the brush angle is chosen: set by hand, rolled at random on each press, or turned along the drag.</summary>
public enum BrushRotationMode { Fixed, Random, Follow }

/// <summary>
/// Turns mouse input into sculpt strokes on the terrain.
///
/// Shift: LMB raise, RMB lower. Level: RMB picks the target height; with none picked, each stroke
/// levels to the dominant height under the brush. Smooth: LMB. Slope: RMB sets the start point,
/// LMB-drag builds a ramp from it to where you pressed. Paint: LMB paints <see cref="PaintLayer"/>,
/// RMB erases painting (the ground goes back to the automatic layers). Water tools (Stream, River, Lake, Sea) place
/// and edit water sources; see <see cref="WaterSourceTool"/>.
/// G toggles the grid, C the contour lines. Brush: [ / ] or Shift+wheel for size, Alt+wheel for strength,
/// hold Ctrl and move the mouse to rotate (the brush stays put), Ctrl+Q/E for 15° steps (45° with Shift), Ctrl+wheel.
/// Ctrl/Cmd+Z undo, +Shift (or Ctrl+Y) redo.
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
    private int _paintLayer;
    private bool _showGrid;
    private int _brushIndex;
    private float _brushAngle;
    private BrushRotationMode _rotationMode;
    private Vector2? _followFrom;
    /// <summary>Where the brush is held while Ctrl + mouse movement rotates it.</summary>
    private Vector3? _rotateHold;

    /// <summary>Degrees of brush rotation per pixel of sideways mouse movement while Ctrl is held.</summary>
    private const float RotateDegreesPerPixel = 0.5f;
    private readonly Random _rng = new();

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

    /// <summary>The water tools' state and input (sources, their settings, the selection).</summary>
    public WaterSourceTool Water { get; }

    public TerrainToolController() => Water = new WaterSourceTool(this);

    /// <summary>The water source kind a tool places, or null for other tools.</summary>
    public static WaterSourceKind? WaterKind(TerrainTool tool) => tool switch
    {
        TerrainTool.WaterStream => WaterSourceKind.Stream,
        TerrainTool.WaterRiver => WaterSourceKind.River,
        TerrainTool.WaterLake => WaterSourceKind.Lake,
        TerrainTool.WaterSea => WaterSourceKind.Sea,
        _ => null,
    };

    public static TerrainTool ToolFor(WaterSourceKind kind) => kind switch
    {
        WaterSourceKind.Stream => TerrainTool.WaterStream,
        WaterSourceKind.River => TerrainTool.WaterRiver,
        WaterSourceKind.Lake => TerrainTool.WaterLake,
        _ => TerrainTool.WaterSea,
    };

    public bool IsWaterTool => WaterKind(_tool) is not null;

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
            if (IsWaterTool && WaterKind(value) is null) Water.Leave();
            _tool = value;
            if (WaterKind(value) is { } kind) Water.SetKind(kind);
            // A fresh visit to the slope tool starts without a start point.
            _slopeAnchor = null;
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

    /// <summary>Material the Paint tool paints: an index into the theme's materials.</summary>
    public int PaintLayer
    {
        get => _paintLayer;
        set { _paintLayer = Math.Clamp(value, 0, Math.Max((Terrain?.Theme?.Materials.Count ?? 1) - 1, 0)); Changed(); }
    }

    /// <summary>The theme material <see cref="PaintLayer"/> points at.</summary>
    public CitySim.TerrainSystem.Themes.TerrainMaterial? PaintMaterial =>
        Terrain?.Theme is { } t && _paintLayer < t.Materials.Count ? t.Materials[_paintLayer] : null;

    // The theme's material with this id, else its material number `fallback` (wrapped): demos run on any theme.
    private int DemoMaterial(string id, int fallback)
    {
        int count = Math.Max(Terrain?.Theme?.Materials.Count ?? 1, 1);
        int i = Terrain?.Theme?.IndexOf(id) ?? -1;
        return i >= 0 ? i : fallback % count;
    }

    /// <summary>Shows the placement grid on the terrain (any tool, or none).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; Changed(); }
    }

    /// <summary>Brush shape: an index into <see cref="BrushLibrary.All"/> (0 = round).</summary>
    public int BrushIndex
    {
        get => _brushIndex;
        set { _brushIndex = Math.Clamp(value, 0, BrushLibrary.All.Length - 1); Changed(); }
    }

    /// <summary>Brush rotation in degrees (0 to 360), counter-clockwise seen from above.</summary>
    public float BrushAngle
    {
        get => _brushAngle;
        set { _brushAngle = Mathf.PosMod(value, 360f); Changed(); }
    }

    public BrushRotationMode RotationMode
    {
        get => _rotationMode;
        set { _rotationMode = value; _followFrom = null; Changed(); }
    }

    public void CycleRotationMode() =>
        RotationMode = (BrushRotationMode)(((int)_rotationMode + 1) % Enum.GetValues<BrushRotationMode>().Length);

    /// <summary>Whether the current tool uses the brush shape and angle. Slope always uses a round brush.</summary>
    public bool UsesBrushShape => _tool is not (TerrainTool.None or TerrainTool.Slope) && !IsWaterTool;

    /// <summary>Switches to a water tool without dropping the selected source (used when a source is selected).</summary>
    public void SelectWaterTool(WaterSourceKind kind)
    {
        var tool = ToolFor(kind);
        if (_tool == tool) return;
        EndStroke();
        _tool = tool;
        Changed();
    }

    /// <summary>Raises <see cref="StateChanged"/> (for the water tool's settings).</summary>
    public void NotifyChanged() => Changed();

    /// <summary>Steps through <see cref="ContourIntervals"/>.</summary>
    public void StepContourInterval(int dir)
    {
        _contourIndex = Math.Clamp(_contourIndex + dir, 0, ContourIntervals.Length - 1);
        Changed();
    }

    private Brush CurrentBrush => new(_radius, _strength, BrushLibrary.Get(_brushIndex).Mask, Mathf.DegToRad(_brushAngle));

    public override void _Ready() => BrushLibrary.Load();

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } key)
            HandleKey(key);
        else if (@event is InputEventMouseButton mb)
            HandleMouseButton(mb);
        else if (@event is InputEventMouseMotion motion)
            HandleMouseMotion(motion);
        else if (@event is InputEventPanGesture { CtrlPressed: true } pan && _tool != TerrainTool.None)
        {
            // macOS trackpad: Ctrl + two-finger scroll rotates the brush, like Ctrl+wheel.
            BrushAngle += pan.Delta.Y * 3f;
            GetViewport().SetInputAsHandled();
        }
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
        else if (key.Keycode is Key.Bracketleft or Key.Bracketright && IsWaterTool)
            Water.Radius *= key.Keycode == Key.Bracketright ? 1.15f : 1f / 1.15f;
        else if (key.Keycode == Key.Bracketleft && _tool != TerrainTool.None)
            BrushRadius /= 1.15f;
        else if (key.Keycode == Key.Bracketright && _tool != TerrainTool.None)
            BrushRadius *= 1.15f;
        else if (key.Keycode == Key.Escape && IsWaterTool && Water.Deselect()) { }
        else if (key.Keycode == Key.Escape && _tool != TerrainTool.None)
            Tool = TerrainTool.None;
        else if (key.Keycode is Key.Q or Key.E && key.CtrlPressed && UsesBrushShape)
            BrushAngle += (key.Keycode == Key.Q ? 1f : -1f) * (key.ShiftPressed ? 45f : 15f);
        else if (key.Keycode == Key.G && !key.IsCommandOrControlPressed() && !key.Echo)
            ShowGrid = !ShowGrid;
        else if (key.Keycode == Key.C && !key.IsCommandOrControlPressed() && !key.Echo)
            ShowContours = !ShowContours;
        else
            handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    /// <summary>Ctrl + mouse movement: the brush stays where it is and turns with sideways movement (right = clockwise).</summary>
    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (!UsesBrushShape || IsStroking || !Input.IsKeyPressed(Key.Ctrl)) return;
        if (_rotateHold is null)
        {
            if (Cursor is not { } c) return;
            _rotateHold = c;
        }
        BrushAngle -= motion.Relative.X * RotateDegreesPerPixel;
        GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (_tool == TerrainTool.None) return;

        if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float dir = mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
            if (mb.ShiftPressed && IsWaterTool) Water.Radius *= Mathf.Pow(1.1f, dir);
            else if (IsWaterTool) return;
            else if (mb.ShiftPressed) BrushRadius *= Mathf.Pow(1.1f, dir);
            else if (mb.AltPressed) BrushStrength += 0.05f * dir;
            else if (mb.CtrlPressed) BrushAngle += 5f * dir;
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
        if (IsWaterTool)
        {
            Water.Press(left, hit);
            return;
        }
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
            _generatedBefore = null;
            _map = map;
            _slopeAnchor = null;
            Water.Deselect();
            Changed();
        }

        UpdateCursor();
        FollowCursor();

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

        if (IsWaterTool)
            Water.Process(Cursor, ForcedCursor is null && Input.IsMouseButtonPressed(MouseButton.Left));
        else
        {
            bool showBrush = _tool != TerrainTool.None && Cursor.HasValue;
            var shape = UsesBrushShape ? BrushLibrary.Get(_brushIndex) : null;
            Terrain.SetBrush(Cursor ?? Vector3.Zero, _radius, showBrush, shape?.Mask is null ? null : shape.Texture,
                Mathf.DegToRad(_brushAngle), UsesBrushShape);
        }
        Terrain.SetAnchor(_tool == TerrainTool.Slope ? _slopeAnchor : null);
        Terrain.SetContours(_showContours, ContourInterval);
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
        if (Terrain is null || CityCamera?.Camera is not { } cam) return;
        if (_rotateHold is { } held)
        {
            if (Input.IsKeyPressed(Key.Ctrl) && UsesBrushShape && !IsStroking)
            {
                Cursor = held;
                return;
            }
            // Rotation done: put the mouse back on the brush so it doesn't jump to where the mouse wandered.
            _rotateHold = null;
            if (!cam.IsPositionBehind(held)) viewport.WarpMouse(cam.UnprojectPosition(held));
        }
        if (viewport.GuiGetHoveredControl() is not null) return;
        var mouse = viewport.GetMousePosition();
        if (Terrain.Raycast(cam.ProjectRayOrigin(mouse), cam.ProjectRayNormal(mouse), out var hit))
            Cursor = hit;
    }

    /// <summary>Follow mode: turns the brush toward the direction the cursor moves, once it has moved far enough.</summary>
    private void FollowCursor()
    {
        if (_rotationMode != BrushRotationMode.Follow || !UsesBrushShape || Cursor is not { } c)
        {
            _followFrom = null;
            return;
        }
        var p = new Vector2(c.X, c.Z);
        if (_followFrom is not { } from) { _followFrom = p; return; }
        var d = p - from;
        if (d.Length() < _radius * 0.25f) return;
        _followFrom = p;
        // The mask's +X axis points along world (cos a, -sin a); see Brush.Weight.
        float target = Mathf.RadToDeg(Mathf.Atan2(-d.Y, d.X));
        float diff = Mathf.PosMod(target - _brushAngle + 180f, 360f) - 180f;
        BrushAngle = _brushAngle + diff * 0.6f;
    }

    private void BeginStroke(MouseButton button, float sign = 1f)
    {
        if (Terrain?.Map is not { } map || Cursor is not { } hit) return;
        CommitGenerated();
        if (_rotationMode == BrushRotationMode.Random && UsesBrushShape)
            BrushAngle = (float)_rng.NextDouble() * 360f;
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
        History.EndStroke();
        Terrain?.RefreshHeightRange();
    }

    private void ApplyTick(Vector3 hit, float dt)
    {
        if (Terrain?.Map is not { } map) return;
        var c = ToLocal2(hit);
        var brush = CurrentBrush;
        if (_tool == TerrainTool.Paint)
        {
            if (Terrain.Splat is not { } splat) return;
            History.Touch(splat.CircleRect(c.X, c.Y, brush.Radius));
            var painted = _strokeSign > 0f
                ? PaintOps.Paint(splat, c, brush, _paintLayer, dt)
                : PaintOps.Erase(splat, c, brush, dt);
            Terrain.MarkSplatDirty(painted);
            return;
        }
        float oy = Terrain.GlobalPosition.Y;
        // Every sculpt op stays inside the brush's bounding square.
        History.Touch(map.CircleRect(c.X, c.Y, brush.Radius));
        var rect = _tool switch
        {
            TerrainTool.Shift => SculptOps.Shift(map, c, brush, _strokeSign, dt, _maxSlope),
            TerrainTool.Level => SculptOps.Level(map, c, brush, _strokeLevelTarget - oy, dt),
            TerrainTool.Smooth => SculptOps.Smooth(map, c, brush, dt),
            TerrainTool.Slope when _slopeAnchor is { } a =>
                SculptOps.Slope(map, c, brush, ToLocal2(a), a.Y - oy, ToLocal2(_slopeEnd), _slopeEnd.Y - oy, dt),
            _ => VertexRect.Empty,
        };
        Terrain.MarkDirty(rect);
    }

    public void Undo() => ApplyHistory(History.Undo);
    public void Redo() => ApplyHistory(History.Redo);

    // --- Generator ---

    /// <summary>Heights before the current run of generator updates; the whole run becomes one undo step.</summary>
    private float[]? _generatedBefore;

    /// <summary>
    /// Puts generated heights on the terrain. Live updates while the generator panel is open are merged into one undo
    /// step, recorded by <see cref="CommitGenerated"/> (on panel close, undo/redo, or the next stroke).
    /// </summary>
    public void ApplyGenerated(HeightMap heights, GenSettings settings)
    {
        if (Terrain?.Map is not { } map) return;
        EndStroke();
        bool sameSize = heights.Width == map.Width && heights.Depth == map.Depth;
        if (sameSize) _generatedBefore ??= map.Snapshot();
        else _generatedBefore = null;
        Terrain.ReplaceHeights(heights, settings);
        if (!sameSize) _map = null; // picked up (and history cleared) next frame
    }

    /// <summary>Turns the pending generator updates into one undo step.</summary>
    public void CommitGenerated()
    {
        if (_generatedBefore is null || Terrain?.Map is not { } map) return;
        History.PushHeights(_generatedBefore, map);
        _generatedBefore = null;
        Changed();
    }

    /// <summary>
    /// Puts eroded heights (same size as the map) on the terrain as one undo step. Returns false if the map was
    /// replaced meanwhile.
    /// </summary>
    public bool ApplyEroded(HeightMap heights)
    {
        if (Terrain?.Map is not { } map || heights.Width != map.Width || heights.Depth != map.Depth) return false;
        CommitGenerated();
        EndStroke();
        var before = map.Snapshot();
        Terrain.ReplaceHeights(heights, Terrain.Settings);
        History.PushHeights(before, map);
        Changed();
        return true;
    }

    /// <summary>
    /// Puts a Lake source in every hollow that lacks one (<see cref="Terrain.AddLakeSources"/>) as one undo step: undo
    /// removes them and drains their lakes. <paramref name="done"/> gets how many were added.
    /// </summary>
    public void AddLakeSources(Action<int>? done = null)
    {
        if (Terrain is not { } terrain) return;
        terrain.AddLakeSources(added =>
        {
            done?.Invoke(added.Added.Length);
            if (added.Added.Length == 0 || terrain.Water is not { } sim) return;
            History.PushAction(
                () =>
                {
                    if (terrain.Water != sim) return;
                    sim.SetSources(added.Before);
                    foreach (var s in added.Added) sim.DrainSource(s);
                    Changed();
                },
                () =>
                {
                    if (terrain.Water != sim) return;
                    sim.SetSources(added.After);
                    if (added.Levels is { } levels) sim.RaiseTo(levels);
                    Changed();
                });
            Changed();
        });
    }

    private void ApplyHistory(Func<HeightMap, SplatMap, UndoChange> op)
    {
        CommitGenerated();
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

        // Textured brushes on a levelled pad: a ridged stamp turned 45° and a terraced one, raised in place.
        BrushRadius = 180f;
        var pad = c + new Vector2(200, -230);
        LevelTarget = Terrain.GetHeight(pad.X, pad.Y);
        DemoStroke(TerrainTool.Level, pad + new Vector2(-120, 0), pad + new Vector2(120, 0), 300);
        LevelTarget = null;
        BrushRadius = 110f;
        BrushIndex = Array.FindIndex(BrushLibrary.All, e => e.Id == "ridged");
        BrushAngle = 45f;
        DemoStroke(TerrainTool.Shift, pad + new Vector2(-130, 0), pad + new Vector2(-130, 0), 90);
        BrushIndex = Array.FindIndex(BrushLibrary.All, e => e.Id == "terrace");
        BrushAngle = 0f;
        DemoStroke(TerrainTool.Shift, pad + new Vector2(130, 0), pad + new Vector2(130, 0), 90);
        BrushIndex = 0;

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
        SlopeAnchor = new Vector3(a.X, Terrain.GetHeight(a.X, a.Y), a.Y);

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
        PaintLayer = DemoMaterial("dirt", 3);
        DemoStroke(TerrainTool.Paint, c + new Vector2(-250, -120), c + new Vector2(250, 60), 240);
        BrushRadius = 60f;
        PaintLayer = DemoMaterial("sand", 5);
        DemoStroke(TerrainTool.Paint, c + new Vector2(-120, 120), c + new Vector2(-60, 140), 90);
        PaintLayer = DemoMaterial("gravel", 4);
        DemoStroke(TerrainTool.Paint, c + new Vector2(150, 150), c + new Vector2(150, 150), 60);
        BrushRadius = 30f;
        DemoStroke(TerrainTool.Paint, c + new Vector2(150, 150), c + new Vector2(150, 150), 40, -1f, MouseButton.Right);

        // Textured brushes: a splatter of gravel, and sand streaks turned 30°.
        BrushRadius = 50f;
        PaintLayer = DemoMaterial("gravel", 4);
        BrushIndex = Array.FindIndex(BrushLibrary.All, e => e.Id == "splatter");
        DemoStroke(TerrainTool.Paint, c + new Vector2(-40, 20), c + new Vector2(-40, 20), 40);
        PaintLayer = DemoMaterial("sand", 5);
        BrushIndex = Array.FindIndex(BrushLibrary.All, e => e.Id == "streaks");
        BrushAngle = 30f;
        DemoStroke(TerrainTool.Paint, c + new Vector2(80, 30), c + new Vector2(80, 30), 40);

        // Undo/redo self-check on an extra stroke.
        var splat = Terrain.Splat;
        var before = splat.Snapshot();
        PaintLayer = DemoMaterial("snow", 7);
        DemoStroke(TerrainTool.Paint, c, c + new Vector2(80, 0), 30);
        var after = splat.Snapshot();
        Undo();
        bool undoOk = splat.Snapshot().AsSpan().SequenceEqual(before);
        Redo();
        bool redoOk = splat.Snapshot().AsSpan().SequenceEqual(after);
        Undo();
        GD.Print($"Demo paint: undo {(undoOk ? "ok" : "FAILED")}, redo {(redoOk ? "ok" : "FAILED")}");

        PaintLayer = DemoMaterial("dirt", 3);
        BrushRadius = 40f;
        BrushAngle = 60f;
        var cursor = c + new Vector2(0, -40);
        ForcedCursor = new Vector3(cursor.X, Terrain.GetHeight(cursor.X, cursor.Y), cursor.Y);
    }
}
