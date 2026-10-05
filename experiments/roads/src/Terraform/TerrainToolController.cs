using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.Splines.Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Sculpt;
using CitySim.UI;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Terraform;

public enum TerrainToolKind { None, Shift, Level, Smooth, Slope, Channel, Paint, Erase }

/// <summary>Which way Shift moves the ground (Alt flips it for one stroke).</summary>
public enum ShiftMode { Raise, Lower }

/// <summary>How the Channel tool sets the height its cross-section hangs from: the ground along the drag, or a straight
/// grade between two clicked points.</summary>
public enum ChannelMode { FollowGround, Graded }

/// <summary>
/// The terrain tools, ported from the terrain experiment's (round brush only). Works while the Terrain tray is open with
/// a <see cref="TerrainTool"/> picked.
///
/// Inputs work like the road tools': LMB does the tool's thing, RMB backs out and never edits (cancels the stroke, puts
/// it back; else clears the picked point), Esc the same and then goes on to the HUD (unpicks, closes). Alt+LMB is the
/// tool's second action.
/// Shift: LMB raises or lowers (Raise · Lower, 1–2), Alt+LMB the other way. Level: LMB levels to the picked height (none:
/// the most common height under the brush), Alt+LMB picks it, RMB back to auto. Smooth: LMB. Slope: LMB sets the start,
/// then LMB-drag ramps from it to where you pressed; Alt+LMB a new start. Channel: LMB-drag cuts a V, U, flat-bed or box
/// cross-section along the ground; Graded: LMB sets A, then B, then cuts A → B (Enter too), RMB steps back. Paint (one card
/// per paintable theme material): LMB paints. Erase: LMB takes the paint off.
/// Brush: [ ] or Shift+wheel size, Alt+wheel strength. C contours. Ctrl+Z undo, Ctrl+Shift+Z / Ctrl+Y redo.
///
/// Each stroke is one terrain edit. Roads don't move with the ground: the <see cref="SplineNetwork"/> shapes it back round
/// them as its own step (<see cref="SplineNetwork.GroundShapedBack"/>), and undo takes that step back with the stroke.
/// Undo here only reaches strokes made since the last road change (and the road undo history ends at a stroke), since
/// both share the terrain's one history.
/// </summary>
public partial class TerrainToolController : Node
{
    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public GameHud? Hud { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    public const float MinRadius = 4f, MaxRadius = 300f;
    /// <summary>Contour spacing, metres.</summary>
    public const float ContourInterval = 2f;
    private const int MaxUndo = 50;
    /// <summary>Edits run at a fixed rate so a stroke's effect doesn't depend on frame rate.</summary>
    private const float Tick = 1f / 60f;
    private const int MaxTicksPerFrame = 4;
    /// <summary>Channel cut rate per second, times the intensity (at 50 %: ~2/3 of the way in 0.5 s).</summary>
    private const float ChannelRate = 4f;

    private float _radius = 40f;
    private float _strength = 0.5f;
    private float _maxSlope = SculptOps.DefaultMaxSlopeDegrees;
    private float? _levelTarget;
    private Vector3? _slopeAnchor;
    private bool _showContours;
    private ChannelMode _channelMode;
    private ChannelProfile _channel = new(ChannelShape.Rounded, 30f, 4f);
    private bool _channelFill;
    private float _channelIntensity = 1f;
    private Vector3? _gradeA, _gradeB;
    private ShiftMode _shiftMode;
    private int _paintLayer = -1;
    private Control? _tags;
    private string? _tag, _tagKey;

    private TerrainEdit? _edit;
    private bool _touched;
    private MouseButton _strokeButton = MouseButton.None;
    private float _strokeSign;
    private float _strokeLevelTarget;
    private Vector3 _slopeEnd;
    private float _tickAccum;
    private ChannelPoint? _channelLast;
    private bool _overlayShown;

    /// <summary>Our strokes on the terrain's history, newest last: how many terrain steps each is (the stroke, plus the
    /// roads' shaping back after it).</summary>
    private readonly List<int> _undo = new();
    private readonly Stack<int> _redo = new();
    private bool _awaitShapeBack;

    /// <summary>Raised when the tool, a setting, the level target or a start point changes.</summary>
    public event Action? StateChanged;

    public TerrainToolKind Kind => Hud is { TerrainOpen: true, Tray.Picked: TerrainTool t } ? KindOf(t.Tool) : TerrainToolKind.None;

    /// <summary>The Paint tab: one card per paintable material of the map's theme.</summary>
    public const string PaintTab = "terrain_paint";

    /// <summary>Terrain point under the mouse, if any.</summary>
    public Vector3? Cursor { get; private set; }
    /// <summary>When set, used instead of the mouse position (for scripted screenshots).</summary>
    public Vector3? ForcedCursor { get; set; }
    public bool IsStroking => _edit is not null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public float BrushRadius { get => _radius; set { _radius = Mathf.Clamp(value, MinRadius, MaxRadius); Changed(); } }
    public float BrushStrength { get => _strength; set { _strength = Mathf.Clamp(value, 0.05f, 1f); Changed(); } }
    /// <summary>Steepest angle Shift builds, degrees.</summary>
    public float MaxSlopeDegrees { get => _maxSlope; set { _maxSlope = Mathf.Clamp(value, 10f, 80f); Changed(); } }
    /// <summary>Level's height (world Y); null = the most common height under the brush.</summary>
    public float? LevelTarget { get => _levelTarget; set { _levelTarget = value; Changed(); } }
    public Vector3? SlopeAnchor { get => _slopeAnchor; set { _slopeAnchor = value; Changed(); } }
    public bool ShowContours { get => _showContours; set { _showContours = value; Changed(); } }
    public ChannelMode ChannelMode { get => _channelMode; set { _channelMode = value; _gradeA = _gradeB = null; Changed(); } }
    public ChannelProfile Channel
    {
        get => _channel;
        set { _channel = value with { Width = Mathf.Clamp(value.Width, 4f, 300f), Depth = Mathf.Clamp(value.Depth, 0.5f, 40f) }; Changed(); }
    }
    /// <summary>Graded: also build walls up to the grade where the ground dips below it.</summary>
    public bool ChannelFill { get => _channelFill; set { _channelFill = value; Changed(); } }
    /// <summary>0.1 to 1: at 1 the full profile at once; lower digs a little each tick.</summary>
    public float ChannelIntensity
    {
        get => _channelIntensity;
        set { _channelIntensity = Mathf.Clamp(MathF.Round(value * 20f) / 20f, 0.1f, 1f); Changed(); }
    }
    public ShiftMode ShiftMode { get => _shiftMode; set { _shiftMode = value; Changed(); } }
    public Vector3? GradeA => _gradeA;
    public Vector3? GradeB => _gradeB;

    /// <summary>Graded Channel from A to B (or the cursor before B), measured on the current ground.</summary>
    public ChannelMeasure? GradeMeasure =>
        _gradeA is { } a && (_gradeB ?? Cursor) is { } b && Terrain?.Map is { } map
            ? ChannelOps.Measure(map, GradePoint(a), GradePoint(b), _channel)
            : null;

    private bool IsGraded => Kind == TerrainToolKind.Channel && _channelMode == ChannelMode.Graded;

    public static TerrainToolKind KindOf(string tool) => tool switch
    {
        TerrainTool.ShiftTool => TerrainToolKind.Shift,
        TerrainTool.LevelTool => TerrainToolKind.Level,
        TerrainTool.SmoothTool => TerrainToolKind.Smooth,
        TerrainTool.SlopeTool => TerrainToolKind.Slope,
        TerrainTool.ChannelTool => TerrainToolKind.Channel,
        TerrainTool.PaintTool => TerrainToolKind.Paint,
        TerrainTool.EraseTool => TerrainToolKind.Erase,
        _ => TerrainToolKind.None,
    };

    public override void _Ready()
    {
        if (Terrain is null || Hud is null) { GD.PushError("TerrainToolController needs a Terrain and a Hud"); return; }
        Hud.TerrainOptions.Bind(this);
        Hud.Tray.ItemPicked += _ => ToolChanged();
        Hud.CategoryOpened += _ => ToolChanged();
        var layer = new CanvasLayer { Name = "TerrainToolTags", Layer = 2 };
        AddChild(layer);
        _tags = new Control { Name = "TerrainTags", MouseFilter = Control.MouseFilterEnum.Ignore };
        _tags.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _tags.Draw += DrawTag;
        layer.AddChild(_tags);
        Terrain.ThemeChanged += MakePaintCards;
        MakePaintCards();
        Terrain.MapReplaced += () =>
        {
            MakePaintCards();
            EndStroke();
            _undo.Clear();
            _redo.Clear();
            _slopeAnchor = _gradeA = _gradeB = null;
            Changed();
        };
        if (Network is not null)
        {
            Network.GroundShapedBack += () =>
            {
                if (_awaitShapeBack && _undo.Count > 0) _undo[^1]++;
                _awaitShapeBack = false;
            };
            // A road change goes on top of the terrain's history, so our strokes are out of reach from here.
            Network.Changed += () =>
            {
                _undo.Clear();
                _redo.Clear();
                _awaitShapeBack = false;
            };
        }
    }

    /// <summary>The Paint tab's cards from the theme's paintable materials, its baked swatches as pictures.</summary>
    private void MakePaintCards()
    {
        if (Hud is null || Terrain?.Theme is not { } theme) return;
        var cards = theme.Materials.Select((m, i) => (m, i)).Where(p => p.m is { Paintable: true }).Select(p =>
        {
            string preview = theme.PreviewPath(p.m);
            return (Content.BuildItem)new TerrainPaint
            {
                Id = "paint_" + p.m.Id,
                DisplayName = p.m.Label,
                Description = $"Paint {p.m.Label.ToLowerInvariant()} on the ground.",
                Tool = TerrainTool.PaintTool,
                Material = p.m.Id,
                Order = 10 + p.i,
                Icon = ResourceLoader.Exists(preview) ? ResourceLoader.Load<Texture2D>(preview) : null,
                Glyph = UiTheme.Icon("brush"),
                Usage = "LMB hold  paint",
            };
        });
        Hud.Library.SetGenerated(PaintTab, cards);
    }

    /// <summary>A fresh pick starts without start points.</summary>
    private void ToolChanged()
    {
        _paintLayer = Hud?.Tray.Picked is TerrainPaint p && Terrain?.Theme is { } theme ? theme.IndexOf(p.Material) : -1;
        EndStroke();
        _slopeAnchor = _gradeA = _gradeB = null;
        Changed();
    }

    // Keys here (not in _UnhandledInput) so Esc reaches us before the HUD, which comes earlier in the tree.
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (Hud?.TerrainOpen == true && @event is InputEventKey { Pressed: true } key) HandleKey(key);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Hud?.TerrainOpen == true && @event is InputEventMouseButton mb && Kind != TerrainToolKind.None) HandleMouseButton(mb);
    }

    private void HandleKey(InputEventKey key)
    {
        var kind = Kind;
        bool handled = true;
        if (key.Keycode == Key.Escape) handled = StepBack(kind);
        else if (key.Keycode is Key.Key1 or Key.Key2 && kind == TerrainToolKind.Shift && !key.IsCommandOrControlPressed())
            ShiftMode = key.Keycode == Key.Key1 ? ShiftMode.Raise : ShiftMode.Lower;
        else if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            if (key.ShiftPressed) Redo(); else Undo();
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed()) Redo();
        else if (key.Keycode is Key.Bracketleft or Key.Bracketright && kind != TerrainToolKind.None)
        {
            float f = key.Keycode == Key.Bracketright ? 1.15f : 1f / 1.15f;
            if (kind == TerrainToolKind.Channel) Channel = _channel with { Width = _channel.Width * f };
            else BrushRadius *= f;
        }
        else if (key.Keycode is Key.Enter or Key.KpEnter && IsGraded && _gradeB.HasValue) CutGraded();
        else if (key.Keycode == Key.C && !key.IsCommandOrControlPressed() && !key.Echo && kind != TerrainToolKind.None)
            ShowContours = !ShowContours;
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        var kind = Kind;
        if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float dir = mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
            if (mb.ShiftPressed && kind == TerrainToolKind.Channel) Channel = _channel with { Width = _channel.Width * Mathf.Pow(1.1f, dir) };
            else if (mb.AltPressed && kind == TerrainToolKind.Channel) ChannelIntensity += 0.05f * dir;
            else if (mb.ShiftPressed) BrushRadius *= Mathf.Pow(1.1f, dir);
            else if (mb.AltPressed) BrushStrength += 0.05f * dir;
            else return; // the camera zooms
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
        GetViewport().SetInputAsHandled();
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed) StepBack(kind);
            return;
        }
        if (!mb.Pressed)
        {
            if (mb.ButtonIndex == _strokeButton) EndStroke();
            return;
        }
        if (IsStroking || Cursor is not { } hit) return;
        Press(kind, hit, mb.AltPressed);
    }

    /// <summary>LMB (<paramref name="alt"/>: Alt+LMB, the tool's second action) on the ground at <paramref name="hit"/>.</summary>
    private void Press(TerrainToolKind kind, Vector3 hit, bool alt = false)
    {
        const MouseButton button = MouseButton.Left;
        switch (kind)
        {
            case TerrainToolKind.Shift:
                BeginStroke(button, (_shiftMode == ShiftMode.Raise ? 1f : -1f) * (alt ? -1f : 1f));
                break;
            case TerrainToolKind.Level:
                if (alt) LevelTarget = hit.Y;
                else BeginStroke(button);
                break;
            case TerrainToolKind.Slope:
                if (alt || _slopeAnchor is null) SlopeAnchor = hit;
                else
                {
                    _slopeEnd = hit;
                    BeginStroke(button);
                }
                break;
            case TerrainToolKind.Channel when _channelMode == ChannelMode.Graded:
                if (_gradeA is null) _gradeA = hit;
                else if (_gradeB is null) _gradeB = hit;
                else CutGraded();
                Changed();
                break;
            case TerrainToolKind.Paint when _paintLayer < 0:
                break;
            case TerrainToolKind.Smooth or TerrainToolKind.Channel or TerrainToolKind.Paint or TerrainToolKind.Erase:
                BeginStroke(button);
                break;
        }
    }

    /// <summary>RMB or Esc: cancels the stroke (the ground goes back), else clears the last picked point or height. False
    /// when there was nothing to back out of (Esc then goes on to the HUD).</summary>
    private bool StepBack(TerrainToolKind kind)
    {
        if (IsStroking) { CancelStroke(); return true; }
        switch (kind)
        {
            case TerrainToolKind.Level when _levelTarget.HasValue: LevelTarget = null; return true;
            case TerrainToolKind.Slope when _slopeAnchor.HasValue: SlopeAnchor = null; return true;
            case TerrainToolKind.Channel: return GradeStepBack();
            default: return false;
        }
    }

    public override void _Process(double delta)
    {
        if (Terrain?.Map is null) return;
        var kind = Kind;
        UpdateCursor();

        // The release can land on a UI panel and never reach us.
        if (IsStroking && ForcedCursor is null && !Input.IsMouseButtonPressed(_strokeButton)) EndStroke();
        if (IsStroking && kind == TerrainToolKind.None) EndStroke();

        if (IsStroking && Cursor is { } hit)
        {
            _tickAccum = Mathf.Min(_tickAccum + (float)delta, Tick * MaxTicksPerFrame);
            while (_tickAccum >= Tick)
            {
                _tickAccum -= Tick;
                ApplyTick(kind, hit, Tick);
            }
        }
        UpdateOverlay(kind);
        UpdateTag(kind);
    }

    /// <summary>The tag by the mouse, like the road tools': what the next click does, with the picked point's numbers;
    /// during a stroke, that RMB cancels it.</summary>
    private void UpdateTag(TerrainToolKind kind)
    {
        (string? key, string? tag) = (null, null);
        if (IsStroking) (key, tag) = ("RMB", "Cancel");
        else if (Cursor is { } at)
        {
            switch (kind)
            {
                case TerrainToolKind.Level when _levelTarget is { } h:
                    (key, tag) = ("LMB", $"Level · {h:0.0} m");
                    break;
                case TerrainToolKind.Slope when _slopeAnchor is { } a:
                    (key, tag) = ("drag", $"Ramp · {Grade(a, at):+0.0;-0.0} %");
                    break;
                case TerrainToolKind.Slope:
                    (key, tag) = ("LMB", "Start");
                    break;
                case TerrainToolKind.Channel when IsGraded:
                    if (_gradeA is null) (key, tag) = ("LMB", "A");
                    else if (_gradeB is null) (key, tag) = ("LMB", $"B · {GradeMeasure?.GradePercent ?? 0f:+0.0;-0.0} %");
                    else (key, tag) = ("LMB · Enter", $"Cut · {GradeMeasure?.GradePercent ?? 0f:+0.0;-0.0} %");
                    break;
            }
        }
        if (tag == _tag && key == _tagKey) { if (tag is not null) _tags?.QueueRedraw(); return; }
        (_tag, _tagKey) = (tag, key);
        _tags?.QueueRedraw();
    }

    private static float Grade(Vector3 a, Vector3 b)
    {
        float run = new Vector2(b.X - a.X, b.Z - a.Z).Length();
        return run < 0.01f ? 0f : (b.Y - a.Y) / run * 100f;
    }

    private void DrawTag()
    {
        if (_tag is null || _tags is null) return;
        var mouse = _tags.GetLocalMousePosition() + new Vector2(18, 14);
        var font = ThemeDB.FallbackFont;
        float keyWidth = _tagKey is null ? 0 : KeyGlyphs.Width(_tagKey, font, 13, 18) + 6;
        float w = font.GetStringSize(_tag, HorizontalAlignment.Left, -1, 13).X + keyWidth + 16;
        _tags.DrawRect(new Rect2(mouse, new Vector2(w, 26)), new Color("#1C2629", 0.92f));
        if (_tagKey is not null) KeyGlyphs.Draw(_tags, mouse + new Vector2(8, 19), _tagKey, font, 13, 18, SplineOverlay.Accent);
        _tags.DrawString(font, mouse + new Vector2(8 + keyWidth, 19), _tag, HorizontalAlignment.Left, -1, 13, new Color("#F4F1E6"));
    }

    private void UpdateOverlay(TerrainToolKind kind)
    {
        if (Terrain is null) return;
        if (kind == TerrainToolKind.None)
        {
            if (!_overlayShown) return;
            _overlayShown = false;
            Terrain.SetBrush(Vector3.Zero, 0f, false);
            Terrain.SetAnchor(null);
            Terrain.SetContours(false, ContourInterval);
            return;
        }
        _overlayShown = true;
        float ring = kind == TerrainToolKind.Channel ? _channel.Width * 0.5f : _radius;
        // Graded Channel: once B is picked, the ring (and the corridor to it) stays at B.
        var at = IsGraded && _gradeB is { } b ? b : Cursor;
        Terrain.SetBrush(at ?? Vector3.Zero, ring, at.HasValue);
        if (IsGraded) Terrain.SetAnchor(_gradeA, _channel.Width * 0.5f);
        else Terrain.SetAnchor(kind == TerrainToolKind.Slope ? _slopeAnchor : null);
        Terrain.SetContours(_showContours, ContourInterval);
    }

    private void UpdateCursor()
    {
        if (ForcedCursor.HasValue) { Cursor = ForcedCursor; return; }
        Cursor = null;
        var viewport = GetViewport();
        if (Terrain is null || CityCamera?.Camera is not { } cam || viewport.GuiGetHoveredControl() is not null) return;
        var mouse = viewport.GetMousePosition();
        // During a Channel stroke the ray hits the ground from before it: on the carved ground a tilted ray passes the lip
        // and lands on the far wall, which then gets cut, so the cut would creep away from the camera.
        Func<float, float, float>? heightAt = null;
        if (IsStroking && Kind == TerrainToolKind.Channel && Terrain.Map is { } map)
        {
            var o = Terrain.GlobalPosition;
            heightAt = (x, z) => OriginalHeight(map, x - o.X, z - o.Z) + o.Y;
        }
        if (Terrain.Raycast(cam.ProjectRayOrigin(mouse), cam.ProjectRayNormal(mouse), out var hit, heightAt: heightAt)) Cursor = hit;
    }

    private Brush CurrentBrush => new(_radius, _strength);

    private void BeginStroke(MouseButton button, float sign = 1f)
    {
        if (Terrain?.Map is not { } map || Cursor is not { } hit || Terrain.History.InStroke) return;
        _edit = Terrain.BeginEdit(paint: Kind is TerrainToolKind.Paint or TerrainToolKind.Erase);
        _touched = false;
        _strokeButton = button;
        _strokeSign = sign;
        _tickAccum = Tick; // the first tick right away
        _channelLast = null;
        if (Kind == TerrainToolKind.Level)
            _strokeLevelTarget = _levelTarget ?? SculptOps.DominantHeight(map, ToLocal(hit), CurrentBrush) + Terrain.GlobalPosition.Y;
    }

    private void EndStroke()
    {
        if (_edit is not { } edit) return;
        _edit = null;
        _strokeButton = MouseButton.None;
        edit.Commit();
        if (_touched) Recorded();
    }

    /// <summary>Drops the open stroke and puts the ground back as it was.</summary>
    private void CancelStroke()
    {
        if (_edit is not { } edit) return;
        _edit = null;
        _strokeButton = MouseButton.None;
        edit.Cancel();
        Changed();
    }

    /// <summary>One terrain step of ours went on the history.</summary>
    private void Recorded()
    {
        _undo.Add(1);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _awaitShapeBack = true;
        Changed();
    }

    private void Touch(VertexRect rect)
    {
        _edit!.Touch(rect);
        _touched = true;
    }

    private void ApplyTick(TerrainToolKind kind, Vector3 hit, float dt)
    {
        if (Terrain?.Map is not { } map || _edit is null) return;
        var c = ToLocal(hit);
        if (kind == TerrainToolKind.Channel)
        {
            CarveChannelTo(map, c, dt);
            return;
        }
        var brush = CurrentBrush;
        if (kind is TerrainToolKind.Paint or TerrainToolKind.Erase)
        {
            Touch(map.CircleRect(c.X, c.Y, brush.Radius));
            _edit.Changed(kind == TerrainToolKind.Paint
                ? PaintOps.Paint(_edit.Splat, c, brush, _paintLayer, dt)
                : PaintOps.Erase(_edit.Splat, c, brush, dt));
            return;
        }
        float oy = Terrain.GlobalPosition.Y;
        Touch(map.CircleRect(c.X, c.Y, brush.Radius)); // every sculpt op stays inside the brush's square
        var rect = kind switch
        {
            TerrainToolKind.Shift => SculptOps.Shift(map, c, brush, _strokeSign, dt, _maxSlope),
            TerrainToolKind.Level => SculptOps.Level(map, c, brush, _strokeLevelTarget - oy, dt),
            TerrainToolKind.Smooth => SculptOps.Smooth(map, c, brush, dt),
            TerrainToolKind.Slope when _slopeAnchor is { } a =>
                SculptOps.Slope(map, c, brush, ToLocal(a), a.Y - oy, ToLocal(_slopeEnd), _slopeEnd.Y - oy, dt),
            _ => VertexRect.Empty,
        };
        _edit.Changed(rect);
    }

    /// <summary>Extends the Channel stroke's path to <paramref name="p"/> and cuts the new segment. A point closer than an
    /// eighth of the width to the last isn't added (it steadies the direction), but below full intensity the segment to it
    /// still cuts a little more each tick.</summary>
    private void CarveChannelTo(HeightMap map, NumVector2 p, float dt)
    {
        float amount = _channelIntensity >= 0.999f ? 1f : 1f - MathF.Exp(-ChannelRate * _channelIntensity * dt);
        bool add = _channelLast is not { } last || NumVector2.Distance(last.Position, p) >= MathF.Max(map.CellSize, _channel.Width * 0.125f);
        if (!add && amount >= 1f) return;
        var point = ChannelPointAt(map, p);
        var from = _channelLast ?? point;
        Touch(ChannelOps.Bounds(map, from, point, _channel, ChannelOps.DefaultBankDegrees));
        _edit!.Changed(ChannelOps.CarveSegment(map, from, point, _channel, ChannelOps.DefaultBankDegrees, amount));
        if (add) _channelLast = point;
    }

    private ChannelPoint ChannelPointAt(HeightMap map, NumVector2 p)
    {
        // The ground from before this stroke (so the cut doesn't feed on itself), averaged across the channel.
        float q = _channel.Width * 0.25f;
        float sample = (OriginalHeight(map, p.X, p.Y) + OriginalHeight(map, p.X - q, p.Y) + OriginalHeight(map, p.X + q, p.Y)
                        + OriginalHeight(map, p.X, p.Y - q) + OriginalHeight(map, p.X, p.Y + q)) / 5f;
        if (_channelLast is not { } last) return new ChannelPoint(p, sample);
        // Smoothed along the path over about a channel width, and never rising, so the bed runs downhill.
        float seg = NumVector2.Distance(last.Position, p);
        float reference = last.Reference + (sample - last.Reference) * (seg / (seg + _channel.Width));
        return new ChannelPoint(p, MathF.Min(reference, last.Reference));
    }

    /// <summary>Bilinear height at local (x, z) as it was when the stroke began.</summary>
    private float OriginalHeight(HeightMap map, float x, float z)
    {
        if (_edit is not { } edit) return map.SampleHeight(x, z);
        float fx = Math.Clamp(x / map.CellSize, 0f, map.Width - 1), fz = Math.Clamp(z / map.CellSize, 0f, map.Depth - 1);
        int x0 = Math.Min((int)fx, map.Width - 2), z0 = Math.Min((int)fz, map.Depth - 2);
        float tx = fx - x0, tz = fz - z0;
        float h00 = edit.Original(x0, z0), h10 = edit.Original(x0 + 1, z0);
        float h01 = edit.Original(x0, z0 + 1), h11 = edit.Original(x0 + 1, z0 + 1);
        return (h00 + (h10 - h00) * tx) * (1f - tz) + (h01 + (h11 - h01) * tx) * tz;
    }

    private ChannelPoint GradePoint(Vector3 world) => new(ToLocal(world), world.Y - (Terrain?.GlobalPosition.Y ?? 0f));

    /// <summary>Graded Channel: clears B, or A without B. False if neither was set.</summary>
    private bool GradeStepBack()
    {
        if (!IsGraded) return false;
        if (_gradeB.HasValue) _gradeB = null;
        else if (_gradeA.HasValue) _gradeA = null;
        else return false;
        Changed();
        return true;
    }

    /// <summary>Cuts the graded channel A → B as one step, then carries on from B.</summary>
    private void CutGraded()
    {
        if (Terrain?.Map is not { } map || _gradeA is not { } a || _gradeB is not { } b || Terrain.History.InStroke) return;
        var pa = GradePoint(a);
        var pb = GradePoint(b);
        _edit = Terrain.BeginEdit();
        _touched = false;
        Touch(ChannelOps.Bounds(map, pa, pb, _channel, ChannelOps.DefaultBankDegrees, _channelFill));
        _edit.Changed(ChannelOps.CarveSegment(map, pa, pb, _channel, ChannelOps.DefaultBankDegrees, 1f, _channelFill));
        EndStroke();
        _gradeA = b;
        _gradeB = null;
        Changed();
    }

    /// <summary>Undoes our last stroke, with the roads' shaping back after it.</summary>
    public void Undo()
    {
        if (IsStroking || Terrain is null || _undo.Count == 0) return;
        int steps = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int i = 0; i < steps; i++) Terrain.Undo();
        _redo.Push(steps);
        _awaitShapeBack = false;
        Changed();
    }

    public void Redo()
    {
        if (IsStroking || Terrain is null || _redo.Count == 0) return;
        int steps = _redo.Pop();
        for (int i = 0; i < steps; i++) Terrain.Redo();
        _undo.Add(steps);
        _awaitShapeBack = false;
        Changed();
    }

    private NumVector2 ToLocal(Vector3 world)
    {
        var o = Terrain?.GlobalPosition ?? Vector3.Zero;
        return new NumVector2(world.X - o.X, world.Z - o.Z);
    }

    private void Changed() => StateChanged?.Invoke();

    /// <summary>One stroke of <paramref name="ticks"/> ticks from <paramref name="from"/> to <paramref name="to"/> (map
    /// metres) with the picked tool, as a drag would make it (for scripts). <paramref name="button"/> Right is an RMB click;
    /// <paramref name="cancel"/> presses RMB before letting go.</summary>
    public void StrokeForTest(Vector2 from, Vector2 to, int ticks, MouseButton button = MouseButton.Left, bool alt = false, bool cancel = false)
    {
        if (Terrain?.Map is null) return;
        var kind = Kind;
        Vector3 At(Vector2 p) => Terrain.MapToWorld(p.X, p.Y, Terrain.GetHeightAtMap(p.X, p.Y) - Terrain.GlobalPosition.Y);
        ForcedCursor = At(from);
        UpdateCursor();
        if (button == MouseButton.Right) StepBack(kind);
        else Press(kind, At(from), alt);
        for (int i = 0; i < ticks && IsStroking; i++)
            ApplyTick(kind, At(from.Lerp(to, ticks > 1 ? i / (float)(ticks - 1) : 0f)), Tick);
        if (cancel) StepBack(kind); // RMB mid-stroke
        EndStroke();
        ForcedCursor = null;
    }
}
