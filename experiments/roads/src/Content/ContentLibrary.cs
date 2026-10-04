using System.Collections.Generic;
using System.Linq;
using CitySim.Roads;
using Godot;

namespace CitySim.Content;

/// <summary>
/// Every build category, tab and item the game knows, read from resource files at start-up:
/// <list type="bullet">
/// <item><c>res://content/**</c>: the base game (source "Base").</item>
/// <item><c>user://mods/&lt;mod&gt;/**</c>: one folder per mod (source = the folder name), read in name order after the base
///   game. A file whose id matches an earlier one of the same kind replaces it, so a mod can change a base road.</item>
/// </list>
/// Any <c>.tres</c>/<c>.res</c> whose script is <see cref="BuildCategory"/>, <see cref="BuildTab"/>, a
/// <see cref="BuildItem"/> type or <see cref="RoadStyle"/> counts; other files are ignored, so content can sit next to
/// its textures and materials. Tabs whose category is missing and items whose tab is missing are skipped with a warning.
/// </summary>
public sealed class ContentLibrary
{
    public const string BaseRoot = "res://content";
    public const string ModsRoot = "user://mods";

    private readonly Dictionary<string, BuildCategory> _categories = new();
    private readonly Dictionary<string, BuildTab> _tabs = new();
    private readonly Dictionary<string, BuildItem> _items = new();
    private readonly Dictionary<string, RoadStyle> _styles = new();

    public IReadOnlyList<BuildCategory> Categories { get; private set; } = [];
    public IReadOnlyList<BuildTab> Tabs { get; private set; } = [];
    public IReadOnlyList<BuildItem> Items { get; private set; } = [];
    public List<string> Warnings { get; } = new();

    public static ContentLibrary Load()
    {
        var lib = new ContentLibrary();
        lib.Scan(BaseRoot, "Base");
        if (DirAccess.DirExistsAbsolute(ModsRoot))
            foreach (string mod in DirAccess.GetDirectoriesAt(ModsRoot).Order())
                lib.Scan($"{ModsRoot}/{mod}", mod);
        lib.Resolve();
        foreach (var w in lib.Warnings) GD.PushWarning($"Content: {w}");
        GD.Print($"Content: {lib.Categories.Count} categories, {lib.Tabs.Count} tabs, {lib.Items.Count} items");
        return lib;
    }

    /// <summary>Categories that have at least one tab with at least one item, in bar order.</summary>
    public IEnumerable<BuildCategory> CategoriesWithContent() => Categories.Where(c => TabsOf(c).Any(t => ItemsOf(t).Any()));

    public IEnumerable<BuildTab> TabsOf(BuildCategory c) => Tabs.Where(t => t.Category == c.Id);
    public IEnumerable<BuildItem> ItemsOf(BuildTab t) => Items.Where(i => i.Tab == t.Id);
    public IEnumerable<BuildItem> ItemsOf(BuildCategory c) => TabsOf(c).SelectMany(ItemsOf);
    public BuildTab? TabOf(BuildItem i) => _tabs.GetValueOrDefault(i.Tab);
    public BuildItem? Item(string id) => _items.GetValueOrDefault(id);
    /// <summary>A road style by id, falling back to <see cref="RoadStyle.DefaultId"/>; null when there is none.</summary>
    public RoadStyle? Style(string id) => _styles.GetValueOrDefault(id) ?? _styles.GetValueOrDefault(RoadStyle.DefaultId);

    private void Scan(string dir, string source)
    {
        foreach (string sub in DirAccess.GetDirectoriesAt(dir))
            Scan($"{dir}/{sub}", source);
        foreach (string file in DirAccess.GetFilesAt(dir))
        {
            // Exported builds list converted resources as "<name>.tres.remap"; loading the original path still works.
            string name = file.EndsWith(".remap") ? file[..^".remap".Length] : file;
            if (!name.EndsWith(".tres") && !name.EndsWith(".res")) continue;
            string path = $"{dir}/{name}";
            switch (ResourceLoader.Load(path))
            {
                case BuildCategory c: Add(_categories, c.Id, c, path, source, "category"); c.Source = source; break;
                case BuildTab t: Add(_tabs, t.Id, t, path, source, "tab"); t.Source = source; break;
                case BuildItem i: Add(_items, i.Id, i, path, source, "item"); i.Source = source; break;
                case RoadStyle s: Add(_styles, s.Id, s, path, source, "road style"); s.Source = source; break;
            }
        }
    }

    private void Add<T>(Dictionary<string, T> into, string id, T value, string path, string source, string kind) where T : Resource
    {
        if (string.IsNullOrWhiteSpace(id)) { Warnings.Add($"{path}: {kind} has no Id, skipped"); return; }
        if (into.TryGetValue(id, out var old))
        {
            string oldSource = old switch { BuildCategory c => c.Source, BuildTab t => t.Source, BuildItem i => i.Source, RoadStyle s => s.Source, _ => "?" };
            if (oldSource == source) Warnings.Add($"{path}: {kind} id \"{id}\" is used twice in {source}; the later file wins");
            else GD.Print($"Content: {source} replaces {kind} \"{id}\" from {oldSource}");
        }
        into[id] = value;
    }

    private void Resolve()
    {
        foreach (var t in _tabs.Values.Where(t => !_categories.ContainsKey(t.Category)).ToList())
        {
            Warnings.Add($"tab \"{t.Id}\" ({t.Source}) points at unknown category \"{t.Category}\", skipped");
            _tabs.Remove(t.Id);
        }
        foreach (var i in _items.Values.Where(i => !_tabs.ContainsKey(i.Tab)).ToList())
        {
            Warnings.Add($"item \"{i.Id}\" ({i.Source}) points at unknown tab \"{i.Tab}\", skipped");
            _items.Remove(i.Id);
        }
        Categories = _categories.Values.OrderBy(c => c.Order).ThenBy(c => c.Label).ToList();
        Tabs = _tabs.Values.OrderBy(t => t.Order).ThenBy(t => t.Label).ToList();
        Items = _items.Values.OrderBy(i => i.Order).ThenBy(i => i.Label).ToList();
    }
}
