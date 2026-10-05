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
        CustomMinimumSize = new Vector2(230, 0),
    };
    private readonly Label _info = UiTheme.Label("", 11, dim: true);
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
        _lib.Changed += () => { if (_category is not null) Reopen(); };
        Name = "BuildTray";
        CustomMinimumSize = new Vector2(TrayWidth, 0);
        SizeFlagsVertical = SizeFlags.ShrinkEnd;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        // Header: title, text tabs (the open one underlined in accent), result count, search, close; a hairline under it.
        var top = new VBoxContainer();
        top.AddThemeConstantOverride("separation", 0);
        col.AddChild(top);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        top.AddChild(header);
        _title.CustomMinimumSize = new Vector2(70, 0);
        _title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        header.AddChild(_title);
        _tabsRow.AddThemeConstantOverride("separation", 2);
        _tabsRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_tabsRow);
        _info.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        header.AddChild(_info);

        _search.RightIcon = UiTheme.Icon("search", 15);
        _search.AddThemeColorOverride("clear_button_color", UiTheme.TextDim);
        _search.SizeFlagsVertical = SizeFlags.ShrinkCenter;
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
        header.AddChild(_search);
        var close = new Button { Icon = UiTheme.Icon("x"), Flat = true, ExpandIcon = true, TooltipText = "Close (Esc)", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28) };
        close.AddThemeConstantOverride("icon_max_width", 16);
        close.AddThemeColorOverride("icon_normal_color", UiTheme.TextDim);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        close.Pressed += () => CloseRequested?.Invoke();
        header.AddChild(close);
        top.AddChild(UiTheme.Rule());

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
            if (tab.DividerBefore) _tabsRow.AddChild(new VSeparator { CustomMinimumSize = new Vector2(12, 16), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var b = Tab(tab.Label);
            b.ButtonGroup = group;
            b.Pressed += () => OpenTab(tab.Id);
            _tabsRow.AddChild(b);
            _tabButtons[tab.Id] = b;
        }
        string open = _openTab.GetValueOrDefault(category.Id) ?? tabs.FirstOrDefault()?.Id ?? "";
        OpenTab(open);
    }

    /// <summary>The open category again (its items changed), keeping the search.</summary>
    private void Reopen()
    {
        string query = _search.Text;
        Open(_category!);
        if (query != "") SetSearch(query);
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
            cards = tab is null ? [] : _lib.ItemsOf(tab).Select(i => new ItemCard(i, tab.Label)).ToList();
            _info.Text = "";
        }
        else
        {
            cards = _lib.ItemsOf(_category)
                .Where(i => i.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(i => new ItemCard(i, _lib.TabOf(i)?.Label, inSearch: true)).ToList();
            _info.Text = cards.Count switch { 0 => "No results", 1 => "1 result", var n => $"{n} results" };
        }

        var picked = Picked;
        foreach (var card in cards)
        {
            card.SetPressedNoSignal(card.Item == picked);
            card.Pressed += () => Pick(card.Item);
            _cards.AddChild(card);
        }
    }

    /// <summary>A text tab: dim, white on hover, and an accent underline when open.</summary>
    private static Button Tab(string text)
    {
        var b = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.None };
        var plain = new StyleBoxEmpty { ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 6, ContentMarginBottom = 8 };
        var open = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = UiTheme.Accent, BorderWidthBottom = 2 };
        open.ContentMarginLeft = open.ContentMarginRight = 9;
        open.ContentMarginTop = 6;
        open.ContentMarginBottom = 8;
        b.AddThemeStyleboxOverride("normal", plain);
        b.AddThemeStyleboxOverride("hover", plain);
        b.AddThemeStyleboxOverride("pressed", open);
        b.AddThemeStyleboxOverride("hover_pressed", open);
        b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        b.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        return b;
    }
}
