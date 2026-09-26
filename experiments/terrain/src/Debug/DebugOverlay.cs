using Godot;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;

namespace CitySim.Debug;

/// <summary>
/// On-screen stats and controls help. Also supports automated screenshots:
///   Godot --path . -- --screenshot=out.png [--screenshot-frames=60]
/// saves the viewport after N frames and quits.
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public Terrain? Terrain { get; set; }

    private Label _label = null!;
    private double _refresh;
    private string? _screenshotPath;
    private int _screenshotFrames = 60;

    public override void _Ready()
    {
        _label = new Label { Position = new Vector2(12, 10) };
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 4);
        AddChild(_label);

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot="))
                _screenshotPath = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--screenshot-frames=") && int.TryParse(arg["--screenshot-frames=".Length..], out int f))
                _screenshotFrames = f;
        }
    }

    public override void _Process(double delta)
    {
        if (_screenshotPath is not null && --_screenshotFrames <= 0)
        {
            var err = GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"Screenshot saved to {_screenshotPath} ({err})");
            _screenshotPath = null;
            GetTree().Quit();
            return;
        }

        _refresh -= delta;
        if (_refresh > 0) return;
        _refresh = 0.25;

        string text = $"FPS {Engine.GetFramesPerSecond():0}";
        if (CityCamera is not null)
        {
            var p = CityCamera.Pivot;
            text += $"\nPivot ({p.X:0}, {p.Y:0.0}, {p.Z:0})" +
                    $"\nYaw {CityCamera.YawDegrees:0}°  Pitch {CityCamera.PitchDegrees:0}°  Distance {CityCamera.Distance:0} m";
            if (Terrain?.Map is not null)
                text += $"\nSlope at pivot {Terrain.GetSlopeDegrees(p.X, p.Z):0.0}°";
        }
        text += "\n\nWASD move · Q/E rotate · R/F tilt · Z/X or wheel zoom";
        _label.Text = text;
    }
}
