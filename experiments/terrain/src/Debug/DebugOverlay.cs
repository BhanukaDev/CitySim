using Godot;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using CitySim.Tools;

namespace CitySim.Debug;

/// <summary>
/// On-screen stats and controls help. Also supports automated screenshots:
///   Godot --path . -- --screenshot=out.png [--screenshot-frames=60]
/// saves the viewport after N frames and quits. Also: --cam=x,z,distance,pitch,yaw, --demo-sculpt, --demo-paint, --demo-camera,
/// and --bake-terrain-textures (packs the layer textures for import, then quits; see TextureBaker).
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    [Export] public CityCamera? CityCamera { get; set; }
    [Export] public Terrain? Terrain { get; set; }
    [Export] public TerrainToolController? Tools { get; set; }

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
            else if (arg.StartsWith("--cam=") && CityCamera is not null)
            {
                // --cam=x,z,distance,pitch,yaw (world metres / degrees)
                var v = System.Array.ConvertAll(arg["--cam=".Length..].Split(','),
                    s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                if (v.Length == 5)
                    Callable.From(() => CityCamera.JumpTo(new Vector2(v[0], v[1]), v[2], v[3], v[4])).CallDeferred();
            }
            else if (arg == "--demo-sculpt" && Tools is not null)
                Callable.From(Tools.RunDemo).CallDeferred();
            else if (arg == "--demo-camera" && CityCamera is not null)
                Callable.From(CityCamera.RunDemo).CallDeferred();
            else if (arg == "--demo-paint" && Tools is not null)
                Callable.From(Tools.RunPaintDemo).CallDeferred();
            else if (arg == "--bake-terrain-textures")
            {
                bool ok = TextureBaker.Bake();
                GetTree().Quit(ok ? 0 : 1);
            }
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
        if (Tools is not null && Terrain is not null)
        {
            text += $"\nTool {Tools.Tool}";
            if (Tools.Cursor is { } c)
                text += $"  ·  cursor height {c.Y:0.0} m, slope {Terrain.GetSlopeDegrees(c.X, c.Z):0.0}°";
            text += $"\nLast rebuild {Terrain.LastRebuildChunks} chunks in {Terrain.LastRebuildMs:0.0} ms";
        }
        text += "\n\nWASD move · Q/E rotate · R/F tilt · Z/X or wheel zoom" +
                "\nCtrl/Cmd+Z undo · Ctrl/Cmd+Shift+Z redo · Esc deselect tool · G grid";
        _label.Text = text;
    }
}
