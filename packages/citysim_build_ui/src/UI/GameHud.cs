using System.Collections.Generic;
using System.Linq;
using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The game's build UI: <see cref="BuildBar"/> along the bottom; above it the dock with the category's always-open
/// options panel and its <see cref="BuildTray"/>. Content comes from <see cref="ContentLibrary"/>. Each feature adds its
/// own options panel for its category (<see cref="AddOptionsPanel"/>), so the HUD knows nothing about roads or zones.
/// Keys: / search, Esc unpicks then closes (a tool backs out of its own state first); the open category's panel gets
/// the rest (<see cref="IOptionsPanel.HandleKey"/>: the roads panel takes 1–4 and Ctrl+A).
/// </summary>
public partial class GameHud : CanvasLayer
{
    private BuildBar _bar = null!;
    private HBoxContainer _dock = null!;
    private readonly Dictionary<string, Control> _optionPanels = new(); // category id → its options panel

    public ContentLibrary Library { get; private set; } = null!;
    public BuildTray Tray { get; private set; } = null!;
    public Control Root { get; private set; } = null!;

    /// <summary>Whether the tray of category <paramref name="categoryId"/> is open (a feature's tools only work while it is).</summary>
    public bool IsOpen(string categoryId) => _dock.Visible && Tray.Category?.Id == categoryId;
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

        Tray = new BuildTray(Library);
        Tray.CloseRequested += Close;
        Tray.ItemPicked += ShowItem;
        _dock.AddChild(Tray);
    }

    /// <summary>
    /// Shows <paramref name="panel"/> left of the tray while category <paramref name="categoryId"/> is open. Features add
    /// theirs from their own <c>_Ready</c> (the HUD's runs first, as it comes first in the scene).
    /// </summary>
    public void AddOptionsPanel<T>(string categoryId, T panel) where T : Control, IOptionsPanel
    {
        _optionPanels[categoryId] = panel;
        panel.Visible = IsOpen(categoryId);
        _dock.AddChild(panel);
        _dock.MoveChild(panel, Tray.GetIndex());
    }

    /// <summary>The options panel of type <typeparamref name="T"/>, if one was added.</summary>
    public T? OptionsPanel<T>() where T : Control => _optionPanels.Values.OfType<T>().FirstOrDefault();

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
        ShowItem(Tray.Picked);
    }

    /// <summary>The picked item in the open category's options panel.</summary>
    private void ShowItem(BuildItem? item)
    {
        if (OpenPanel is { } panel) panel.SetItem(item, TabLabel(item));
    }

    private IOptionsPanel? OpenPanel => Tray.Category is { } c ? _optionPanels.GetValueOrDefault(c.Id) as IOptionsPanel : null;

    private string? TabLabel(BuildItem? item) => item is null ? null : Library.TabOf(item)?.Label;

    public void OpenById(string categoryId) => Open(Library.Categories.FirstOrDefault(c => c.Id == categoryId));

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool open = _dock.Visible;

        if (key.Keycode == Key.Escape && open)
        {
            if (Tray.Picked is not null) Tray.Pick(null);
            else Close();
        }
        else if (key.Keycode == Key.Slash && open) Tray.FocusSearch();
        else if (!open || OpenPanel?.HandleKey(key) != true) return;
        GetViewport().SetInputAsHandled();
    }
}

/// <summary>A category's always-open options panel next to its tray (<see cref="GameHud.AddOptionsPanel"/>).</summary>
public interface IOptionsPanel
{
    /// <summary>The item picked in the tray (null: none), with its tab's label for the chip.</summary>
    void SetItem(BuildItem? item, string? tabLabel);
    /// <summary>A key pressed while this panel's category is open; true when the panel used it.</summary>
    bool HandleKey(InputEventKey key) => false;
}
