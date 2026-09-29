using System.Collections.Generic;
using CitySim.Splines.Godot;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// The testbed's wiring: loads the test profiles from <see cref="ProfileDir"/> and shows the options bar. The Draw
/// tool (S2) hangs off this.
/// </summary>
public partial class SplinesTestbed : Node
{
    [Export(PropertyHint.Dir)] public string ProfileDir { get; set; } = "res://profiles";

    public IReadOnlyList<SplineProfile> Profiles { get; private set; } = new List<SplineProfile>();
    public SplineProfile? Profile { get; private set; }
    public DrawMode Mode { get; private set; } = DrawMode.Draw;
    public SnapProviders EnabledSnaps { get; private set; } = SnapProviders.All;
    /// <summary>Ctrl+A: Invalid issues still build (and stay red), and radii may go below the profile's minimum.</summary>
    public bool Anarchy { get; private set; }

    private SplineOptionsBar? _bar;

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
        _bar.ModeChanged += m => { Mode = m; GD.Print($"Splines: mode {m}"); };
        _bar.SnapProvidersChanged += p => EnabledSnaps = p;
        _bar.AnarchyChanged += on => SetAnarchy(on);
        EnabledSnaps = _bar.EnabledSnaps;
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
