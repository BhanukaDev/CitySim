using System;
using System.Collections.Generic;
using CitySim.Splines.Godot;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// The testbed's wiring: loads the test profiles from <see cref="ProfileDir"/> and shows the options bar. The Draw
/// tool (S2) and the Edit tool (S5) hang off this: <c>M</c> switches between them, <c>1</c>–<c>4</c> pick a draw mode.
/// </summary>
public partial class SplinesTestbed : Node
{
    [Export(PropertyHint.Dir)] public string ProfileDir { get; set; } = "res://profiles";

    public IReadOnlyList<SplineProfile> Profiles { get; private set; } = new List<SplineProfile>();
    public SplineProfile? Profile { get; private set; }
    public DrawMode Mode { get; private set; } = DrawMode.Draw;
    public SplineTool Tool { get; private set; } = SplineTool.Draw;
    public SnapProviders EnabledSnaps { get; private set; } = SnapProviders.All;
    /// <summary>Ctrl+A: Invalid issues still build (and stay red), and radii may go below the profile's minimum.</summary>
    public bool Anarchy { get; private set; }
    /// <summary>Grid mode's block, in lots of clear space between roads: along the first edge, and across it.</summary>
    public (int Along, int Across) GridLots { get; private set; } = (GridLayout.DefaultLots, GridLayout.DefaultLots);

    private SplineOptionsBar? _bar;

    /// <summary>A draw mode was picked (the Draw tool ends its chain).</summary>
    public event System.Action<DrawMode>? ModeChanged;

    public override void _Ready()
    {
        Profiles = LoadProfiles(ProfileDir);

        var layer = new CanvasLayer { Name = "SplineUi" };
        AddChild(layer);
        _bar = new SplineOptionsBar();
        layer.AddChild(_bar);
        _bar.SetProfiles(Profiles);
        Profile = _bar.Profile;
        if (Profile is not null) _bar.SetOfferedSnaps(Profile.SnapProviders);
        _bar.ProfileChanged += p =>
        {
            Profile = p;
            _bar.SetOfferedSnaps(p.SnapProviders);
            GD.Print($"Splines: profile {p.Id}");
        };
        _bar.ModeChanged += m =>
        {
            Mode = m;
            GD.Print($"Splines: mode {m}");
            ModeChanged?.Invoke(m);
        };
        _bar.ToolChanged += t => { Tool = t; GD.Print($"Splines: tool {t}"); };
        _bar.SnapProvidersChanged += p => EnabledSnaps = p;
        _bar.AnarchyChanged += on => SetAnarchy(on);
        _bar.GridLotsChanged += (a, c) => GridLots = (a, c);
        _bar.SetGridLots(GridLots.Along, GridLots.Across);
        EnabledSnaps = _bar.EnabledSnaps;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || key.IsCommandOrControlPressed() || key.AltPressed) return;
        if (key.Keycode == Key.M) SetTool(Tool == SplineTool.Edit ? SplineTool.Draw : SplineTool.Edit);
        else if (key.Keycode is >= Key.Key1 and <= Key.Key4) _bar?.SetMode((DrawMode)(key.Keycode - Key.Key1));
        else return;
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Switches tool (M, the options bar, or a scripted frame).</summary>
    public void SetTool(SplineTool tool)
    {
        if (_bar is null) { Tool = tool; return; }
        _bar.SetTool(tool);
    }

    /// <summary>Picks a draw mode (keys 1–4, or a scripted frame); switches to the Draw tool.</summary>
    public void SetMode(DrawMode mode)
    {
        if (_bar is null) { Mode = mode; return; }
        _bar.SetMode(mode);
    }

    /// <summary>Sets Grid mode's block size (keys, or a scripted frame), kept within limits, and shows it in the bar.</summary>
    public void SetGridLots(int along, int across)
    {
        GridLots = (Math.Clamp(along, GridLayout.MinLots, GridLayout.MaxLots), Math.Clamp(across, GridLayout.MinLots, GridLayout.MaxLots));
        _bar?.SetGridLots(GridLots.Along, GridLots.Across);
    }

    public void SetAnarchy(bool on)
    {
        Anarchy = on;
        _bar?.SetAnarchy(on);
        GD.Print($"Splines: anarchy {(on ? "on" : "off")}");
    }

    /// <summary>Selects a profile in the options bar (and fires the same event a click would) — used by
    /// <c>--demo-draw</c> to switch profiles without simulating UI input.</summary>
    public void SelectProfile(SplineProfile profile)
    {
        if (_bar is null) return;
        for (int i = 0; i < Profiles.Count; i++)
            if (Profiles[i] == profile) { _bar.SelectIndex(i); Profile = profile; return; }
    }

    /// <summary>Every <see cref="SplineProfile"/> <c>.tres</c> in a folder, sorted by file name.</summary>
    private static List<SplineProfile> LoadProfiles(string dir)
    {
        var result = new List<SplineProfile>();
        var files = DirAccess.GetFilesAt(dir);
        System.Array.Sort(files);
        foreach (var file in files)
        {
            // Exported builds list resources as "name.tres.remap".
            var name = file.TrimSuffix(".remap");
            if (!name.EndsWith(".tres")) continue;
            if (ResourceLoader.Load($"{dir}/{name}") is SplineProfile p) result.Add(p);
            else GD.PrintErr($"Splines: {dir}/{name} is not a SplineProfile");
        }
        return result;
    }
}
