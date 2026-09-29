using System;
using System.Collections.Generic;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Draw-mode tool (DESIGN.md → Draw tool, mode 1 of 4; ROADMAP.md S2): click to place PIs on the terrain, with
/// auto-rounded corners, Alt hard corner, Shift+wheel/<c>[</c>/<c>]</c> radius, RMB/Ctrl+Z undo, Esc/RMB-empty
/// cancel, double-click/Enter finish. No-ops unless <see cref="SplinesTestbed.Mode"/> is <see cref="DrawMode.Draw"/>.
/// Snapping (S3), the graph and its undo (S4) and Anarchy are not implemented yet — radii just clamp.
/// </summary>
public partial class SplineDrawTool : Node
{
    private const float RadiusWheelFactor = 1.1f;
    private const float RadiusKeyFactor = 1.15f;

    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public SplinesTestbed? Testbed { get; set; }

    private readonly DrawSession _session = new();
    private readonly List<(SplineProfile Profile, Alignment Alignment)> _built = new();
    private IGround? _ground;
    private RibbonRenderer? _renderer;
    private DrawCursorTag? _cursorTag;
    private SplineProfile? _sessionProfile;

    /// <summary>The current ground hit, world space. Null off the terrain or over UI.</summary>
    public NumVector3? Cursor { get; private set; }
    /// <summary>Scripted-demo override: a plan-space (map metres) position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get; set; }
    public int BuiltCount => _built.Count;

    public override void _Ready()
    {
        if (Terrain is null) { GD.PushError("SplineDrawTool needs a Terrain"); return; }
        _ground = new TerrainGround(Terrain);
        _renderer = new RibbonRenderer(Terrain, _ground);
        var layer = new CanvasLayer { Name = "SplineDrawHud" };
        AddChild(layer);
        _cursorTag = new DrawCursorTag(layer);
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
            Place(PlanOf(hit), hard: mb.AltPressed);
        }
    }

    public override void _Process(double delta)
    {
        if (Testbed is null || _ground is null || _renderer is null || _cursorTag is null) return;
        if (!_session.IsEmpty && Testbed.Profile != _sessionProfile)
        {
            GD.Print("Splines: profile changed mid-draw, cancelling");
            CancelSession();
        }

        UpdateCursor();

        if (Testbed.Mode != DrawMode.Draw || Testbed.Profile is null || _session.IsEmpty || Cursor is not { } cursor)
        {
            _renderer.SetPreview(null, 0);
            _cursorTag.Hide();
            return;
        }

        var preview = _session.BuildPreview(PlanOf(cursor));
        _renderer.SetPreview(preview, Testbed.Profile.Width);
        UpdateCursorTag(preview);
        _cursorTag.Tick(Time.GetTicksMsec() / 1000.0);
    }

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
        }
        bool allowHard = hard && profile.AllowHardCorners;
        if (hard && !allowHard) _cursorTag?.FlashHardCornerHint(Time.GetTicksMsec() / 1000.0);
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
        _renderer?.SetPreview(null, 0);
    }

    private void UpdateCursorTag(Alignment preview)
    {
        var curve = preview.Curve;
        if (curve.Length <= 0f) { _cursorTag!.Hide(); return; }

        var lastSample = curve.Sample(curve.Length);
        float length = NumVector2.Distance(_session.Pis[^1].Position, preview.Pis[^1].Position);
        float heading = Mathf.RadToDeg(SplineMath.Angle(lastSample.Tangent));
        if (heading < 0) heading += 360f;

        float turn = float.NaN;
        if (_session.Pis.Count >= 2)
        {
            var prevDir = _session.Pis[^1].Position - _session.Pis[^2].Position;
            var curDir = preview.Pis[^1].Position - _session.Pis[^1].Position;
            if (prevDir.Length() > 1e-4f && curDir.Length() > 1e-4f)
                turn = Mathf.RadToDeg(SplineMath.Turn(NumVector2.Normalize(prevDir), NumVector2.Normalize(curDir)));
        }

        _cursorTag!.Update(GetViewport().GetMousePosition(), length, heading, turn, _session.PendingRadius);
    }

    private NumVector2 PlanOf(NumVector3 worldHit)
    {
        var map = Terrain!.WorldToMap(new Vector3(worldHit.X, worldHit.Y, worldHit.Z));
        return new NumVector2(map.X, map.Y);
    }

    private static NumVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);

    // --- Direct-API test hooks for --demo-draw (no simulated InputEvents) ---

    public void PlaceForTest(bool hard)
    {
        UpdateCursor(); // ForcedPlanCursor was just set; Cursor otherwise only refreshes in _Process
        if (Cursor is { } hit) Place(PlanOf(hit), hard);
    }

    public void FinishForTest() => Finish();
}
