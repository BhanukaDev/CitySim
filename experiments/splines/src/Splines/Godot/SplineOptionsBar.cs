using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Draw tool's options bar along the top of the screen: a profile picker and the mode strip (Draw · Curve ·
/// Freehand · Grid). Plain Godot controls; snap toggles, radius and elevation step join it in later milestones.
/// </summary>
public partial class SplineOptionsBar : PanelContainer
{
    private readonly OptionButton _profilePicker = new() { TooltipText = "Profile" };
    private readonly Dictionary<DrawMode, Button> _modeButtons = new();
    private IReadOnlyList<SplineProfile> _profiles = Array.Empty<SplineProfile>();

    public event Action<SplineProfile>? ProfileChanged;
    public event Action<DrawMode>? ModeChanged;

    public SplineProfile? Profile => _profilePicker.Selected >= 0 && _profilePicker.Selected < _profiles.Count
        ? _profiles[_profilePicker.Selected] : null;
    public DrawMode Mode { get; private set; } = DrawMode.Draw;

    public SplineOptionsBar()
    {
        Name = "SplineOptionsBar";
        SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

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

    public void SetMode(DrawMode mode)
    {
        _modeButtons[mode].SetPressedNoSignal(true);
        if (mode == Mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }
}
