using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;

namespace CitySim.Splines.Godot;

/// <summary>Modifier keys a scripted frame can hold down (<see cref="SplineDrawTool.ForcedModifiers"/>).</summary>
[Flags]
public enum DrawModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Space = 8 }

/// <summary>
/// The Draw-mode tool (DESIGN.md → Draw tool, mode 1 of 4; ROADMAP.md S2-S3): click to place PIs on the terrain,
/// with auto-rounded corners, Alt hard corner, Shift+wheel/<c>[</c>/<c>]</c> radius of the live corner, RMB/Ctrl+Z
/// undo, Esc/RMB-empty cancel, double-click/Enter finish, and snapping to nodes, edges, angles and guides (S3, via
/// <see cref="SnapEngine"/>), with the storyboard's feedback drawn by <see cref="SplineOverlay"/>. No-ops unless
/// <see cref="SplinesTestbed.Mode"/> is <see cref="DrawMode.Draw"/>. The graph and its undo (S4) and Anarchy are
/// not implemented yet — radii just clamp.
/// </summary>
public partial class SplineDrawTool : Node
{
    private const float RadiusWheelFactor = 1.1f;
    private const float RadiusKeyFactor = 1.15f;
    private const float CatchPixels = 8f;
    private const double FlashSeconds = 3.0;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }

    private readonly DrawSession _session = new();
    private readonly List<(SplineProfile Profile, Alignment Alignment)> _built = new();
    private IGround? _ground;
    private RibbonRenderer? _renderer;
    private SplineOverlay? _overlay;
    private SplineProfile? _sessionProfile;
    private SnapResult? _snap;
    private NumVector2? _startHeading;
    private double _hardHintUntil;
    private (NumVector2 At, string Text, double Until)? _flash;

    /// <summary>The current ground hit, world space. Null off the terrain or over UI.</summary>
    public NumVector3? Cursor { get; private set; }
    /// <summary>Scripted-demo override: a plan-space (map metres) position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get; set; }
    /// <summary>Scripted-demo override: modifier keys treated as held (added to the real ones).</summary>
    public DrawModifiers ForcedModifiers { get; set; }
    public int BuiltCount => _built.Count;

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineDrawTool needs a Terrain"); return; }
        _ground = new TerrainGround(Terrain);
        _renderer = new RibbonRenderer(Terrain, _ground);
        var layer = new CanvasLayer { Name = "SplineDrawHud" };
        AddChild(layer);
        _overlay = new SplineOverlay { Project = ProjectPlan };
        layer.AddChild(_overlay);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Testbed?.Mode != DrawMode.Draw) return;
        if (@event is InputEventKey { Pressed: true } key) HandleKey(key);
        else if (@event is InputEventMouseButton mb) HandleMouseButton(mb);
    }

    private void HandleKey(InputEventKey key)
    {
        bool handled = true;
        if (key.Keycode == Key.Z && key.IsCommandOrControlPressed())
        {
            if (key.ShiftPressed) _session.Redo(); else _session.Undo();
        }
        else if (key.Keycode == Key.Y && key.IsCommandOrControlPressed()) _session.Redo();
        else if (key.Keycode == Key.Bracketleft) AdjustRadius(1f / RadiusKeyFactor);
        else if (key.Keycode == Key.Bracketright) AdjustRadius(RadiusKeyFactor);
        else if (key.Keycode is Key.Enter or Key.KpEnter) Finish();
        else if (key.Keycode == Key.Escape) CancelSession();
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        if (mb.Pressed && mb.ShiftPressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            AdjustRadius(MathF.Pow(RadiusWheelFactor, mb.ButtonIndex == MouseButton.WheelUp ? 1f : -1f));
            GetViewport().SetInputAsHandled();
            return;
        }
        if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right) || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Right)
        {
            GetViewport().SetInputAsHandled();
            RemoveLastOrCancel();
            return;
        }
        if (mb.DoubleClick)
        {
            GetViewport().SetInputAsHandled();
            Finish();
            return;
        }
        if (Cursor is { } hit)
        {
            GetViewport().SetInputAsHandled();
            Place(_snap?.Position ?? PlanOf(hit), hard: mb.AltPressed);
        }
    }

    public override void _Process(double delta)
    {
        if (Testbed is null || _ground is null || _renderer is null || _overlay is null) return;
        if (!_session.IsEmpty && Testbed.Profile != _sessionProfile)
        {
            GD.Print("Splines: profile changed mid-draw, cancelling");
            CancelSession();
        }

        UpdateCursor();

        if (Testbed.Mode != DrawMode.Draw || Testbed.Profile is not { } profile || Cursor is not { } cursor)
        {
            _renderer.SetPreview(null, 0);
            _overlay.Show(null);
            _snap = null;
            return;
        }

        var rules = profile.ToRules();
        var mods = Modifiers();
        _snap = SnapEngine.Evaluate(BuildSnapQuery(PlanOf(cursor), cursor, rules, mods));

        Alignment? preview = null;
        if (!_session.IsEmpty)
        {
            preview = _session.BuildPreview(_snap.Position);
            _renderer.SetPreview(preview, profile.Width);
        }
        else _renderer.SetPreview(null, 0);

        double now = Time.GetTicksMsec() / 1000.0;
        if (_flash is { } fl && now > fl.Until) _flash = null;
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl) && !mods.HasFlag(DrawModifiers.Space);
        _overlay.Show(new OverlayFrame
        {
            SessionPis = _session.Pis,
            Preview = preview,
            Snap = _snap,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            Rules = rules,
            BuiltEnds = _built.SelectMany(b => new[] { b.Alignment.Pis[0].Position, b.Alignment.Pis[^1].Position }).ToList(),
            Mouse = MouseScreen(),
            CtrlStepDegrees = ctrl && !_session.IsEmpty ? (mods.HasFlag(DrawModifiers.Shift) ? 5f : 15f) : 0f,
            HardRefused = now < _hardHintUntil,
            Flash = _flash is { } f ? (f.At, f.Text) : null,
        });
    }

    private DrawModifiers Modifiers()
    {
        var m = ForcedModifiers;
        if (Input.IsKeyPressed(Key.Ctrl)) m |= DrawModifiers.Ctrl;
        if (Input.IsKeyPressed(Key.Shift)) m |= DrawModifiers.Shift;
        if (Input.IsKeyPressed(Key.Alt)) m |= DrawModifiers.Alt;
        if (Input.IsKeyPressed(Key.Space)) m |= DrawModifiers.Space;
        return m;
    }

    /// <summary>Every currently-held-input flag and existing alignment <see cref="SnapEngine"/> needs, plus the
    /// screen-pixel catch distance converted to plan units for this frame's cursor depth.</summary>
    private SnapQuery BuildSnapQuery(NumVector2 rawPlan, NumVector3 worldCursor, ProfileRules rules, DrawModifiers mods)
    {
        bool ctrl = mods.HasFlag(DrawModifiers.Ctrl);
        return new SnapQuery
        {
            Cursor = rawPlan,
            SessionPis = _session.Pis,
            StartHeading = _session.IsEmpty ? null : _startHeading,
            Candidates = _built.Select(b => new SnapCandidate(b.Alignment, b.Profile.Width)).ToList(),
            Rules = rules,
            EnabledProviders = Testbed?.EnabledSnaps ?? SnapProviders.All,
            CatchDistance = PixelsToPlanUnits(CatchPixels, worldCursor),
            CtrlSteps = ctrl,
            FineSteps = ctrl && mods.HasFlag(DrawModifiers.Shift),
            Disabled = mods.HasFlag(DrawModifiers.Space),
        };
    }

    /// <summary>Converts a screen-pixel distance to plan units at <paramref name="worldHit"/>'s depth, so the catch
    /// distance feels the same at every zoom (DESIGN.md → Snapping and guides).</summary>
    private float PixelsToPlanUnits(float pixels, NumVector3 worldHit)
    {
        var world = new Vector3(worldHit.X, worldHit.Y, worldHit.Z);
        if (CityCamera?.Camera is not { } cam || cam.IsPositionBehind(world)) return pixels;
        var origin = cam.UnprojectPosition(world);
        var offset = cam.UnprojectPosition(world + new Vector3(1f, 0, 0));
        float pxPerMeter = origin.DistanceTo(offset);
        return pxPerMeter > 1e-3f ? pixels / pxPerMeter : pixels;
    }

    /// <summary>A plan point draped on the ground, on screen (null when behind the camera) — for the overlay.</summary>
    private Vector2? ProjectPlan(NumVector2 plan)
    {
        if (Terrain is null || _ground is null || CityCamera?.Camera is not { } cam) return null;
        var o = Terrain.GlobalPosition;
        var world = new Vector3(o.X + plan.X, _ground.GetHeight(plan), o.Z + plan.Y);
        return cam.IsPositionBehind(world) ? null : cam.UnprojectPosition(world);
    }

    /// <summary>The mouse on screen, or where the forced cursor projects to in a scripted frame.</summary>
    private Vector2 MouseScreen() =>
        ForcedPlanCursor is { } forced && ProjectPlan(forced) is { } p ? p : GetViewport().GetMousePosition();

    private void UpdateCursor()
    {
        if (Terrain is null || _ground is null) { Cursor = null; return; }
        if (ForcedPlanCursor is { } forced)
        {
            var world = Terrain.MapToWorld(forced.X, forced.Y, _ground.GetHeight(forced));
            Cursor = new NumVector3(world.X, world.Y, world.Z);
            return;
        }
        Cursor = null;
        if (CityCamera?.Camera is not { } cam) return;
        var viewport = GetViewport();
        if (viewport.GuiGetHoveredControl() is not null) return;
        var mouse = viewport.GetMousePosition();
        var origin = ToNumerics(cam.ProjectRayOrigin(mouse));
        var dir = ToNumerics(cam.ProjectRayNormal(mouse));
        if (_ground.Raycast(origin, dir, out var hit)) Cursor = hit;
    }

    private void Place(NumVector2 position, bool hard)
    {
        if (Testbed?.Profile is not { } profile) return;
        if (_session.IsEmpty)
        {
            _sessionProfile = profile;
            _session.Reset(profile.DefaultRadius);
            // Starting on an edge makes it the soft-angle reference for the whole draw ("∡ 90° · square to edge").
            _startHeading = _snap is { Kind: SnapKind.Node or SnapKind.Edge or SnapKind.PerpendicularFoot } s &&
                            NumVector2.Distance(s.Position, position) < 1e-3f ? s.EdgeTangent : null;
        }
        bool allowHard = hard && profile.AllowHardCorners;
        if (hard && !allowHard) _hardHintUntil = Time.GetTicksMsec() / 1000.0 + 1.2;
        _session.Place(position, allowHard);
    }

    private void AdjustRadius(float factor)
    {
        if (Testbed?.Profile is not { } profile) return;
        _session.SetPendingRadius(_session.PendingRadius * factor, profile.MinRadius);
    }

    private void Finish()
    {
        if (Testbed?.Profile is not { } profile || _session.Pis.Count < 2) return;
        var alignment = _session.Finish();
        _built.Add((profile, alignment));
        _renderer?.AddBuilt(profile, alignment);
        _flash = (alignment.Pis[^1].Position, $"Total {alignment.Length:0} m", Time.GetTicksMsec() / 1000.0 + FlashSeconds);
        CancelSession();
    }

    private void RemoveLastOrCancel()
    {
        if (!_session.Undo()) CancelSession();
    }

    private void CancelSession()
    {
        _session.Reset(Testbed?.Profile?.DefaultRadius ?? 0f);
        _sessionProfile = Testbed?.Profile;
        _startHeading = null;
        _renderer?.SetPreview(null, 0);
    }

    private NumVector2 PlanOf(NumVector3 worldHit)
    {
        var map = Terrain!.WorldToMap(new Vector3(worldHit.X, worldHit.Y, worldHit.Z));
        return new NumVector2(map.X, map.Y);
    }

    private static NumVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);

    // --- Direct-API test hooks for --demo-draw and --storyboard (no simulated InputEvents) ---

    /// <summary>Places a PI at <see cref="ForcedPlanCursor"/> exactly (no snapping, so golden coordinates stay exact),
    /// but still records the start edge's heading if that point is on a built spline.</summary>
    public void PlaceForTest(bool hard)
    {
        UpdateCursor(); // ForcedPlanCursor was just set; Cursor otherwise only refreshes in _Process
        if (Cursor is not { } hit || Testbed?.Profile is not { } profile) return;
        var plan = PlanOf(hit);
        if (_session.IsEmpty)
            _snap = SnapEngine.Evaluate(new SnapQuery
            {
                Cursor = plan, Rules = profile.ToRules(), CatchDistance = 0.5f,
                EnabledProviders = SnapProviders.Node | SnapProviders.Edge,
                Candidates = _built.Select(b => new SnapCandidate(b.Alignment, b.Profile.Width)).ToList(),
            }) with { Position = plan };
        Place(plan, hard);
    }

    public void FinishForTest() => Finish();

    /// <summary>Shift+wheel / <c>[</c> <c>]</c>: scales the live corner's radius.</summary>
    public void AdjustRadiusForTest(float factor) => AdjustRadius(factor);

    /// <summary>Adds an already-made alignment as built (storyboard scenes with exact radii).</summary>
    public void AddBuiltForTest(SplineProfile profile, Alignment alignment)
    {
        _built.Add((profile, alignment));
        _renderer?.AddBuilt(profile, alignment);
    }
}
