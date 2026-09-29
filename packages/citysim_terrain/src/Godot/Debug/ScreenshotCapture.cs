using System.Collections.Generic;
using System.Globalization;
using CitySim.CameraSystem;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// Command-line view and screenshot tools any project with a Terrain can use, to check visual changes without a person:
/// <list type="bullet">
/// <item><c>--cam=x,z,distance,pitch,yaw</c>: puts the camera there (x, z in map metres from the map's corner; degrees).</item>
/// <item><c>--screenshot=out.png [--screenshot-frames=60]</c>: saves the viewport after that many frames, then quits.</item>
/// <item><c>--screenshot-zoom=60,120,250</c> (with --cam): more shots from the same pivot at those distances
///   (out_&lt;distance&gt;.png), with the water paused so only the view changes.</item>
/// <item><c>--setting=Graphics.WetShine=false</c> / <c>--setting=Tuning.HorizonMinRelief=600</c>: sets a
///   <see cref="TerrainGraphics"/> / <see cref="TerrainTuning"/> property (repeatable).</item>
/// </list>
/// </summary>
public partial class ScreenshotCapture : Node
{
    [Export] public Terrain? Terrain { get; set; }
    [Export] public CityCamera? CityCamera { get; set; }

    /// <summary>Raised after each screenshot is saved, with its path.</summary>
    [Signal] public delegate void CapturedEventHandler(string path);

    private string? _path;
    private int _frames = 60;
    private readonly Queue<float> _zoomShots = new();
    private float[]? _cam;
    private double _zoomWait;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot="))
                _path = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--screenshot-frames=") && int.TryParse(arg["--screenshot-frames=".Length..], out int f))
                _frames = f;
            else if (arg.StartsWith("--screenshot-zoom="))
                foreach (var t in arg["--screenshot-zoom=".Length..].Split(','))
                    _zoomShots.Enqueue(float.Parse(t, CultureInfo.InvariantCulture));
            else if (arg.StartsWith("--cam=") && CityCamera is not null)
            {
                var v = System.Array.ConvertAll(arg["--cam=".Length..].Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                if (v.Length != 5) GD.PushError("--cam needs x,z,distance,pitch,yaw");
                else
                {
                    _cam = v;
                    Callable.From(() => CityCamera.JumpTo(Pivot(v), v[2], v[3], v[4])).CallDeferred();
                }
            }
            else if (arg.StartsWith("--setting="))
                Callable.From(() => ApplySetting(arg["--setting=".Length..])).CallDeferred();
        }
    }

    public override void _Process(double delta)
    {
        if (_path is null || --_frames > 0 || (_zoomWait -= delta) > 0) return;
        string path = _path;
        if (_zoomShots.Count > 0 && _cam is not null)
            path = System.IO.Path.ChangeExtension(path, null) + $"_{_cam[2]:0}.png";
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"Screenshot saved to {path} ({err})");
        if (Terrain?.Falls is { } falls)
            GD.Print($"Waterfalls: {falls.Segments} curtain segments, {falls.Puffs} mist puffs, search {falls.ScanMs:0.0} ms");
        EmitSignal(SignalName.Captured, path);
        if (_zoomShots.Count > 0 && _cam is not null && CityCamera is not null)
        {
            // Same pivot, next distance, with the sim paused so only the view changes; wait for the camera and the LODs to settle.
            if (Terrain?.Water is { } sim) sim.Settings = sim.Settings with { Paused = true };
            _cam[2] = _zoomShots.Dequeue();
            CityCamera.JumpTo(Pivot(_cam), _cam[2], _cam[3], _cam[4]);
            _frames = 60;
            _zoomWait = 2.0;
            if (_zoomShots.Count == 0) _path = System.IO.Path.ChangeExtension(_path, null) + $"_{_cam[2]:0}.png";
            return;
        }
        _path = null;
        GetTree().Quit();
    }

    /// <summary>World pivot for --cam's map-metre x, z.</summary>
    private Vector2 Pivot(float[] cam)
    {
        var w = Terrain?.MapToWorld(cam[0], cam[1]) ?? new Vector3(cam[0], 0f, cam[1]);
        return new Vector2(w.X, w.Z);
    }

    /// <summary>"Graphics.Name=value" or "Tuning.Name=value"; the value is parsed like the property's current type.</summary>
    private void ApplySetting(string spec)
    {
        int dot = spec.IndexOf('.'), eq = spec.IndexOf('=');
        Resource? target = dot < 0 ? null : spec[..dot] switch
        {
            "Graphics" => Terrain?.Graphics,
            "Tuning" => Terrain?.Tuning,
            _ => null,
        };
        if (target is null || eq < dot)
        {
            GD.PushError($"--setting={spec}: expected Graphics.Name=value or Tuning.Name=value");
            return;
        }
        string name = spec[(dot + 1)..eq], text = spec[(eq + 1)..];
        var current = target.Get(name);
        Variant value = current.VariantType switch
        {
            Variant.Type.Bool => bool.Parse(text),
            Variant.Type.Int => int.TryParse(text, out int i) ? i : EnumValue(target, name, text),
            Variant.Type.Float => float.Parse(text, CultureInfo.InvariantCulture),
            _ => GD.StrToVar(text),
        };
        target.Set(name, value);
        GD.Print($"Setting {spec[..eq]} = {target.Get(name)}");
    }

    /// <summary>An enum property's value by name (e.g. "Low").</summary>
    private static int EnumValue(Resource target, string name, string text)
    {
        foreach (var p in target.GetPropertyList())
            if (p["name"].AsString() == name)
            {
                var items = p["hint_string"].AsString().Split(',');
                for (int i = 0; i < items.Length; i++)
                {
                    var parts = items[i].Split(':');
                    if (parts[0] == text) return parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : i;
                }
            }
        GD.PushError($"--setting: '{text}' isn't a value of {name}");
        return target.Get(name).AsInt32();
    }
}
