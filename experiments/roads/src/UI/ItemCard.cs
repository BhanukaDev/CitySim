using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>A card in the tray: the item's picture and name. Hovering shows its <see cref="DetailCard"/>.</summary>
public partial class ItemCard : Button
{
    public const float CardWidth = 120f;

    private readonly string? _tabName;

    public BuildItem Item { get; }

    /// <param name="tabName">Shown under the name when the card isn't in its own tab (search results).</param>
    public ItemCard(BuildItem item, string? tabName = null)
    {
        Item = item;
        _tabName = tabName;
        ToggleMode = true;
        FocusMode = FocusModeEnum.None;
        TooltipText = item.Label; // non-empty, so Godot asks _MakeCustomTooltip for the real card
        CustomMinimumSize = new Vector2(CardWidth, 138);

        var normal = UiTheme.Box(UiTheme.ButtonBg, 6, 0);
        var hover = UiTheme.Box(UiTheme.ButtonHover, 6, 0);
        var picked = UiTheme.Box(UiTheme.AccentDim, 6, 0);
        picked.SetBorderWidthAll(2);
        picked.BorderColor = UiTheme.Accent;
        AddThemeStyleboxOverride("normal", normal);
        AddThemeStyleboxOverride("hover", hover);
        AddThemeStyleboxOverride("pressed", picked);
        AddThemeStyleboxOverride("hover_pressed", picked);

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        col.AddThemeConstantOverride("separation", 4);
        AddChild(col);

        var pic = item.CreatePicture();
        pic.CustomMinimumSize = new Vector2(0, 70);
        pic.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(pic);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 6);
        col.AddChild(margin);
        var text = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 0);
        margin.AddChild(text);

        var name = UiTheme.Label(item.Label, 13);
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(name);
        if (tabName is not null)
        {
            var where = UiTheme.Label($"in {tabName}", 11, dim: true);
            where.MouseFilter = MouseFilterEnum.Ignore;
            text.AddChild(where);
        }
        if (item.Source != "Base")
        {
            var src = UiTheme.Label(item.Source, 11, dim: true);
            src.MouseFilter = MouseFilterEnum.Ignore;
            text.AddChild(src);
        }
    }

    public override GodotObject _MakeCustomTooltip(string forText) => DetailCard.Create(Item, _tabName);
}
