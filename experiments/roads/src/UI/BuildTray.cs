using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The panel above the bar for one category: its tabs, a search box over every tab, and the cards of the open tab.
/// Remembers the open tab and the picked item per category.
/// </summary>
public partial class BuildTray : PanelContainer
{
    public const float TrayWidth = 940f;

    private readonly ContentLibrary _lib;
    private readonly Label _title = UiTheme.Label("", 17);
    private readonly HBoxContainer _tabsRow = new();
    private readonly LineEdit _search = new()
    {
        PlaceholderText = "Search",
        ClearButtonEnabled = true,
        CustomMinimumSize = new Vector2(280, 0),
    };
    private readonly Label _info = UiTheme.Label("", 12, dim: true);
    private readonly ScrollContainer _scroll = new()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
        VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        CustomMinimumSize = new Vector2(0, 150),
    };
    private readonly HBoxContainer _cards = new();
    private readonly Dictionary<string, Button> _tabButtons = new();
    private readonly Dictionary<string, string> _openTab = new();          // category id → tab id
    private readonly Dictionary<string, BuildItem?> _picked = new();       // category id → item
    private BuildCategory? _category;

    /// <summary>Raised when the picked item changes (null = nothing picked).</summary>
    public event Action<BuildItem?>? ItemPicked;
    public event Action? CloseRequested;

    public BuildCategory? Category => _category;
    public BuildItem? Picked => _category is null ? null : _picked.GetValueOrDefault(_category.Id);
    public bool Searching => _search.Text.Trim() != "";

    public BuildTray(ContentLibrary lib)
    {
        _lib = lib;
        Name = "BuildTray";
        CustomMinimumSize = new Vector2(TrayWidth, 0);
        SizeFlagsVertical = SizeFlags.ShrinkEnd;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 4);
        col.AddChild(header);
        _title.CustomMinimumSize = new Vector2(80, 0);
        header.AddChild(_title);
        _tabsRow.AddThemeConstantOverride("separation", 4);
        _tabsRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_tabsRow);
        var close = new Button { Icon = UiTheme.Icon("x"), Flat = true, ExpandIcon = true, TooltipText = "Close (Esc)", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 30) };
        close.AddThemeConstantOverride("icon_max_width", 18);
        close.Pressed += () => CloseRequested?.Invoke();
        header.AddChild(close);

        col.AddChild(new ColorRect { Color = UiTheme.Accent, CustomMinimumSize = new Vector2(0, 2) });

        var searchRow = new HBoxContainer();
        searchRow.AddThemeConstantOverride("separation", 10);
        col.AddChild(searchRow);
        _search.TextChanged += _ => RebuildCards();
        _search.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                _search.Text = "";
                _search.ReleaseFocus();
                RebuildCards();
                _search.AcceptEvent();
            }
        };
        searchRow.AddChild(_search);
        _info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _info.HorizontalAlignment = HorizontalAlignment.Right;
        _info.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        searchRow.AddChild(_info);

        _cards.AddThemeConstantOverride("separation", 8);
        _scroll.AddChild(_cards);
        col.AddChild(_scroll);
    }

    public void Open(BuildCategory category)
    {
        _category = category;
        _title.Text = category.Label;
        _search.Text = "";
        _search.PlaceholderText = $"Search {category.Label.ToLowerInvariant()}";
        foreach (var child in _tabsRow.GetChildren()) child.QueueFree();
        _tabButtons.Clear();

        var tabs = _lib.TabsOf(category).Where(t => _lib.ItemsOf(t).Any()).ToList();
        var group = new ButtonGroup();
        foreach (var tab in tabs)
        {
            if (tab.DividerBefore) _tabsRow.AddChild(new VSeparator());
            var b = new Button { Text = tab.Label, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None };
            b.Pressed += () => OpenTab(tab.Id);
            _tabsRow.AddChild(b);
            _tabButtons[tab.Id] = b;
        }
        string open = _openTab.GetValueOrDefault(category.Id) ?? tabs.FirstOrDefault()?.Id ?? "";
        OpenTab(open);
    }

    public void OpenTab(string tabId)
    {
        if (_category is null || !_tabButtons.ContainsKey(tabId)) return;
        _openTab[_category.Id] = tabId;
        if (Searching) _search.Text = "";
        RebuildCards();
    }

    public void Pick(BuildItem? item)
    {
        if (_category is null) return;
        _picked[_category.Id] = item;
        foreach (var card in _cards.GetChildren().OfType<ItemCard>())
            card.SetPressedNoSignal(card.Item == item);
        ItemPicked?.Invoke(item);
    }

    public void FocusSearch() => _search.GrabFocus();

    public void SetSearch(string text)
    {
        _search.Text = text;
        RebuildCards();
    }

    /// <summary>The card showing <paramref name="item"/>, if it is in the current list.</summary>
    public ItemCard? CardFor(BuildItem item) => _cards.GetChildren().OfType<ItemCard>().FirstOrDefault(c => c.Item == item);

    private void RebuildCards()
    {
        if (_category is null) return;
        foreach (var child in _cards.GetChildren()) child.QueueFree();

        string query = _search.Text.Trim();
        string openTab = _openTab.GetValueOrDefault(_category.Id) ?? "";
        foreach (var (id, b) in _tabButtons) b.SetPressedNoSignal(query == "" && id == openTab);

        List<ItemCard> cards;
        if (query == "")
        {
            var tab = _lib.Tabs.FirstOrDefault(t => t.Id == openTab);
            cards = tab is null ? [] : _lib.ItemsOf(tab).Select(i => new ItemCard(i)).ToList();
            _info.Text = "";
        }
        else
        {
            cards = _lib.ItemsOf(_category)
                .Where(i => i.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(i => new ItemCard(i, _lib.TabOf(i)?.Label)).ToList();
            _info.Text = cards.Count == 0 ? $"Nothing matches \"{query}\"" : $"{cards.Count} found across all tabs";
        }

        var picked = Picked;
        foreach (var card in cards)
        {
            card.SetPressedNoSignal(card.Item == picked);
            card.Pressed += () => Pick(card.Item);
            _cards.AddChild(card);
        }
    }
}
