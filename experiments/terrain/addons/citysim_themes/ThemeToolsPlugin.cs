#if TOOLS
using System.Linq;
using Godot;
using CitySim.TerrainSystem.Themes;

namespace CitySim.Editor;

/// <summary>
/// Editor tools for terrain themes: a Bake and a Validate button at the top of every <see cref="TerrainTheme"/>'s
/// inspector. Bake runs <see cref="ThemeBaker"/> (texture arrays, previews, materials.gdshaderinc) and lets Godot import
/// the results; a running game picks them up with Reload in its Theme panel.
/// </summary>
[Tool]
public partial class ThemeToolsPlugin : EditorPlugin
{
    private ThemeInspector? _inspector;

    public override void _EnterTree()
    {
        _inspector = new ThemeInspector();
        AddInspectorPlugin(_inspector);
    }

    public override void _ExitTree()
    {
        if (_inspector is not null) RemoveInspectorPlugin(_inspector);
        _inspector = null;
    }
}

[Tool]
public partial class ThemeInspector : EditorInspectorPlugin
{
    public override bool _CanHandle(GodotObject obj) => obj is TerrainTheme;

    public override void _ParseBegin(GodotObject obj)
    {
        if (obj is not TerrainTheme theme) return;
        var box = new VBoxContainer();
        var row = new HBoxContainer();
        box.AddChild(row);
        var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(status);

        var bake = new Button
        {
            Text = "Bake theme", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Pack the materials' textures into the theme's texture arrays, make preview swatches and write " +
                          "materials.gdshaderinc. Needed after adding, removing or reordering materials, changing their " +
                          "textures, or filling/emptying an erosion slot.",
        };
        bake.Pressed += () => Bake(theme, status);
        row.AddChild(bake);

        var validate = new Button { Text = "Validate", TooltipText = "Check the theme for problems" };
        validate.Pressed += () => ShowProblems(theme, status);
        row.AddChild(validate);

        ShowProblems(theme, status, quiet: true);
        AddCustomControl(box);
    }

    private static void ShowProblems(TerrainTheme theme, Label status, bool quiet = false)
    {
        var problems = theme.Validate();
        if (problems.Count == 0 && string.IsNullOrEmpty(theme.ResourcePath) == false && !ThemeBaker.IsIncludeCurrent(theme))
            problems.Add("Materials or slots changed since the last bake: press Bake theme.");
        status.Text = problems.Count == 0 ? (quiet ? "" : "No problems found.") : string.Join("\n", problems.Select(p => "• " + p));
        status.Modulate = problems.Count == 0 ? Colors.White : new Color(1f, 0.75f, 0.4f);
    }

    private static void Bake(TerrainTheme theme, Label status)
    {
        if (string.IsNullOrEmpty(theme.ResourcePath))
        {
            status.Text = "Save the theme as themes/<id>/theme.tres first.";
            return;
        }
        int before = theme.Materials.Count;
        var files = ThemeBaker.Bake(theme);
        if (files is null)
        {
            ShowProblems(theme, status);
            if (theme.Validate().Count == 0) status.Text = "Bake failed: see the Output panel.";
            return;
        }
        // Slot materials may have been added to the list: save that too.
        if (theme.Materials.Count != before) ResourceSaver.Save(theme);
        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        fs.Scan();
        status.Modulate = Colors.White;
        status.Text = $"Baked {theme.Materials.Count} materials. Godot is importing them; in a running game press Reload in the Theme panel.";
    }
}
#endif
