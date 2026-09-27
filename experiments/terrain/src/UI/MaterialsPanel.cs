using System;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Look;
using CitySim.Tools;

namespace CitySim.UI;

/// <summary>
/// Map Editor panel (right side): edits the shared <see cref="TerrainLook"/> live. On top, plain cards (beach sand, cliffs,
/// ...) with a few sliders each; collapsed below, the layer colours and the full rule editor (each rule lays its layer
/// over the ones before it, where its conditions hold). Save writes <see cref="TerrainLook.DefaultPath"/>, used by every map.
/// </summary>
public partial class MaterialsPanel : PanelContainer
{
    public TerrainToolController? Tools { get; set; }

    /// <summary>Raised when the panel closes (close button or <see cref="Close"/>).</summary>
    public event Action? Closed;

    private enum View { Normal, SelectedRule, StrongestLayer, Cost }

    private static readonly string[] LayerNames = Array.ConvertAll(TerrainLayers.All, l => l.DisplayName);

    private readonly VBoxContainer _cards = new();
    private ScrollContainer _scroll = null!;
    private readonly VBoxContainer _ruleList = new();
    private readonly GridContainer _editor = NewGrid();
    private readonly GridContainer _layers = NewGrid();
    private readonly OptionButton _view = new() { FocusMode = FocusModeEnum.None };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim };
    private MaterialRule? _selected;
    private bool _dirty;

    private Terrain? Terrain => Tools?.Terrain;
    private TerrainLook? Look => Terrain?.Look;

    public MaterialsPanel()
    {
        Name = "MaterialsPanel";
        Visible = false;
        AnchorLeft = AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 1f;
        OffsetLeft = -416f;
        OffsetRight = -16f;
        OffsetTop = 16f;
        OffsetBottom = -(UiTheme.BarHeight + UiTheme.Gap);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);

        var header = new HBoxContainer();
        var title = new Label { Text = "Materials", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        var close = new Button { Text = "✕", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28), TooltipText = "Close (Esc)" };
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(header);

        var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(body);

        var help = new Label
        {
            Text = "Drag a slider and the map updates. Save keeps the look for every map.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = UiTheme.TextDim,
        };
        help.AddThemeFontSizeOverride("font_size", 12);
        body.AddChild(help);
        _cards.AddThemeConstantOverride("separation", 10);
        body.AddChild(_cards);

        Fold(body, "Colours & blending", "Each layer's colour; its texture only adds light and dark detail").AddChild(_layers);

        var advanced = Fold(body, "Advanced: all rules",
            "The rules behind the cards above. Each rule lays one layer over the rules before it, where its conditions hold.",
            onClose: RebuildCards);
        _ruleList.AddThemeConstantOverride("separation", 2);
        advanced.AddChild(_ruleList);
        var ruleButtons = new HBoxContainer();
        ruleButtons.AddChild(SmallButton("Add Rule", "Add a rule after the selected one", AddRule));
        ruleButtons.AddChild(SmallButton("Duplicate", "Copy the selected rule", DuplicateRule));
        advanced.AddChild(ruleButtons);
        advanced.AddChild(SectionLabel("Selected Rule", ""));
        advanced.AddChild(_editor);
        var viewRow = NewGrid();
        foreach (var v in new[] { "Normal", "Selected rule's coverage", "Strongest layer", "Cost (textures blended)" }) _view.AddItem(v);
        _view.TooltipText = "Debug view: where the selected rule applies (pink), each pixel's strongest layer in a flat colour, " +
                            "or how many textures each pixel blends (green 1 to red 4): soft, wide transitions cost frame time";
        _view.ItemSelected += _ => UpdateView();
        Row(viewRow, "View", _view);
        advanced.AddChild(viewRow);

        var footer = new HBoxContainer();
        footer.AddChild(SmallButton("Save", $"Write {TerrainLook.DefaultPath}; every map uses it", Save, expand: true));
        footer.AddChild(SmallButton("Revert", "Reload the last saved look", Revert, expand: true));
        footer.AddChild(SmallButton("Defaults", "Back to the built-in look (Revert undoes it until you save)", ResetDefaults, expand: true));
        col.AddChild(footer);
        _status.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_status);
    }

    public void Open()
    {
        Visible = true;
        if (_selected is null || Look is not { } look || !look.Rules.Contains(_selected))
            _selected = Look is { Rules.Count: > 0 } l ? l.Rules[0] : null;
        RebuildAll();
        Callable.From(() => _scroll.ScrollVertical = 0).CallDeferred();
        SetStatus(_dirty ? "Unsaved changes." : "Changes show live. Save to keep them for every map.");
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        if (Terrain is { } t) t.RuleDebug = -1;
        _view.Selected = 0;
        Closed?.Invoke();
    }

    // --- Actions ---

    private void Changed()
    {
        Terrain?.ApplyLook(live: true);
        UpdateView();
        if (!_dirty) SetStatus("Unsaved changes.");
        _dirty = true;
    }

    private void Select(MaterialRule rule)
    {
        _selected = rule;
        RebuildRuleList();
        RebuildEditor();
        UpdateView();
    }

    private void AddRule()
    {
        if (Look is not { } look) return;
        var rule = MaterialRule.Make("", "New rule", TerrainLayers.Dirt, 1f, RuleCondition.Above(RuleInput.Slope, 30f, 5f));
        Insert(look, rule);
    }

    private void DuplicateRule()
    {
        if (Look is not { } look || _selected is null) return;
        var rule = _selected.Copy();
        rule.Name += " copy";
        rule.Id = ""; // the simple cards keep editing the original
        Insert(look, rule);
    }

    private void Insert(TerrainLook look, MaterialRule rule)
    {
        int at = _selected is null ? look.Rules.Count : look.Rules.IndexOf(_selected) + 1;
        look.Rules.Insert(at, rule);
        Changed();
        Select(rule);
    }

    private void Move(MaterialRule rule, int by)
    {
        if (Look is not { } look) return;
        int i = look.Rules.IndexOf(rule), j = i + by;
        if (i < 0 || j < 0 || j >= look.Rules.Count) return;
        look.Rules.RemoveAt(i);
        look.Rules.Insert(j, rule);
        Changed();
        RebuildRuleList();
    }

    private void Delete(MaterialRule rule)
    {
        if (Look is not { } look) return;
        int i = look.Rules.IndexOf(rule);
        look.Rules.Remove(rule);
        if (_selected == rule)
            _selected = look.Rules.Count == 0 ? null : look.Rules[Math.Clamp(i, 0, look.Rules.Count - 1)];
        Changed();
        RebuildAll();
    }

    private void ResetDefaults()
    {
        if (Look is not { } look) return;
        look.CopyFrom(TerrainLook.CreateDefault());
        _selected = look.Rules.Count > 0 ? look.Rules[0] : null;
        Changed();
        RebuildAll();
    }

    private void Save()
    {
        if (Look is not { } look) return;
        string path = string.IsNullOrEmpty(look.ResourcePath) ? TerrainLook.DefaultPath : look.ResourcePath;
        var err = ResourceSaver.Save(look, path);
        if (err == Error.Ok)
        {
            look.TakeOverPath(path);
            _dirty = false;
            SetStatus($"Saved {path}.");
        }
        else SetStatus($"Couldn't save {path}: {err}.");
    }

    private void Revert()
    {
        if (Terrain is not { } terrain || Look is not { } look) return;
        string path = string.IsNullOrEmpty(look.ResourcePath) ? TerrainLook.DefaultPath : look.ResourcePath;
        if (!ResourceLoader.Exists(path)) { SetStatus($"Nothing saved at {path} yet."); return; }
        var saved = ResourceLoader.Load<TerrainLook>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        look.CopyFrom(saved);
        _selected = look.Rules.Count > 0 ? look.Rules[0] : null;
        terrain.ApplyLook();
        _dirty = false;
        RebuildAll();
        UpdateView();
        SetStatus($"Reverted to {path}.");
    }

    private void UpdateView()
    {
        if (Terrain is not { } t) return;
        t.RuleDebug = (View)_view.Selected switch
        {
            View.SelectedRule when _selected is not null && Look is { } look => look.ShaderIndex(_selected),
            View.StrongestLayer => Terrain.LayerDebugView,
            View.Cost => Terrain.CostDebugView,
            _ => -1,
        };
    }

    private void SetStatus(string text) => _status.Text = text;

    // --- Building the controls ---

    private void RebuildAll()
    {
        RebuildCards();
        RebuildRuleList();
        RebuildEditor();
        RebuildLayers();
    }

    /// <summary>
    /// The simple view: one card per effect with a few plain sliders, each driving fields of the shipped rules (found by
    /// <see cref="MaterialRule.Id"/>). A card whose rules were deleted in Advanced disappears.
    /// </summary>
    private void RebuildCards()
    {
        Clear(_cards);
        if (Look is not { } look) return;

        if (Card(look, "Beach sand (lakes & rivers)", "Sand along lakes, rivers and the sea", "shore_sand") is { } sand)
        {
            var r = look.Find("shore_sand")!;
            Slider(sand, "Width", 0, 30, 0.5, "{0:0.#} m", r.A.To, v => r.A.To = v,
                "Sand fully covers the ground up to this far from the water (each metre above the water counts as 4 m)");
            Slider(sand, "Soft edge", 0, 40, 0.5, "{0:0.#} m", r.A.Fade, v => r.A.Fade = v,
                "How gradually sand fades into the grass beyond that. Wider is softer");
            Patchy(sand, r.A, 28f, "Breaks the sand's edge into patches instead of a smooth line");
        }
        if (Card(look, "Worn grass near water", "A band of trampled grass and dirt between the sand and the grass", "shore_worn") is { } worn)
        {
            var r = look.Find("shore_worn")!;
            Amount(worn, r);
            Slider(worn, "Reach", 0, 40, 0.5, "{0:0.#} m", r.A.To, v => r.A.To = v, "How far from the water it reaches");
        }
        if (Card(look, "Gully gravel", "Gravel in the beds of gullies and streams", "gully_beds") is { } gravel)
        {
            var r = look.Find("gully_beds")!;
            Amount(gravel, r);
            Slider(gravel, "Soft edge", 0, 12, 0.25, "{0:0.#} m", r.A.Fade, v => r.A.Fade = v,
                "How gradually the gravel fades into the ground around it");
        }
        if (Card(look, "Dirt along gullies", "Bare dirt on the banks of gullies", "gully_banks") is { } banks)
        {
            var r = look.Find("gully_banks")!;
            Amount(banks, r);
            Slider(banks, "Reach", 0, 12, 0.25, "{0:0.#} m", r.A.To, v => r.A.To = v, "How far from the gully bed it reaches");
        }
        if (Card(look, "Cliffs (rock)", "Bare rock on steep ground", "cliffs", "scoured_rock") is { } cliffs)
        {
            var r = look.Find("cliffs")!;
            var scoured = look.Find("scoured_rock");
            // Water-scoured ground turns to rock a little earlier; it follows the main threshold.
            float scouredOffset = scoured is null ? 0f : r.A.From - scoured.A.From;
            Slider(cliffs, "Starts at", 20, 80, 0.5, "{0:0}°", r.A.From, v =>
            {
                r.A.From = v;
                if (scoured is not null) scoured.A.From = v - scouredOffset;
            }, "Slopes steeper than this are rock");
            Slider(cliffs, "Soft edge", 0, 25, 0.5, "{0:0}°", r.A.Fade, v =>
            {
                r.A.Fade = v;
                if (scoured is not null) scoured.A.Fade = v;
            }, "How gradually grass turns to rock below that slope");
        }
        if (Card(look, "Grassy dirt on slopes", "Thinner, dirtier grass on medium slopes", "worn_slopes") is { } slopes)
        {
            var r = look.Find("worn_slopes")!;
            Slider(slopes, "Starts at", 10, 70, 0.5, "{0:0}°", r.A.From, v => r.A.From = v, "Full coverage from this slope up");
            Slider(slopes, "Soft edge", 0, 30, 0.5, "{0:0}°", r.A.Fade, v => r.A.Fade = v, "How gradually it fades in below that slope");
        }
        if (Card(look, "Scree below cliffs", "Loose gravel at the foot of rock faces", "scree") is { } scree)
            Amount(scree, look.Find("scree")!);
        if (Card(look, "Dry grass", "Yellowed grass in large patches and on high ground", "dry_patches", "dry_high") is { } dry)
        {
            if (look.Find("dry_patches") is { } p) Amount(dry, p, "Patches", "Dry grass in large random patches");
            if (look.Find("dry_high") is { } h) Amount(dry, h, "High ground", "Dry grass on the upper part of the map");
        }
        if (Card(look, "Eroded ground", "Dirt and gravel where running water scours and where its sediment settles",
                "scoured_dirt", "scoured_gravel", "deposits", "thick_deposits") is { } eroded)
        {
            AmountOfAll(eroded, "Water channels", "Bare dirt and gravel where running water scours", look, "scoured_dirt", "scoured_gravel");
            AmountOfAll(eroded, "Sediment", "Dirt and gravel fans where water slows and drops what it carries", look, "deposits", "thick_deposits");
        }
    }

    /// <summary>A card: a title with an on/off box for its rules, and a grid for its sliders. Null if none of its rules exist.</summary>
    private GridContainer? Card(TerrainLook look, string title, string tooltip, params string[] ids)
    {
        var rules = new System.Collections.Generic.List<MaterialRule>();
        foreach (var id in ids)
            if (look.Find(id) is { } r) rules.Add(r);
        if (rules.Count == 0) return null;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        // The theme draws a checked box like a pressed button, so the title is a separate label.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        var on = new CheckBox { ButtonPressed = rules[0].Enabled, FocusMode = FocusModeEnum.None, TooltipText = "On / off" };
        head.AddChild(on);
        var label = new Label { Text = title, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AddThemeColorOverride("font_color", UiTheme.Accent);
        head.AddChild(label);
        box.AddChild(head);
        var grid = NewGrid();
        box.AddChild(grid);
        on.Toggled += v =>
        {
            foreach (var r in rules) r.Enabled = v;
            grid.Modulate = v ? Colors.White : new Color(1, 1, 1, 0.4f);
            Changed();
            RebuildRuleList();
        };
        grid.Modulate = rules[0].Enabled ? Colors.White : new Color(1, 1, 1, 0.4f);
        _cards.AddChild(box);
        return grid;
    }

    private void Amount(GridContainer grid, MaterialRule r, string label = "Amount", string tooltip = "How strongly it covers the ground") =>
        Slider(grid, label, 0, 1, 0.01, "{0:0%}", r.Strength, v => r.Strength = v, tooltip);

    /// <summary>One slider for several rules' strength, keeping their ratio (it shows the first rule's).</summary>
    private void AmountOfAll(GridContainer grid, string label, string tooltip, TerrainLook look, params string[] ids)
    {
        var rules = new System.Collections.Generic.List<MaterialRule>();
        foreach (var id in ids)
            if (look.Find(id) is { } r) rules.Add(r);
        if (rules.Count == 0) return;
        float first = rules[0].Strength;
        var ratio = rules.ConvertAll(r => first > 0 ? r.Strength / first : 1f);
        Slider(grid, label, 0, 1, 0.01, "{0:0%}", first, v =>
        {
            for (int i = 0; i < rules.Count; i++) rules[i].Strength = Math.Clamp(v * ratio[i], 0f, 1f);
        }, tooltip);
    }

    private void Patchy(GridContainer grid, RuleCondition c, float max, string tooltip) =>
        Slider(grid, "Patchy", 0, 100, 1, "{0:0}%", c.Noise / max * 100f, v => c.Noise = v / 100f * max, tooltip);

    /// <summary>A collapsed section: a flat "▸ title" button that shows or hides the returned container.</summary>
    private static VBoxContainer Fold(VBoxContainer parent, string title, string tooltip, Action? onClose = null)
    {
        var button = new Button
        {
            Text = "▸  " + title, Flat = true, FocusMode = FocusModeEnum.None, TooltipText = tooltip,
            Alignment = HorizontalAlignment.Left,
        };
        var content = new VBoxContainer { Visible = false };
        content.AddThemeConstantOverride("separation", 8);
        button.Pressed += () =>
        {
            content.Visible = !content.Visible;
            button.Text = (content.Visible ? "▾  " : "▸  ") + title;
            if (!content.Visible) onClose?.Invoke();
        };
        parent.AddChild(button);
        parent.AddChild(content);
        return content;
    }

    private void RebuildRuleList()
    {
        Clear(_ruleList);
        if (Look is not { } look) return;
        for (int i = 0; i < look.Rules.Count; i++)
        {
            var rule = look.Rules[i];
            if (rule is null) continue;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 2);
            var on = new CheckBox { ButtonPressed = rule.Enabled, FocusMode = FocusModeEnum.None, TooltipText = "Enabled" };
            on.Toggled += v => { rule.Enabled = v; Changed(); };
            row.AddChild(on);
            var pick = new Button
            {
                Text = $"{i + 1}. {rule.Name}  ·  {LayerNames[Math.Clamp(rule.Layer, 0, LayerNames.Length - 1)]}",
                ToggleMode = true, ButtonPressed = rule == _selected, FocusMode = FocusModeEnum.None,
                Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true, Modulate = rule.Enabled ? Colors.White : new Color(1, 1, 1, 0.5f),
            };
            pick.Pressed += () => Select(rule);
            row.AddChild(pick);
            row.AddChild(SmallButton("▲", "Move up (applied earlier, so later rules cover it)", () => Move(rule, -1)));
            row.AddChild(SmallButton("▼", "Move down (applied later, covering earlier rules)", () => Move(rule, 1)));
            row.AddChild(SmallButton("✕", "Delete this rule", () => Delete(rule)));
            _ruleList.AddChild(row);
        }
        if (look.Rules.Count > TerrainLook.MaxRules)
            _ruleList.AddChild(new Label { Text = $"Only the first {TerrainLook.MaxRules} enabled rules are used.", Modulate = UiTheme.TextDim });
    }

    private void RebuildEditor()
    {
        Clear(_editor);
        if (_selected is not { } rule)
        {
            Row(_editor, "", new Label { Text = "Select or add a rule.", Modulate = UiTheme.TextDim });
            return;
        }

        var name = new LineEdit { Text = rule.Name };
        name.TextChanged += t => rule.Name = t;
        name.TextSubmitted += _ => RebuildRuleList();
        name.FocusExited += RebuildRuleList;
        Row(_editor, "Name", name);

        var layer = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var n in LayerNames) layer.AddItem(n);
        layer.Selected = Math.Clamp(rule.Layer, 0, LayerNames.Length - 1);
        layer.ItemSelected += i => { rule.Layer = (int)i; Changed(); RebuildRuleList(); };
        Row(_editor, "Layer", layer, "The ground layer this rule lays down");

        Slider(_editor, "Strength", 0, 1, 0.01, "{0:0%}", rule.Strength, v => rule.Strength = v,
            "Coverage where both conditions fully hold");

        rule.A ??= new RuleCondition();
        rule.B ??= new RuleCondition();
        ConditionRows("Where", rule.A);
        ConditionRows("And where", rule.B);
    }

    private void ConditionRows(string title, RuleCondition c)
    {
        var info = RuleInputInfo.For(c.Input);
        var input = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var i in RuleInputInfo.All) input.AddItem(i.Name);
        input.Selected = (int)c.Input + 1;
        input.TooltipText = info.Help;
        input.ItemSelected += i =>
        {
            var next = RuleInputInfo.All[i];
            // Start a new input somewhere sensible: the upper half of its range, fading over a tenth of it.
            if (next.Input != RuleInput.None && next.Input != c.Input)
            {
                float span = next.Max - next.Min;
                c.From = next.Min + span * 0.5f;
                c.To = next.Max;
                c.FromOpen = false;
                c.ToOpen = true;
                c.Fade = span * 0.1f;
                c.Noise = 0f;
            }
            c.Input = next.Input;
            Changed();
            Callable.From(RebuildEditor).CallDeferred();
        };
        var head = new Label { Text = title };
        head.AddThemeColorOverride("font_color", UiTheme.Accent);
        _editor.AddChild(head);
        input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _editor.AddChild(input);
        if (c.Input == RuleInput.None) return;

        float spanAll = info.Max - info.Min;
        RangeEnd("From", info, c.From, c.FromOpen, v => c.From = v, open => c.FromOpen = open,
            "Full coverage from here up (open: no lower end)");
        RangeEnd("To", info, c.To, c.ToOpen, v => c.To = v, open => c.ToOpen = open,
            "Full coverage up to here (open: no upper end)");
        Slider(_editor, "Fade", 0, spanAll * 0.5, info.Step, info.Format, c.Fade, v => c.Fade = v,
            "Width of the blend outside the range. Wider is softer");
        Slider(_editor, "Edge Noise", 0, spanAll * 0.25, info.Step, info.Format, c.Noise, v => c.Noise = v,
            "How far noise moves the edges (about ±⅓ of this), so they break up into patches");
        var noiseSize = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var n in new[] { "Fine (~10 m)", "Patchy (~35 m)", "Medium (~150 m)", "Large (~700 m)" }) noiseSize.AddItem(n);
        noiseSize.Selected = (int)c.NoiseSize;
        noiseSize.ItemSelected += i => { c.NoiseSize = (NoiseScale)(int)i; Changed(); };
        Row(_editor, "Noise Size", noiseSize, "Size of the edge noise pattern");
        var invert = new CheckBox { ButtonPressed = c.Invert, FocusMode = FocusModeEnum.None, Text = "Outside the range" };
        invert.Toggled += v => { c.Invert = v; Changed(); };
        Row(_editor, "Invert", invert);
    }

    private void RangeEnd(string name, RuleInputInfo info, float value, bool open, Action<float> set, Action<bool> setOpen, string tooltip)
    {
        var row = new HBoxContainer();
        var (slider, label) = NewSlider(info.Min, info.Max, info.Step, info.Format, value, set, tooltip);
        slider.Editable = !open;
        row.AddChild(slider);
        row.AddChild(label);
        var openBox = new CheckBox { Text = "open", ButtonPressed = open, FocusMode = FocusModeEnum.None, TooltipText = tooltip };
        openBox.Toggled += v => { setOpen(v); slider.Editable = !v; Changed(); };
        row.AddChild(openBox);
        Row(_editor, name, row, tooltip);
    }

    private void RebuildLayers()
    {
        Clear(_layers);
        if (Look is not { } look) return;
        foreach (var l in TerrainLayers.All)
        {
            var pick = new ColorPickerButton { Color = look.Tint(l.Index), EditAlpha = false, CustomMinimumSize = new Vector2(0, 24) };
            pick.ColorChanged += c => { look.SetTint(l.Index, c); Changed(); };
            Row(_layers, l.DisplayName, pick);
        }
        Slider(_layers, "Height Blend", 0, 1, 0.01, "{0:0.00}", look.HeightBlend, v => look.HeightBlend = v,
            "How much texture height decides layer borders (grass fills between stones, sand between grass tufts)");
        Slider(_layers, "Softness", 0.01, 0.6, 0.01, "{0:0.00}", look.BlendSoftness, v => look.BlendSoftness = v,
            "Width of height-blended borders: lower is crisper");
    }

    // --- Helpers ---

    private void Slider(GridContainer grid, string name, double min, double max, double step, string format,
        float value, Action<float> set, string tooltip)
    {
        var row = new HBoxContainer();
        var (slider, label) = NewSlider(min, max, step, format, value, set, tooltip);
        row.AddChild(slider);
        row.AddChild(label);
        Row(grid, name, row, tooltip);
    }

    private (HSlider, Label) NewSlider(double min, double max, double step, string format, float value, Action<float> set, string tooltip)
    {
        // Values set elsewhere (inspector, older files) may sit outside the slider's range: widen it rather than clamp them.
        var slider = new HSlider
        {
            MinValue = Math.Min(min, value), MaxValue = Math.Max(max, value), Step = step, Value = value,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None, TooltipText = tooltip,
        };
        var label = new Label
        {
            Text = string.Format(format, value), CustomMinimumSize = new Vector2(60, 0), HorizontalAlignment = HorizontalAlignment.Right,
        };
        slider.ValueChanged += v =>
        {
            label.Text = string.Format(format, v);
            set((float)v);
            Changed();
        };
        return (slider, label);
    }

    private static GridContainer NewGrid()
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 6);
        return grid;
    }

    private static Label SectionLabel(string text, string tooltip)
    {
        var label = new Label { Text = text, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass };
        label.AddThemeColorOverride("font_color", UiTheme.Accent);
        return label;
    }

    private static void Row(GridContainer grid, string name, Control value, string tooltip = "")
    {
        // Fixed label width so sliders line up across cards (each card has its own grid).
        grid.AddChild(new Label { Text = name, TooltipText = tooltip, MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(96, 0) });
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(value);
    }

    private static Button SmallButton(string text, string tooltip, Action pressed, bool expand = false)
    {
        var b = new Button
        {
            Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(26, 26),
            SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
        };
        b.Pressed += pressed;
        return b;
    }

    private static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
