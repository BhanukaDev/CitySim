using System;
using Godot;
using CitySim.App;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Esc menu in the map: Resume, Save, Save As, Load, Terrain Generator (Map Editor), Export Heightmap, Main Menu, Quit. Pauses the tree while open.
/// Esc with a terrain tool selected only deselects the tool. Ctrl/Cmd+S saves at any time
/// (asks for a file the first time).
/// </summary>
public partial class PauseMenu : Control
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>The Map Editor's generator panel (null in game mode). Esc closes it before opening this menu.</summary>
    public GeneratorPanel? Generator { get; set; }

    /// <summary>Raised by "Terrain Generator…".</summary>
    public event Action? GeneratorRequested;

    /// <summary>Shows a short message to the user (e.g. "Saved").</summary>
    public event Action<string>? Notify;

    public PauseMenu()
    {
        Name = "PauseMenu";
        Visible = false;
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelBg, 6, 16));
        var column = MainMenu.Column("Paused");
        MainMenu.AddButton(column, "Resume", Close);
        MainMenu.AddButton(column, "Save Map", Save);
        MainMenu.AddButton(column, "Save Map As…", SaveAs);
        MainMenu.AddButton(column, "Load Map…", Load);
        if (MapSession.Mode == AppMode.MapEditor)
            MainMenu.AddButton(column, "Terrain Generator…", () => { Close(); GeneratorRequested?.Invoke(); },
                "Regenerate the terrain: presets, island and coast shapes, heightmap import with rotate/scale/tile");
        MainMenu.AddButton(column, "Export Heightmap…", ExportHeightmap,
            "16-bit PNG (or .r16 RAW) of the heights, lowest point black, highest white. The PNG remembers the height range.");
        MainMenu.AddButton(column, "Main Menu", () => ChangeScene(MainMenu.ScenePath));
        MainMenu.AddButton(column, "Quit", () => GetTree().Quit());
        panel.AddChild(column);
        AddChild(MainMenu.Centered(panel));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.S && key.IsCommandOrControlPressed())
            Save();
        else if (key.Keycode != Key.Escape)
            return;
        else if (Visible) Close();
        else if (Generator is { Visible: true }) Generator.Close();
        else if (Tools is null || Tools.Tool == TerrainTool.None) Open();
        else return; // let the tool controller deselect the tool
        GetViewport().SetInputAsHandled();
    }

    private void Open()
    {
        Visible = true;
        GetTree().Paused = true;
    }

    private void Close()
    {
        Visible = false;
        GetTree().Paused = false;
    }

    private void Save()
    {
        if (MapSession.CurrentPath is { } path) SaveTo(path);
        else SaveAs();
    }

    private void SaveAs() => Popup(MapFiles.Dialog(save: true, SaveTo));

    private void SaveTo(string path)
    {
        if (Tools?.Terrain is not { } terrain) return;
        Notify?.Invoke(MapFiles.Save(terrain, path));
    }

    private void ExportHeightmap() => Popup(MapFiles.HeightmapDialog(save: true, path =>
    {
        if (Tools?.Terrain is { } terrain) Notify?.Invoke(MapFiles.ExportHeightmap(terrain, path));
    }));

    private void Load() => Popup(MapFiles.Dialog(save: false, path =>
    {
        if (MapFiles.QueueLoad(path) is { } error) Notify?.Invoke(error);
        else
        {
            // The map scene picks up the loaded map on reload, which also resets the tools and camera.
            GetTree().Paused = false;
            GetTree().ReloadCurrentScene();
        }
    }));

    private void Popup(FileDialog dialog)
    {
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private void ChangeScene(string path)
    {
        GetTree().Paused = false;
        GetTree().ChangeSceneToFile(path);
    }
}
