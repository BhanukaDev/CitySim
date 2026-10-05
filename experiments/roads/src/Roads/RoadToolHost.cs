using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines;
using CitySim.Splines.Godot;
using CitySim.UI;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// Connects the build UI to the splines addon's tools (<see cref="ISplineToolHost"/>): the road picked in the tray is
/// the profile drawn with, the options panel gives the mode, snaps, grid blocks and Anarchy. Registers a profile per
/// road type with the network and hands it the <see cref="RoadVisual"/>. <c>M</c> switches between Draw and Edit. The
/// tools only work while the Roads tray is open: closing it leaves Edit and gives Draw no profile, so both idle.
/// </summary>
public partial class RoadToolHost : Node, ISplineToolHost
{
    [Export] public GameHud? Hud { get; set; }
    [Export] public SplineNetwork? Network { get; set; }

    private Dictionary<string, SplineProfile> _profiles = new();
    private RoadDrawMode _mode;

    public RoadVisual? Visual { get; private set; }

    /// <summary>The profile a road type draws with (null for an unknown id).</summary>
    public SplineProfile? ProfileFor(string roadId) => _profiles.GetValueOrDefault(roadId);

    public SplineProfile? Profile => Hud is { RoadsOpen: true, Tray.Picked: RoadType r } ? _profiles.GetValueOrDefault(r.Id) : null;
    public SplineTool Tool { get; private set; } = SplineTool.Draw;
    public DrawMode Mode => ToDrawMode(Options?.Mode ?? RoadDrawMode.Straight);
    public SnapProviders EnabledSnaps => Options is { Snapping: true } o ? ToProviders(o.Snaps) : SnapProviders.None;
    public bool Anarchy => Options?.Anarchy == true;
    public (int Cols, int Rows) GridBlocks => Options is { } o ? (o.GridCols, o.GridRows) : (GridLayout.DefaultCols, GridLayout.DefaultRows);
    public GridFit GridFit => Options?.GridFit == RoadGridFit.LotSteps ? GridFit.LotSteps : GridFit.Even;

    public event Action<DrawMode>? ModeChanged;

    private RoadToolOptions? Options => Hud?.RoadOptions.Options;

    public override void _Ready()
    {
        if (Hud is null || Network is null) { GD.PushError("RoadToolHost needs a Hud and a Network"); return; }
        // The HUD and the network set themselves up in their own _Ready, which runs before ours (they come first).
        var roads = Hud.Library.Items.OfType<RoadType>().ToList();
        _profiles = RoadProfiles.For(roads);
        foreach (var p in _profiles.Values) Network.RegisterProfile(p);

        var style = Hud.Library.Style(RoadStyle.DefaultId);
        if (style is null) GD.PushWarning("Roads: no road style found; roads draw as flat ribbons");
        else if (Network.Ground is { } ground && Network.Terrain is { } terrain)
        {
            var defs = roads.ToDictionary(r => r.Id, r => r.ToDef());
            Visual = new RoadVisual(terrain, ground, style, id => defs.GetValueOrDefault(id));
            Network.Visual = Visual;
        }

        Hud.CategoryOpened += c => { if (c?.Id != "roads" && Tool != SplineTool.Draw) SetTool(SplineTool.Draw); };
        // A road tool (Crossings) takes the mouse: Edit stands down, and Draw idles with no road picked.
        Hud.Tray.ItemPicked += item => { if (item is RoadTool && Tool != SplineTool.Draw) SetTool(SplineTool.Draw); };
        _mode = Options!.Mode;
        Hud.RoadOptions.OptionsChanged += o =>
        {
            if (o.Mode == _mode) return;
            _mode = o.Mode;
            ModeChanged?.Invoke(Mode);
        };
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } key || key.IsCommandOrControlPressed()) return;
        if (Hud?.RoadsOpen != true || Hud.PickedRoadTool is not null) return;
        SetTool(Tool == SplineTool.Edit ? SplineTool.Draw : SplineTool.Edit);
        GetViewport().SetInputAsHandled();
    }

    public void SetTool(SplineTool tool)
    {
        Tool = tool;
        GD.Print($"Roads: tool {tool}");
    }

    public void SetAnarchy(bool on)
    {
        if (Hud is not null && Anarchy != on) Hud.RoadOptions.ToggleAnarchy();
    }

    public void SetGridBlocks(int cols, int rows) => Hud?.RoadOptions.SetGridBlocks(
        Math.Clamp(cols, 1, GridLayout.MaxBlocks), Math.Clamp(rows, 1, GridLayout.MaxBlocks));

    private static DrawMode ToDrawMode(RoadDrawMode m) => m switch
    {
        RoadDrawMode.Straight => DrawMode.Draw,
        RoadDrawMode.Curve => DrawMode.Curve,
        RoadDrawMode.Freehand => DrawMode.Freehand,
        RoadDrawMode.Replace => DrawMode.Replace,
        _ => DrawMode.Grid,
    };

    private static SnapProviders ToProviders(RoadSnaps s)
    {
        var p = SnapProviders.None;
        if (s.HasFlag(RoadSnaps.Roads)) p |= SnapProviders.Node | SnapProviders.Edge | SnapProviders.Crossing;
        if (s.HasFlag(RoadSnaps.Guides)) p |= SnapProviders.Extension | SnapProviders.NodeAlign | SnapProviders.Parallel | SnapProviders.Perpendicular;
        if (s.HasFlag(RoadSnaps.Angles)) p |= SnapProviders.Angle;
        if (s.HasFlag(RoadSnaps.Lengths)) p |= SnapProviders.Length | SnapProviders.EqualLength;
        return p;
    }
}
