using Godot;
using CitySim.App;
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
    private GeneratorPanel? _generator;
    private Button? _generatorButton;
    private ErosionPanel? _erosion;
    private Button? _erosionButton;
    private ThemePanel? _themePanel;
    private Button? _themeButton;
    private WaterPanel? _waterPanel;
    private Button? _waterButton;
    private Label _toast = null!;
    private int _toastId;

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
        _toolPanel.ToolPressed += def =>
        {
            if (Tools is null) return;
            if (def.Layer >= 0) Tools.PaintLayer = def.Layer;
            Tools.Tool = def.Tool;
        };
        _toolPanel.CloseRequested += Close;
        root.AddChild(_toolPanel);

        _config = new ConfigPanel { Visible = false };
        root.AddChild(_config);

        // Map Editor only: the terrain generator (presets, shapes, heightmap placement, live preview).
        if (MapSession.Mode == AppMode.MapEditor)
        {
            _generator = new GeneratorPanel { Tools = Tools };
            _generator.Closed += () => _generatorButton?.SetPressedNoSignal(false);
            root.AddChild(_generator);
            _generatorButton = _bar.AddToggle("Generate", "Terrain generator: presets, island/coast shapes, heightmap placement", () =>
            {
                if (_generator.Visible) _generator.Close(); else OpenGenerator();
            });

            _erosion = new ErosionPanel { Tools = Tools };
            _erosion.Closed += () => _erosionButton?.SetPressedNoSignal(false);
            root.AddChild(_erosion);
            _erosionButton = _bar.AddToggle("Erode", "Rain erosion and lakes", () =>
            {
                if (_erosion.Visible) _erosion.Close(); else OpenErosion();
            });

            _waterPanel = new WaterPanel { Tools = Tools };
            _waterPanel.Closed += () => _waterButton?.SetPressedNoSignal(false);
            root.AddChild(_waterPanel);
            _waterButton = _bar.AddToggle("Water", "Water simulation: run/pause, speed, evaporation, fill hollows (place sources in Terrain → Water)", () =>
            {
                if (_waterPanel.Visible) _waterPanel.Close(); else OpenWater();
            });

            _themePanel = new ThemePanel { Tools = Tools };
            _themePanel.Closed += () => _themeButton?.SetPressedNoSignal(false);
            root.AddChild(_themePanel);
            _themeButton = _bar.AddToggle("Theme", "The map's terrain theme (made in Godot): pick one, see its materials", () =>
            {
                if (_themePanel.Visible) _themePanel.Close(); else OpenTheme();
            });
        }

        _toast = new Label
        {
            Visible = false,
            ProcessMode = ProcessModeEnum.Always,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _toast.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.KeepSize, 16);
        _toast.GrowHorizontal = Control.GrowDirection.Both;
        _toast.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.PanelBg, 6, 8));

        var pause = new PauseMenu { Tools = Tools, Generator = _generator, Erosion = _erosion, ThemePanel = _themePanel, Water = _waterPanel };
        pause.Notify += ShowToast;
        pause.GeneratorRequested += OpenGenerator;
        root.AddChild(pause);
        root.AddChild(_toast);

        if (Tools is not null)
        {
            _config.Bind(Tools);
            Tools.StateChanged += OnToolsChanged;
            if (Tools.Terrain is { } terrain) terrain.ThemeChanged += OnThemeChanged;
        }
        OnToolsChanged();

        if (_generator is not null && Tools?.Terrain is { ShowGeneratorOnStart: true })
            Callable.From(OpenGenerator).CallDeferred();
    }

    /// <summary>Shows the generator panel; tools are put away while it's open so its live updates and strokes don't mix.</summary>
    public void OpenGenerator()
    {
        if (_generator is null) return;
        if (_open is not null) Close();
        _erosion?.Close();
        _themePanel?.Close();
        _waterPanel?.Close();
        _generator.Open();
        _generatorButton?.SetPressedNoSignal(true);
    }

    /// <summary>Shows the erosion panel (closes the tools and the generator, which share its side of the screen).</summary>
    public void OpenErosion()
    {
        if (_erosion is null) return;
        if (_open is not null) Close();
        _generator?.Close();
        _themePanel?.Close();
        _waterPanel?.Close();
        _erosion.Open();
        _erosionButton?.SetPressedNoSignal(true);
    }

    /// <summary>Shows the theme panel (closes the tools and the other side panels).</summary>
    public void OpenTheme()
    {
        if (_themePanel is null) return;
        if (_open is not null) Close();
        _generator?.Close();
        _erosion?.Close();
        _waterPanel?.Close();
        _themePanel.Open();
        _themeButton?.SetPressedNoSignal(true);
    }

    /// <summary>
    /// Shows the water panel. It shares the right side with the other panels but not the tools: sources are placed with
    /// the Water tab while it's open.
    /// </summary>
    public void OpenWater()
    {
        if (_waterPanel is null) return;
        _generator?.Close();
        _erosion?.Close();
        _themePanel?.Close();
        _waterPanel.Open();
        _waterButton?.SetPressedNoSignal(true);
    }

    /// <summary>The Paint tab lists the theme's materials, so it's rebuilt when the theme changes.</summary>
    private void OnThemeChanged()
    {
        if (_open is { } cat) ShowCategory(cat);
    }

    private void ShowCategory(ToolCategory cat) =>
        _toolPanel.ShowCategory(ToolCatalog.ForMap(cat, MapSession.Mode, Tools?.Terrain?.Theme));

    private void Open(ToolCategory cat)
    {
        _generator?.Close();
        _erosion?.Close();
        _themePanel?.Close();
        _open = cat;
        _bar.SetActive(cat);
        _toolPanel.SetSelectedTool(Tools?.Tool ?? TerrainTool.None, Tools?.PaintLayer ?? -1);
        ShowCategory(cat);
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

    /// <summary>Shows a short message at the top of the screen for a few seconds.</summary>
    public void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visible = true;
        int id = ++_toastId;
        GetTree().CreateTimer(2.5, processAlways: true).Timeout += () => { if (id == _toastId) _toast.Visible = false; };
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
        _toolPanel.SetSelectedTool(tool, Tools?.PaintLayer ?? -1);
        _config.Visible = _open is not null && tool != TerrainTool.None;
        _config.Refresh();
    }
}
