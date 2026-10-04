using CitySim.Content;
using Godot;

namespace CitySim.UI;

/// <summary>
/// The hover card for a tray item: name, a wide picture, its <see cref="BuildItem.Details"/> rows, where it came from,
/// and its description. Shown as the card's tooltip, so it only informs and never acts.
/// </summary>
public static class DetailCard
{
    public const float Width = 270f;

    public static Control Create(BuildItem item, string? tabName = null)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0) };
        col.AddThemeConstantOverride("separation", 6);
        col.AddChild(UiTheme.Label(item.Label, 16));

        var pic = item.CreatePicture();
        pic.CustomMinimumSize = new Vector2(Width, 40);
        col.AddChild(pic);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 2);
        void Row(string label, string value)
        {
            grid.AddChild(UiTheme.Label(label, 13, dim: true));
            grid.AddChild(UiTheme.Label(value, 13));
        }
        foreach (var (label, value) in item.Details()) Row(label, value);
        if (tabName is not null) Row("Tab", tabName);
        Row("Source", item.Source);
        col.AddChild(grid);

        if (item.Description != "")
        {
            var d = UiTheme.Label(item.Description, 13, dim: true);
            d.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            d.CustomMinimumSize = new Vector2(Width, 0);
            col.AddChild(d);
        }
        return col;
    }
}
