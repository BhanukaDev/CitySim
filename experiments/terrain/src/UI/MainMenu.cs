using System;
using System.Globalization;
using Godot;
using CitySim.TerrainSystem.Themes;
using CitySim.App;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CitySim.UI;

/// <summary>
/// Start screen: New Map (generator preset, flat, or a heightmap image, with a size), Load Map and Quit. Generator and
/// heightmap maps open the Map Editor with the generator panel showing, where the details are set with a live preview.
/// Deliberately plain; the experiment is a tool, not the game. Any command-line flag skips it: --flat[=height] starts an
/// empty map, --preset=name (e.g. island, coast) and --seed=n pick generator settings, --show-generator opens the panel,
/// --size=cells sets the size (8192 = 28.7 km), --load=path opens a file, --heightmap=path[,min,max] imports a 16-bit PNG/RAW (range defaults to the one stored in
/// the PNG, else 0–250 m; combines with --preset for a shape), --game starts in game mode, anything else
/// (--screenshot, --demo-*) the generated default map.
/// </summary>
public partial class MainMenu : Control
{
    public const string ScenePath = "res://scenes/Menu.tscn";
    public const string MapScenePath = "res://scenes/Main.tscn";

    private const float DefaultFlatHeight = 40f;
    private const float DefaultImportMin = 0f, DefaultImportMax = 250f;

    private Control _main = null!, _newMap = null!;
    private Label _status = null!, _formStatus = null!;

