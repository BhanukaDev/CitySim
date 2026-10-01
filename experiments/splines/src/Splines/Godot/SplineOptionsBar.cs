using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Draw tool's options bar along the top of the screen: a profile picker, the mode strip (Draw · Curve ·
/// Freehand · Grid) with the Edit tool (M) beside it, and the snap-provider toggle row (DESIGN.md → Snapping and guides: "each snap and guide type
/// toggles in the options bar"), and the Anarchy toggle (Ctrl+A). Radius and elevation step join it in later milestones.
/// </summary>
public partial class SplineOptionsBar : PanelContainer
{
    private readonly OptionButton _profilePicker = new() { TooltipText = "Profile" };
    private readonly Dictionary<DrawMode, Button> _modeButtons = new();
    private readonly Button _editButton = new()
    {
        Text = "M Edit", ToggleMode = true, FocusMode = FocusModeEnum.None,
        TooltipText = "M: select, move and reshape built splines",
    };
    private readonly Dictionary<SnapProviders, Button> _snapButtons = new();
    private readonly Button _anarchy = new()
    {
        Text = "Anarchy", ToggleMode = true, FocusMode = FocusModeEnum.None,
        TooltipText = "Ctrl+A: build past radius, angle and grade limits (the result stays red)",
    };
    private IReadOnlyList<SplineProfile> _profiles = Array.Empty<SplineProfile>();

    public event Action<SplineProfile>? ProfileChanged;
    public event Action<DrawMode>? ModeChanged;
    public event Action<SplineTool>? ToolChanged;
    public event Action<SnapProviders>? SnapProvidersChanged;
    public event Action<bool>? AnarchyChanged;

    public SplineProfile? Profile => _profilePicker.Selected >= 0 && _profilePicker.Selected < _profiles.Count
        ? _profiles[_profilePicker.Selected] : null;
    public DrawMode Mode { get; private set; } = DrawMode.Draw;
    public SplineTool Tool { get; private set; } = SplineTool.Draw;

    /// <summary>The bar's own toggle bitmask, ANDed with the active profile's <c>SnapProviders</c> — the profile
    /// is a ceiling this can only narrow, never widen.</summary>
    public SnapProviders EnabledSnaps { get; private set; } = SnapProviders.All;

    public SplineOptionsBar()
    {
        Name = "SplineOptionsBar";
        SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);

        var container = new VBoxContainer();
        AddChild(container);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        container.AddChild(row);

        row.AddChild(new Label { Text = "Profile" });
        _profilePicker.CustomMinimumSize = new Vector2(140, 0);
        _profilePicker.ItemSelected += i => { if (Profile is { } p) ProfileChanged?.Invoke(p); };
        row.AddChild(_profilePicker);
        row.AddChild(new VSeparator());

        var group = new ButtonGroup();
        foreach (DrawMode mode in Enum.GetValues<DrawMode>())
        {
            var b = new Button
            {
                Text = $"{(int)mode + 1} {mode}", ToggleMode = true, ButtonGroup = group,
                ButtonPressed = mode == Mode, FocusMode = FocusModeEnum.None,
            };
            b.Pressed += () => SetMode(mode);
            row.AddChild(b);
            _modeButtons[mode] = b;
        }
        _editButton.ButtonGroup = group;
        _editButton.Pressed += () => SetTool(SplineTool.Edit);
        row.AddChild(_editButton);
        row.AddChild(new VSeparator());
        _anarchy.Toggled += on => AnarchyChanged?.Invoke(on);
        row.AddChild(_anarchy);

        var snapRow = new HBoxContainer();
        snapRow.AddThemeConstantOverride("separation", 4);
        container.AddChild(snapRow);
        snapRow.AddChild(new Label { Text = "Snap" });
        foreach (SnapProviders flag in Enum.GetValues<SnapProviders>())
        {
            // Crossing isn't its own toggle (DESIGN.md → Snapping and guides): a crossing is the strongest pick
            // whenever two of the *other* enabled guides happen to line up, not an independently switchable snap
            // or guide type — the storyboard shows five guide types (extension, node align, parallel,
            // perpendicular, equal length), not six.
            if (flag is SnapProviders.None or SnapProviders.All or SnapProviders.Crossing) continue;
            var b = new Button
            {
                Text = flag switch
                {
                    SnapProviders.NodeAlign => "Node align",
                    SnapProviders.EqualLength => "Equal length",
                    _ => flag.ToString(),
                },
                ToggleMode = true, ButtonPressed = true, FocusMode = FocusModeEnum.None,
            };
            b.Pressed += () => ToggleSnap(flag, b.ButtonPressed);
            snapRow.AddChild(b);
            _snapButtons[flag] = b;
        }
    }

    private void ToggleSnap(SnapProviders flag, bool pressed)
    {
        EnabledSnaps = pressed ? EnabledSnaps | flag : EnabledSnaps & ~flag;
        SnapProvidersChanged?.Invoke(EnabledSnaps);
    }

    /// <summary>Greys out the toggles the active profile doesn't offer (its <c>SnapProviders</c> is a ceiling).</summary>
    public void SetOfferedSnaps(SnapProviders offered)
    {
        foreach (var (flag, button) in _snapButtons)
        {
            bool on = offered.HasFlag(flag);
            button.Disabled = !on;
            button.TooltipText = on ? "" : "Not offered by this profile";
        }
    }

    public void SetProfiles(IReadOnlyList<SplineProfile> profiles)
    {
        _profiles = profiles;
        _profilePicker.Clear();
        foreach (var p in profiles) _profilePicker.AddItem(p.Label);
        if (profiles.Count > 0) _profilePicker.Select(0);
    }

    /// <summary>Selects a profile by index and fires <see cref="ProfileChanged"/> (unlike <c>OptionButton.Select</c>,
    /// which doesn't emit a signal on its own) — used by <c>--demo-draw</c> to switch profiles programmatically.</summary>
    public void SelectIndex(int index)
    {
        if (index < 0 || index >= _profiles.Count) return;
        _profilePicker.Select(index);
        ProfileChanged?.Invoke(_profiles[index]);
    }

    /// <summary>Shows Anarchy on or off (Ctrl+A) without firing <see cref="AnarchyChanged"/>.</summary>
    public void SetAnarchy(bool on) => _anarchy.SetPressedNoSignal(on);

    /// <summary>Picks a draw mode, which also switches to the Draw tool.</summary>
    public void SetMode(DrawMode mode)
    {
        _modeButtons[mode].SetPressedNoSignal(true);
        SetToolState(SplineTool.Draw);
        if (mode == Mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    /// <summary>Switches tool; back to Draw it's in the last draw mode.</summary>
    public void SetTool(SplineTool tool)
    {
        (tool == SplineTool.Edit ? _editButton : _modeButtons[Mode]).SetPressedNoSignal(true);
        SetToolState(tool);
    }

    private void SetToolState(SplineTool tool)
    {
        if (tool == Tool) return;
        Tool = tool;
        ToolChanged?.Invoke(tool);
    }
}
