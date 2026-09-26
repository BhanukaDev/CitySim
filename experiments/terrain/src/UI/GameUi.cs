using Godot;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Root of the in-game UI: bottom bar, tool panel (centre, above the bar) and config panel (bottom-left).
/// Panels stop mouse events, so clicks on them never reach the sculpt tools.
/// </summary>
public partial class GameUi : CanvasLayer
{
    [Export] public TerrainToolController? Tools { get; set; }

    private BottomBar _bar = null!;
    private ToolPanel _toolPanel = null!;
    private ConfigPanel _config = null!;
    private ToolCategory? _open;

    public override void _Ready()
    {
        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _bar = new BottomBar();
        _bar.AddCategories(ToolCatalog.All);
        _bar.CategoryPressed += cat => { if (_open == cat) Close(); else Open(cat); };
        root.AddChild(_bar);

        _toolPanel = new ToolPanel { Visible = false };
        _toolPanel.ToolPressed += def => { if (Tools is not null) Tools.Tool = def.Tool; };
        _toolPanel.CloseRequested += Close;
        root.AddChild(_toolPanel);

        _config = new ConfigPanel { Visible = false };
        root.AddChild(_config);

        if (Tools is not null)
        {
            _config.Bind(Tools);
            Tools.StateChanged += OnToolsChanged;
        }
        OnToolsChanged();
    }

    private void Open(ToolCategory cat)
    {
        _open = cat;
        _bar.SetActive(cat);
        _toolPanel.ShowCategory(cat);
        _toolPanel.Visible = true;
        OnToolsChanged();
    }

    private void Close()
    {
        _open = null;
        _bar.SetActive(null);
        _toolPanel.Visible = false;
        if (Tools is not null) Tools.Tool = TerrainTool.None;
        OnToolsChanged();
    }

    private void OnToolsChanged()
    {
        var tool = Tools?.Tool ?? TerrainTool.None;
        // A terrain tool selected some other way (e.g. a script) opens its panel.
        if (tool != TerrainTool.None && _open is null)
        {
            Open(ToolCatalog.Terrain);
            return;
        }
        _toolPanel.SetSelectedTool(tool);
        _config.Visible = _open is not null && tool != TerrainTool.None;
        _config.Refresh();
    }
}
