using System;
using Godot;

namespace CitySim.Lookdev;

/// <summary>
/// Orbit camera for scenes/WaterLookdev.tscn when it runs (F6). 1–9 jump to a station, middle or right drag orbits,
/// wheel zooms, W/A/S/D pans. Flags (after `--`): --station=n, --cam=distance,pitch,yaw (degrees),
/// --screenshot=path [--screenshot-frames=n] saves the view and quits.
/// </summary>
public partial class LookdevCamera : Camera3D
{
    private Vector3 _target;
    private float _distance = 110f, _pitch = 22f, _yaw = 25f;
    private string? _shot;
    private int _shotFrames = 90;

    public override void _Ready()
    {
        int station = 0;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--station=") && int.TryParse(arg["--station=".Length..], out int s)) station = s - 1;
            else if (arg.StartsWith("--screenshot=")) _shot = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--screenshot-frames=") && int.TryParse(arg["--screenshot-frames=".Length..], out int f)) _shotFrames = f;
            else if (arg.StartsWith("--cam="))
            {
                var p = arg["--cam=".Length..].Split(',');
                if (p.Length == 3)
                {
                    _distance = float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
                    _pitch = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    _yaw = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        Focus(station);
        Far = 4000f;
    }

    private void Focus(int index)
    {
        var stations = GetTree().GetNodesInGroup("lookdev_station");
        if (index < 0 || index >= stations.Count) return;
        _target = ((WaterLookdevStation)stations[index]).Focus;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)
            Focus((int)(k.Keycode - Key.Key1));
        else if (e is InputEventMouseMotion m && (m.ButtonMask & (MouseButtonMask.Middle | MouseButtonMask.Right)) != 0)
        {
            _yaw -= m.Relative.X * 0.3f;
            _pitch = Math.Clamp(_pitch + m.Relative.Y * 0.3f, -5f, 89f);
        }
        else if (e is InputEventMouseButton { Pressed: true } b)
        {
            if (b.ButtonIndex == MouseButton.WheelUp) _distance = MathF.Max(_distance * 0.9f, 5f);
            if (b.ButtonIndex == MouseButton.WheelDown) _distance = MathF.Min(_distance * 1.1f, 1500f);
        }
        else if (e is InputEventMagnifyGesture g) _distance = Math.Clamp(_distance / g.Factor, 5f, 1500f);
        else if (e is InputEventPanGesture p) _distance = Math.Clamp(_distance * (1f + p.Delta.Y * 0.05f), 5f, 1500f);
    }

    public override void _Process(double delta)
    {
        var move = new Vector2(
            (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0),
            (Input.IsKeyPressed(Key.S) ? 1 : 0) - (Input.IsKeyPressed(Key.W) ? 1 : 0));
        if (move != Vector2.Zero)
        {
            float yaw = Mathf.DegToRad(_yaw);
            var fwd = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            var right = new Vector3(fwd.Z, 0f, -fwd.X);
            _target += (right * move.X + fwd * move.Y) * MathF.Max(_distance, 20f) * (float)delta;
        }
        float p = Mathf.DegToRad(_pitch), y = Mathf.DegToRad(_yaw);
        var offset = new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p)) * _distance;
        LookAtFromPosition(_target + offset, _target, Vector3.Up);

        if (_shot is not null && --_shotFrames <= 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shot);
            GD.Print($"Lookdev screenshot: {_shot}");
            GetTree().Quit();
            _shot = null;
        }
    }
}