    // New map being generated on a worker before the scene change.
    private Task<HeightMap>? _generating;
    private GeneratedMapRequest? _generatingRequest;
    private float _progress;
    private readonly Stopwatch _generateTime = new();

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Length > 0)
        {
            ApplyFlags();
            // Deferred: changing scene inside _Ready of the current scene isn't allowed.
            Callable.From(() => GetTree().ChangeSceneToFile(MapScenePath)).CallDeferred();
            return;
        }

        Theme = UiTheme.Theme;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = UiTheme.BarBg, AnchorRight = 1, AnchorBottom = 1 });

        var main = Column("CitySim · Terrain Lab");
        AddButton(main, "New Map", () => { _main.Visible = false; _newMap.Visible = true; });
        AddButton(main, "Load Map", Load);
        AddButton(main, "Quit", () => GetTree().Quit());
        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = UiTheme.TextDim };
        main.AddChild(_status);
        AddChild(_main = Centered(main));
        AddChild(_newMap = Centered(NewMapForm()));
        _newMap.Visible = false;
    }

    private static void ApplyFlags()
    {
        GenSettings? gen = null;
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--game")
                MapSession.Mode = AppMode.Game;
            else if (arg == "--flat" || arg.StartsWith("--flat="))
            {
                float h = arg.Length > "--flat=".Length ? float.Parse(arg["--flat=".Length..], CultureInfo.InvariantCulture) : DefaultFlatHeight;
                gen = (gen ?? new GenSettings()) with { Source = TerrainSource.Flat, FlatHeight = h };
            }
            else if (arg.StartsWith("--preset="))
            {
                if (GenPresets.Find(arg["--preset=".Length..]) is { } preset) gen = preset.ApplyTo(gen ?? new GenSettings());
                else GD.PushError($"Unknown preset '{arg["--preset=".Length..]}'");
            }
            else if (arg.StartsWith("--seed=") && int.TryParse(arg["--seed=".Length..], out int seed))
                gen = (gen ?? new GenSettings()) with { Noise = (gen ?? new GenSettings()).Noise with { Seed = seed } };
            else if (arg == "--show-generator")
                gen ??= new GenSettings();
            else if (arg.StartsWith("--size=") && int.TryParse(arg["--size=".Length..], out int cells))
                gen = (gen ?? new GenSettings()) with { Cells = cells };
            else if (arg.StartsWith("--load=") && MapFiles.QueueLoad(arg["--load=".Length..]) is { } error)
                GD.PushError(error);
            else if (arg.StartsWith("--heightmap="))
            {
                // --heightmap=path[,min,max]
                string[] parts = arg["--heightmap=".Length..].Split(',');
                if (MapFiles.ReadHeightmap(parts[0], out string? err) is not { } image) { GD.PushError(err); continue; }
                var (min, max) = parts.Length == 3
                    ? (float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture))
                    : image.Range ?? (DefaultImportMin, DefaultImportMax);
                MapFiles.QueueImport(image, System.IO.Path.GetFileName(parts[0]), 1024, min, max);
            }
        }
        // --preset / --seed / --flat can combine with a heightmap (e.g. an island mask over it).
        if (gen is not null)
        {
            if (MapSession.Pending is GeneratedMapRequest { Settings: var imported }) gen = gen with
            {
                Source = imported.Source, Image = imported.Image, ImageName = imported.ImageName, Placement = imported.Placement,
            };
            bool show = OS.GetCmdlineUserArgs().Contains("--show-generator");
            MapSession.Pending = new GeneratedMapRequest(gen, show);
            MapSession.CurrentPath = null;
        }
    }

    private VBoxContainer NewMapForm()
    {
        var column = Column("New Map");
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 8);
        column.AddChild(grid);

        var size = new OptionButton();
        foreach (var z in MapSize.All) size.AddItem(z.Label);
        size.Selected = 1;
        Row(grid, "Size", size);

        var theme = new OptionButton { TooltipText = "The map's look (terrain theme); it can be changed later in the editor" };
        foreach (var t in ThemeLibrary.All) theme.AddItem(t.Label);
        Row(grid, "Theme", theme);

        var type = new OptionButton();
        type.AddItem("Generator");
        type.AddItem("Flat (empty)");
        type.AddItem("Heightmap image");
        Row(grid, "Start from", type);

        var preset = new OptionButton();
        foreach (var p in GenPresets.All) preset.AddItem(p.Name);
        var presetLabel = Row(grid, "Preset", preset);

        var height = new SpinBox { MinValue = 0, MaxValue = 500, Step = 1, Value = DefaultFlatHeight, Suffix = "m" };
        var heightLabel = Row(grid, "Height", height);

        // Heightmap import: a 16-bit PNG or RAW (World Machine, Gaea, DEMs). Height range, rotation, scale, tiling and
        // shapes are set in the Map Editor's generator panel, which opens with it.
        HeightmapImage? image = null;
        string imageName = "";
        var fileRow = new HBoxContainer();
        var fileName = new Label { Text = "No file", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Modulate = UiTheme.TextDim };
        fileRow.AddChild(fileName);
        var browse = new Button { Text = "Browse…", TooltipText = "16-bit greyscale PNG, or square 16-bit RAW (.r16/.raw, little-endian)" };
        fileRow.AddChild(browse);
        var fileLabel = Row(grid, "File", fileRow);
        browse.Pressed += () =>
        {
            var dialog = MapFiles.HeightmapDialog(save: false, path =>
            {
                image = MapFiles.ReadHeightmap(path, out string? error);
                _formStatus.Text = error ?? "";
                if (image is null) { fileName.Text = "No file"; return; }
                imageName = System.IO.Path.GetFileName(path);
                fileName.Text = $"{imageName} ({image.Width}×{image.Height}, {image.BitDepth}-bit)";
                fileName.TooltipText = path;
            });
            AddChild(dialog);
            dialog.PopupCentered();
        };

        var hint = new Label { Modulate = UiTheme.TextDim, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) };
        column.AddChild(hint);

        void UpdateRows()
        {
            bool gen = type.Selected == 0, flat = type.Selected == 1, import = type.Selected == 2;
            presetLabel.Visible = preset.Visible = gen;
            heightLabel.Visible = height.Visible = flat;
            fileLabel.Visible = fileRow.Visible = import;
            hint.Text = gen ? GenPresets.All[preset.Selected].Description + ". Fine-tune it with a live preview in the editor."
                      : import ? "Height range, rotation, scale and tiling are set in the editor, with a live preview."
                      : "";
            hint.Visible = hint.Text != "";
        }
        type.ItemSelected += _ => { UpdateRows(); _formStatus.Text = ""; };
        preset.ItemSelected += _ => UpdateRows();
        UpdateRows();

        AddButton(column, "Create", () =>
        {
            int cells = MapSize.All[size.Selected].Cells;
            MapSession.NewMapTheme = theme.Selected >= 0 && theme.Selected < ThemeLibrary.All.Count ? ThemeLibrary.All[theme.Selected].Id : null;
            var settings = new GenSettings { Cells = cells, CellSize = Terrain.DefaultCellSize };
            switch (type.Selected)
            {
                case 2:
                    if (image is null) { _formStatus.Text = "Pick a heightmap file first."; return; }
                    var (min, max) = image.Range ?? (DefaultImportMin, DefaultImportMax);
                    MapFiles.QueueImport(image, imageName, cells, min, max, showGenerator: true);
                    break;
                case 1:
                    MapSession.Pending = new GeneratedMapRequest(settings with { Source = TerrainSource.Flat, FlatHeight = (float)height.Value });
                    break;
                default:
                    var s = GenPresets.All[preset.Selected].ApplyTo(settings);
                    MapSession.Pending = new GeneratedMapRequest(s with { Noise = s.Noise with { Seed = Random.Shared.Next(0, int.MaxValue) } }, ShowGenerator: true);
                    break;
            }
            MapSession.CurrentPath = null;
            StartGenerating();
        });
        AddButton(column, "Back", () => { _newMap.Visible = false; _main.Visible = true; });
        _formStatus = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = UiTheme.TextDim, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_formStatus);
        return column;
    }

    /// <summary>Generates the pending map on a worker; <see cref="_Process"/> shows progress and opens it when done.</summary>
    private void StartGenerating()
    {
        if (MapSession.TakePending() is not GeneratedMapRequest request) return;
        _generatingRequest = request;
        _progress = 0f;
        _generateTime.Restart();
        _newMap.PropagateCall(BaseButton.MethodName.SetDisabled, [true]);
        _generating = Task.Run(() => TerrainGen.Create(request.Settings, CancellationToken.None, f => Volatile.Write(ref _progress, f)));
    }

    public override void _Process(double delta)
    {
        if (_generating is not { } task) return;
        if (!task.IsCompleted)
        {
            _formStatus.Text = $"Generating… {Volatile.Read(ref _progress) * 100f:0}%";
            return;
        }
        _generating = null;
        if (task.IsFaulted)
        {
            GD.PushError($"Terrain generation failed: {task.Exception}");
            _formStatus.Text = "Generation failed (see the log).";
            _newMap.PropagateCall(BaseButton.MethodName.SetDisabled, [false]);
            return;
        }
        GD.Print($"MainMenu: {task.Result.Width}² map generated in {_generateTime.ElapsedMilliseconds} ms");
        MapSession.Pending = _generatingRequest! with { Map = task.Result };
        GetTree().ChangeSceneToFile(MapScenePath);
    }

    private static Label Row(GridContainer grid, string name, Control value)
    {
        var label = new Label { Text = name };
        grid.AddChild(label);
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(value);
        return label;
    }

    private void Load()
    {
        var dialog = MapFiles.Dialog(save: false, path =>
        {
            if (MapFiles.QueueLoad(path) is { } error) _status.Text = error;
            else GetTree().ChangeSceneToFile(MapScenePath);
        });
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>A vertical button column with a title, shared with <see cref="PauseMenu"/>.</summary>
    public static VBoxContainer Column(string title)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        column.AddThemeConstantOverride("separation", 8);
        column.AddChild(new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center });
        return column;
    }

    /// <summary>Adds a button; with no action it's shown disabled (a feature that doesn't exist yet).</summary>
    public static Button AddButton(VBoxContainer column, string text, Action? act, string tooltip = "")
    {
        var b = new Button
        {
            Text = text,
            Disabled = act is null,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(0, 36),
        };
        if (act is not null) b.Pressed += act;
        column.AddChild(b);
        return b;
    }

    public static CenterContainer Centered(Control child)
    {
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.AddChild(child);
        return center;
    }
}
