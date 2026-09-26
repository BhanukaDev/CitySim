using System;
using System.IO;
using Godot;
using CitySim.App;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;

namespace CitySim.UI;

/// <summary>File dialogs and load/save glue between the menus, <see cref="MapFile"/> and <see cref="MapSession"/>.</summary>
public static class MapFiles
{
    /// <summary>Default folder for maps: <c>user://maps</c> (on macOS under ~/Library/Application Support/Godot/app_userdata).</summary>
    public static string MapsDir
    {
        get
        {
            string dir = ProjectSettings.GlobalizePath("user://maps");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Default folder for heightmap images: <c>user://heightmaps</c>.</summary>
    public static string HeightmapsDir
    {
        get
        {
            string dir = ProjectSettings.GlobalizePath("user://heightmaps");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly string[] HeightmapFilters = ["*.png ; 16-bit PNG", "*.r16, *.raw ; 16-bit RAW"];

    /// <summary>A native open/save dialog for .csmap files that frees itself when closed. Add it to the tree, then call PopupCentered.</summary>
    public static FileDialog Dialog(bool save, Action<string> picked) =>
        Dialog(save ? "Save Map" : "Load Map", save, [$"*.{MapFile.Extension} ; CitySim maps"], MapsDir, picked);

    /// <summary>A dialog for heightmap images (PNG or RAW). Export and import share the folder.</summary>
    public static FileDialog HeightmapDialog(bool save, Action<string> picked) =>
        Dialog(save ? "Export Heightmap" : "Import Heightmap", save, HeightmapFilters, HeightmapsDir, picked);

    private static FileDialog Dialog(string title, bool save, string[] filters, string dir, Action<string> picked)
    {
        var d = new FileDialog
        {
            Title = title,
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = filters,
            CurrentDir = dir,
            UseNativeDialog = true,
            ProcessMode = Node.ProcessModeEnum.Always,
            Size = new Vector2I(900, 560),
        };
        if (save && dir == MapsDir && MapSession.CurrentPath is { } current) d.CurrentPath = current;
        // Freed once used; QueueFree is deferred, so the callback still runs on a live node.
        d.FileSelected += path => { picked(path); d.QueueFree(); };
        d.Canceled += d.QueueFree;
        return d;
    }

    /// <summary>Reads a map and queues it for the map scene. Returns an error message, or null on success.</summary>
    public static string? QueueLoad(string path)
    {
        try
        {
            var (map, splat) = MapFile.Load(path);
            MapSession.Pending = new LoadedMapRequest(map, splat, path);
            MapSession.CurrentPath = path;
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PushError($"Load failed ({path}): {e.Message}");
            return $"Couldn't load {Path.GetFileName(path)}: {e.Message}";
        }
    }

    /// <summary>Saves the terrain's map. Returns a status message for the UI.</summary>
    public static string Save(Terrain terrain, string path)
    {
        if (terrain.Map is null || terrain.Splat is null) return "Nothing to save.";
        if (!path.EndsWith("." + MapFile.Extension, StringComparison.OrdinalIgnoreCase))
            path += "." + MapFile.Extension;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MapFile.Save(path, terrain.Map, terrain.Splat);
            MapSession.CurrentPath = path;
            GD.Print($"Saved {path} ({new FileInfo(path).Length / 1024} KB, {sw.ElapsedMilliseconds} ms)");
            return $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError($"Save failed ({path}): {e.Message}");
            return $"Couldn't save: {e.Message}";
        }
    }

    /// <summary>Reads a heightmap image. Returns the image, or null with an error message.</summary>
    public static HeightmapImage? ReadHeightmap(string path, out string? error)
    {
        try
        {
            error = null;
            return HeightmapImage.Read(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PushError($"Heightmap import failed ({path}): {e.Message}");
            error = $"Couldn't read {Path.GetFileName(path)}: {e.Message}";
            return null;
        }
    }

    /// <summary>Queues a new map built from a heightmap image (stretched over the map) for the map scene.</summary>
    public static void QueueImport(HeightmapImage image, string name, int cells, float minHeight, float maxHeight, bool showGenerator = false)
    {
        MapSession.Pending = new GeneratedMapRequest(new GenSettings
        {
            Cells = cells,
            CellSize = Terrain.DefaultCellSize,
            Source = TerrainSource.Heightmap,
            Image = image,
            ImageName = name,
            Placement = new ImagePlacement { Lowest = minHeight, Highest = maxHeight },
        }, showGenerator);
        MapSession.CurrentPath = null;
    }

    /// <summary>Exports the terrain's heights as a 16-bit PNG or RAW (by extension, PNG if none). Returns a status message.</summary>
    public static string ExportHeightmap(Terrain terrain, string path)
    {
        if (terrain.Map is null) return "Nothing to export.";
        if (Path.GetExtension(path) == "") path += ".png";
        try
        {
            var image = HeightmapImage.FromHeightMap(terrain.Map, out var range);
            image.Write(path, range);
            string msg = $"Exported {Path.GetFileName(path)} ({image.Width}×{image.Height}, {range.Min:0.#}–{range.Max:0.#} m)";
            GD.Print($"{msg} to {path}");
            return msg;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError($"Heightmap export failed ({path}): {e.Message}");
            return $"Couldn't export: {e.Message}";
        }
    }
}
