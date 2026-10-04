using System.Collections.Generic;
using System.Linq;
using CitySim.Content;
using CitySim.Roads;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The game's build UI: <see cref="BuildBar"/> along the bottom; above it the dock with the category's always-open
/// options panel and its <see cref="BuildTray"/>. Content comes from <see cref="ContentLibrary"/>.
/// Keys: 1–4 tool mode, Ctrl+A Anarchy, / search, Esc unpicks then closes.
/// </summary>
public partial class GameHud : CanvasLayer
{
    private BuildBar _bar = null!;
    private HBoxContainer _dock = null!;
    private readonly Dictionary<string, Control> _optionPanels = new(); // category id → its options panel

    public ContentLibrary Library { get; private set; } = null!;
    public BuildTray Tray { get; private set; } = null!;
    public RoadOptionsPanel RoadOptions { get; private set; } = null!;
    public Control Root { get; private set; } = null!;

    /// <summary>Whether the Roads tray is open (the road tools only work while it is).</summary>
    public bool RoadsOpen => _dock.Visible && Tray.Category?.Id == "roads";
    /// <summary>A category's tray was opened, or the tray closed (null).</summary>
    public event System.Action<BuildCategory?>? CategoryOpened;

    public override void _Ready()
    {
        Library = ContentLibrary.Load();

        Root = new Control { Name = "HudRoot", Theme = UiTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        Root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(Root);

        _bar = new BuildBar();
        _bar.SetCategories(Library.CategoriesWithContent());
        _bar.CategoryPressed += c => { if (Tray.Category == c && _dock.Visible) Close(); else Open(c); };
        Root.AddChild(_bar);

        _dock = new HBoxContainer { Name = "Dock", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _dock.AddThemeConstantOverride("separation", (int)UiTheme.Gap);
        _dock.AnchorLeft = _dock.AnchorRight = 0.5f;
        _dock.AnchorTop = _dock.AnchorBottom = 1f;
        _dock.OffsetTop = _dock.OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);
        _dock.GrowHorizontal = Control.GrowDirection.Both;
        _dock.GrowVertical = Control.GrowDirection.Begin;
        Root.AddChild(_dock);

        RoadOptions = new RoadOptionsPanel();
        _optionPanels["roads"] = RoadOptions;
        _dock.AddChild(RoadOptions);

        Tray = new BuildTray(Library);
        Tray.CloseRequested += Close;
        Tray.ItemPicked += item => RoadOptions.SetRoad(item as RoadType);
        _dock.AddChild(Tray);
    }

    public void Close() => Open((BuildCategory?)null);

    /// <summary>Opens a category's tray (null closes it).</summary>
    public void Open(BuildCategory? category)
    {
        _bar.SetActive(category);
        _dock.Visible = category is not null;
        CategoryOpened?.Invoke(category);
        if (category is null) return;
        foreach (var (id, panel) in _optionPanels) panel.Visible = id == category.Id;
        Tray.Open(category);
        RoadOptions.SetRoad(Tray.Picked as RoadType);
    }

    public void OpenById(string categoryId) => Open(Library.Categories.FirstOrDefault(c => c.Id == categoryId));

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool open = _dock.Visible;
        bool roads = open && Tray.Category?.Id == "roads";

        if (key.Keycode == Key.Escape && open)
        {
            if (Tray.Picked is not null) Tray.Pick(null);
            else Close();
        }
        else if (key.Keycode == Key.Slash && open) Tray.FocusSearch();
        else if (key.Keycode == Key.A && key.IsCommandOrControlPressed() && roads) RoadOptions.ToggleAnarchy();
        else if (roads && key.Keycode is >= Key.Key1 and <= Key.Key4 && !key.IsCommandOrControlPressed())
            RoadOptions.SetMode((RoadDrawMode)(key.Keycode - Key.Key1));
        else return;
        GetViewport().SetInputAsHandled();
    }
}
