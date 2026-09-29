using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// A plain Settings window (the prototype of the game's settings menu): a Graphics tab for <see cref="TerrainGraphics"/>,
/// saved to the player's file on every change, and optionally a Tuning tab for <see cref="TerrainTuning"/> (developer
/// knobs, live) with Save as defaults, which writes its .tres. The rows are built from each resource's exported
/// properties (<see cref="ResourceForm"/>), so a new setting needs no UI code. Works while the tree is paused.
/// </summary>
public partial class SettingsPanel : PanelContainer
{
    public event Action? Closed;

    private readonly Label _status;
    private readonly TabContainer _tabs;

    public SettingsPanel(TerrainGraphics? graphics, TerrainTuning? tuning, bool showTuning)
    {
        Name = "SettingsPanel";
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(460, 520);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        header.AddChild(new Label { Text = "Settings", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        var tabs = _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(tabs);
        _status = new Label { Modulate = new Color(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_status);

        if (graphics is not null)
        {
            var form = new ResourceForm(graphics);
            // Every change is the player's choice: keep it (the package's .tres is never written from here).
            form.ValueChanged += () => Report(graphics.Save(), $"Saved to {TerrainGraphics.UserPath}.");
            tabs.AddChild(Page("Graphics", form));
        }
        if (showTuning && tuning is not null)
        {
            var form = new ResourceForm(tuning);
            var page = Page("Tuning", form);
            var save = new Button { Text = "Save as defaults", FocusMode = FocusModeEnum.None, TooltipText = $"Write these values to {tuning.ResourcePath}" };
            save.Pressed += () => Report(ResourceSaver.Save(tuning), $"Saved to {tuning.ResourcePath}.");
            page.AddChild(save);
            tabs.AddChild(page);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Shows the tab with this name ("Graphics", "Tuning"), if there is one.</summary>
    public void SelectTab(string name)
    {
        for (int i = 0; i < _tabs.GetTabCount(); i++)
            if (_tabs.GetTabTitle(i) == name) _tabs.CurrentTab = i;
    }

    public void Close()
    {
        Visible = false;
        Closed?.Invoke();
    }

    private void Report(Error error, string ok) => _status.Text = error == Error.Ok ? ok : $"Couldn't save: {error}.";

    private static VBoxContainer Page(string name, Control form)
    {
        var page = new VBoxContainer { Name = name };
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        form.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(form);
        page.AddChild(scroll);
        return page;
    }
}

/// <summary>
/// Editable rows for a resource's script properties, from its property list: groups become headings, bools check
/// buttons, enums dropdowns, ranged numbers sliders, Vector4s four number boxes. Rows follow changes made elsewhere
/// (e.g. a preset setting other values).
/// </summary>
public partial class ResourceForm : VBoxContainer
{
    /// <summary>Raised after the user changed a value (not for changes made elsewhere).</summary>
    public event Action? ValueChanged;

    private readonly Resource _resource;
    private readonly List<Action> _refreshers = new();
    private bool _syncing;

    public ResourceForm(Resource resource)
    {
        _resource = resource;
        GridContainer? grid = null;
        foreach (var p in ((Script)resource.GetScript()).GetScriptPropertyList())
        {
            string name = p["name"].AsString();
            var usage = (PropertyUsageFlags)p["usage"].AsInt64();
            if ((usage & PropertyUsageFlags.Group) != 0)
            {
                AddChild(new Label { Text = name, Modulate = new Color(1, 1, 1, 0.6f) });
                grid = null;
                continue;
            }
            if ((usage & PropertyUsageFlags.Editor) == 0 || (usage & (PropertyUsageFlags.Category | PropertyUsageFlags.Subgroup)) != 0) continue;
            if (Editor(name, (Variant.Type)p["type"].AsInt32(), (PropertyHint)p["hint"].AsInt32(), p["hint_string"].AsString()) is not { } editor)
                continue;
            if (grid is null)
            {
                grid = new GridContainer { Columns = 2 };
                grid.AddThemeConstantOverride("h_separation", 12);
                AddChild(grid);
            }
            grid.AddChild(new Label { Text = Pretty(name), CustomMinimumSize = new Vector2(170, 0) });
            editor.SizeFlagsHorizontal = editor is CheckButton ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
            grid.AddChild(editor);
        }
        resource.Changed += Refresh;
        TreeExiting += () => resource.Changed -= Refresh;
    }

    private void Refresh()
    {
        _syncing = true;
        foreach (var r in _refreshers) r();
        _syncing = false;
    }

    private void Write(string name, Variant value)
    {
        if (_syncing) return;
        _resource.Set(name, value);
        ValueChanged?.Invoke();
    }

    private Control? Editor(string name, Variant.Type type, PropertyHint hint, string hintString)
    {
        switch (type)
        {
            case Variant.Type.Bool:
            {
                var check = new CheckButton { FocusMode = FocusModeEnum.None };
                check.Toggled += on => Write(name, on);
                _refreshers.Add(() => check.ButtonPressed = _resource.Get(name).AsBool());
                _refreshers[^1]();
                return check;
            }
            case Variant.Type.Int when hint == PropertyHint.Enum:
            {
                var option = new OptionButton { FocusMode = FocusModeEnum.None };
                foreach (var item in hintString.Split(','))
                {
                    var parts = item.Split(':');
                    option.AddItem(parts[0], parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : option.ItemCount);
                }
                option.ItemSelected += i => Write(name, option.GetItemId((int)i));
                _refreshers.Add(() => option.Select(option.GetItemIndex(_resource.Get(name).AsInt32())));
                _refreshers[^1]();
                return option;
            }
            case Variant.Type.Int or Variant.Type.Float:
            {
                var (min, max, step, suffix) = ParseRange(hintString, type == Variant.Type.Int);
                var row = new HBoxContainer();
                var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, FocusMode = FocusModeEnum.None,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                var value = new Label { CustomMinimumSize = new Vector2(80, 0), HorizontalAlignment = HorizontalAlignment.Right };
                slider.ValueChanged += v =>
                {
                    value.Text = Format(v, suffix);
                    Write(name, type == Variant.Type.Int ? Variant.From((int)Math.Round(v)) : Variant.From((float)v));
                };
                _refreshers.Add(() =>
                {
                    double v = _resource.Get(name).AsDouble();
                    slider.SetValueNoSignal(v);
                    value.Text = Format(v, suffix);
                });
                _refreshers[^1]();
                row.AddChild(slider);
                row.AddChild(value);
                return row;
            }
            case Variant.Type.Vector4:
            {
                var row = new HBoxContainer();
                var boxes = new SpinBox[4];
                for (int i = 0; i < 4; i++)
                {
                    boxes[i] = new SpinBox { MinValue = 0, MaxValue = 1000, Step = 0.1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    boxes[i].ValueChanged += _ => Write(name, new Vector4((float)boxes[0].Value, (float)boxes[1].Value, (float)boxes[2].Value, (float)boxes[3].Value));
                    row.AddChild(boxes[i]);
                }
                _refreshers.Add(() =>
                {
                    var v = _resource.Get(name).AsVector4();
                    boxes[0].SetValueNoSignal(v.X);
                    boxes[1].SetValueNoSignal(v.Y);
                    boxes[2].SetValueNoSignal(v.Z);
                    boxes[3].SetValueNoSignal(v.W);
                });
                _refreshers[^1]();
                return row;
            }
            default:
                return null;
        }
    }

    private static (double Min, double Max, double Step, string Suffix) ParseRange(string hint, bool isInt)
    {
        double min = 0, max = isInt ? 100 : 1, step = isInt ? 1 : 0.01;
        string suffix = "";
        var parts = hint.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<double>();
        foreach (var part in parts)
        {
            if (part.StartsWith("suffix:")) suffix = part["suffix:".Length..];
            else if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) numbers.Add(n);
        }
        if (numbers.Count >= 2) (min, max) = (numbers[0], numbers[1]);
        if (numbers.Count >= 3) step = numbers[2];
        return (min, max, step, suffix);
    }

    private static string Format(double v, string suffix) =>
        (Math.Abs(v - Math.Round(v)) < 1e-6 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture))
        + (suffix.Length > 0 ? " " + suffix : "");

    /// <summary>"WaterMaxCells" → "Water max cells".</summary>
    private static string Pretty(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
