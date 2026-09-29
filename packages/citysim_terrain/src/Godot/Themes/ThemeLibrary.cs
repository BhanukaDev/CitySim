using System.Collections.Generic;
using Godot;

namespace CitySim.TerrainSystem.Themes;

/// <summary>
/// Finds the terrain themes: every <c>&lt;root&gt;/&lt;id&gt;/theme.tres</c> under <see cref="Roots"/>: the package's built-in
/// themes, then the project's own <c>res://themes</c>. A project theme with a built-in theme's id replaces it. Mods will add
/// their own folders later.
/// </summary>
public static class ThemeLibrary
{
    public const string BuiltInRoot = TerrainPaths.Root + "/themes";
    public const string ProjectRoot = "res://themes";
    /// <summary>Where themes are searched, in order (later roots override earlier ones by id).</summary>
    public static readonly string[] Roots = [BuiltInRoot, ProjectRoot];
    public const string DefaultId = "default";

    private static List<TerrainTheme>? _all;

    /// <summary>Every theme found, the default first, then by name.</summary>
    public static IReadOnlyList<TerrainTheme> All => _all ??= Scan();

    /// <summary>The theme with this id, or the default theme (null only when no theme exists at all).</summary>
    public static TerrainTheme? Get(string? id)
    {
        foreach (var t in All)
            if (t.Id == id) return t;
        foreach (var t in All)
            if (t.Id == DefaultId) return t;
        return All.Count > 0 ? All[0] : null;
    }

    /// <summary>Loads a theme again from disk (after it was edited and baked in Godot) and returns the fresh copy.</summary>
    public static TerrainTheme? Reload(string id)
    {
        string dir = Get(id)?.Dir ?? $"{BuiltInRoot}/{id}";
        _all = null;
        // The baked arrays aren't dependencies of theme.tres, so they're refreshed by hand (Terrain.ApplyTheme would get the
        // cached copies from before the bake). materials.gdshaderinc needs nothing: Terrain inlines it fresh from disk.
        foreach (var file in new[] { "baked/albedo_height.png", "baked/normal.png" })
            if (ResourceLoader.Exists($"{dir}/{file}"))
                ResourceLoader.Load($"{dir}/{file}", cacheMode: ResourceLoader.CacheMode.Replace);
        var path = $"{dir}/{TerrainTheme.FileName}";
        if (ResourceLoader.Exists(path))
            ResourceLoader.Load(path, cacheMode: ResourceLoader.CacheMode.ReplaceDeep);
        return Get(id);
    }

    private static List<TerrainTheme> Scan()
    {
        var themes = new List<TerrainTheme>();
        foreach (var root in Roots)
        {
            if (!DirAccess.DirExistsAbsolute(root)) continue;
            foreach (var dir in DirAccess.GetDirectoriesAt(root))
            {
                var path = $"{root}/{dir}/{TerrainTheme.FileName}";
                if (!ResourceLoader.Exists(path)) continue;
                if (ResourceLoader.Load(path) is TerrainTheme theme)
                {
                    if (string.IsNullOrEmpty(theme.Id)) theme.Id = dir;
                    themes.RemoveAll(t => t.Id == theme.Id);
                    themes.Add(theme);
                }
                else GD.PushWarning($"ThemeLibrary: {path} isn't a TerrainTheme.");
            }
        }
        themes.Sort((a, b) => a.Id == DefaultId ? -1 : b.Id == DefaultId ? 1 : string.CompareOrdinal(a.Label, b.Label));
        return themes;
    }
}
