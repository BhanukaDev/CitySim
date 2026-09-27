using System;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Themes;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side): picks the map's terrain theme and shows what it contains. Themes are made in the Godot
/// editor (themes/&lt;id&gt;/theme.tres, see terrain_sdk/README.md), so this panel only chooses and inspects: its
/// materials, which erosion features it draws, and debug views. Reload picks up a theme edited and baked in Godot.
/// </summary>
public partial class ThemePanel : PanelContainer
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private readonly OptionButton _themes;
    private readonly Label _description;
    private readonly HFlowContainer _materials;
    private readonly GridContainer _slots;
    private readonly OptionButton _view;
    private readonly Label _status;
    private bool _syncing;

    private Terrain? Terrain => Tools?.Terrain;

    public ThemePanel()
    {
        Name = "ThemePanel";
        Visible = false;
        AnchorLeft = AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 1f;
        OffsetLeft = -356f;
        OffsetRight = -16f;
        OffsetTop = 16f;
        OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        var title = new Label { Text = "Terrain Theme", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28), TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        var pick = new HBoxContainer();
        _themes = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The map's look (saved with the map)" };
        _themes.ItemSelected += OnThemeSelected;
        pick.AddChild(_themes);
        var reload = new Button { Text = "Reload", FocusMode = FocusModeEnum.None, TooltipText = "Load the theme again from disk, after editing and baking it in Godot" };
        reload.Pressed += () =>
        {
            Terrain?.ReloadTheme();
            _status.Text = $"Reloaded '{Terrain?.Theme?.Label}'.";
        };
        pick.AddChild(reload);
        col.AddChild(pick);

        _description = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
        _description.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_description);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(body);

        body.AddChild(SectionLabel("Materials"));
        _materials = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _materials.AddThemeConstantOverride("h_separation", 6);
        _materials.AddThemeConstantOverride("v_separation", 6);
        body.AddChild(_materials);

        body.AddChild(SectionLabel("Erosion & water"));
        _slots = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _slots.AddThemeConstantOverride("h_separation", 10);
        _slots.AddThemeConstantOverride("v_separation", 4);
        body.AddChild(_slots);

        var viewRow = new HBoxContainer();
        viewRow.AddChild(new Label { Text = "View" });
        _view = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _view.AddItem("Normal");
        _view.AddItem("Strongest material");
        _view.AddItem("Cost (textures per pixel)");
        foreach (var s in ErosionSlotInfo.All) _view.AddItem("Slot: " + s.Name);
        _view.ItemSelected += OnViewSelected;
        viewRow.AddChild(_view);
        col.AddChild(viewRow);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
        _status.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_status);
    }

    public void Open()
    {
        Visible = true;
        if (Terrain is { } t) t.ThemeChanged += Sync;
        _status.Text = "Themes are made in Godot: see terrain_sdk/README.md.";
        Sync();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (Terrain is { } t)
        {
            t.ThemeChanged -= Sync;
            t.DebugView = 0;
            t.SlotDebug = -1;
        }
        _view.Selected = 0;
        Closed?.Invoke();
    }

    private void OnThemeSelected(long index)
    {
        if (_syncing || Terrain is not { } t || index < 0 || index >= ThemeLibrary.All.Count) return;
        var theme = ThemeLibrary.All[(int)index];
        t.SetTheme(theme);
        int kept = 0;
        if (t.Splat is { } s)
        {
            var used = s.UsedLayers();
            for (int i = theme.Materials.Count; i < s.Palette.Length; i++) if (used[i]) kept++;
        }
        _status.Text = kept > 0
            ? $"'{theme.Label}' doesn't have {kept} of the painted materials: they show as automatic ground until you switch back."
            : $"Switched to '{theme.Label}'.";
    }

    private void OnViewSelected(long index)
    {
        if (Terrain is not { } t) return;
        t.DebugView = index is 1 or 2 ? (int)index : 0;
        t.SlotDebug = index >= 3 ? (int)index - 3 : -1;
    }

    /// <summary>Refills the panel from the terrain's current theme.</summary>
    private void Sync()
    {
        _syncing = true;
        _themes.Clear();
        var themes = ThemeLibrary.All;
        for (int i = 0; i < themes.Count; i++)
        {
            _themes.AddItem(themes[i].Label);
            if (themes[i].Id == Terrain?.Theme?.Id) _themes.Selected = i;
        }
        _syncing = false;

        foreach (var c in _materials.GetChildren()) c.QueueFree();
        foreach (var c in _slots.GetChildren()) c.QueueFree();
        if (Terrain?.Theme is not { } theme) return;
        _description.Text = theme.Description;

        foreach (var m in theme.Materials)
        {
            if (m is null) continue;
            var cell = new VBoxContainer { TooltipText = $"{m.Label} ({m.Id})", MouseFilter = MouseFilterEnum.Pass };
            cell.AddChild(new TextureRect
            {
                Texture = ThemePreviews.Get(theme, m),
                CustomMinimumSize = new Vector2(56, 56),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = MouseFilterEnum.Pass,
            });
            var name = new Label
            {
                Text = m.Label, CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Center,
                ClipText = true, Modulate = UiTheme.TextDim, MouseFilter = MouseFilterEnum.Pass,
            };
            name.AddThemeFontSizeOverride("font_size", 10);
            cell.AddChild(name);
            _materials.AddChild(cell);
        }
        var slots = theme.Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            var info = ErosionSlotInfo.All[i];
            _slots.AddChild(new Label { Text = info.Name, TooltipText = info.Help, MouseFilter = MouseFilterEnum.Pass });
            int mi = theme.SlotMaterialIndex(slots[i]);
            _slots.AddChild(new Label
            {
                Text = mi >= 0 ? theme.Materials[mi].Label : "off",
                Modulate = mi >= 0 ? UiTheme.Text : UiTheme.TextDim,
            });
        }
    }

    private static Label SectionLabel(string text)
    {
        var l = new Label { Text = text, Modulate = UiTheme.Accent };
        l.AddThemeFontSizeOverride("font_size", 13);
        return l;
    }
}

/// <summary>The baked preview swatch of a theme material (<see cref="ThemeBaker"/>), or null before the theme is baked.</summary>
public static class ThemePreviews
{
    public static Texture2D? Get(TerrainTheme theme, TerrainMaterial m)
    {
        string path = theme.PreviewPath(m);
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }
}
