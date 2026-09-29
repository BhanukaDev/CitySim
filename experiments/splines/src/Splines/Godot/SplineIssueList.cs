using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The issue list (DESIGN.md → Feedback): the draw's issues, then the built network's, in plain words with the fix,
/// amber or red, in a small panel at the bottom left. Hidden when there's nothing to say.
/// </summary>
public partial class SplineIssueList : PanelContainer
{
    private const int MaxLines = 8;
    private readonly VBoxContainer _lines = new();
    private string _shown = "";

    public SplineIssueList()
    {
        Name = "SplineIssueList";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        GrowVertical = GrowDirection.Begin;
        OffsetLeft = 12;
        OffsetBottom = -12;
        AddChild(_lines);
        Visible = false;
    }

    public void Show(IReadOnlyList<Issue> draw, IReadOnlyList<Issue> built, bool anarchy)
    {
        var rows = new List<(string Text, Color Color)>();
        if (anarchy) rows.Add(("Anarchy on: red builds", SplineOverlay.Bad));
        rows.AddRange(draw.Select(i => ($"• {i.Message}", ColorOf(i))));
        if (built.Count > 0) rows.Add(($"Built: {built.Count} issue{(built.Count == 1 ? "" : "s")}", Colors.White));
        rows.AddRange(built.Take(MaxLines).Select(i => ($"  {i.Message}", ColorOf(i))));
        string key = string.Join("\n", rows.Select(r => r.Text));
        if (key == _shown) return;
        _shown = key;
        foreach (var child in _lines.GetChildren()) child.QueueFree();
        foreach (var (text, color) in rows)
        {
            var label = new Label { Text = text };
            label.AddThemeColorOverride("font_color", color);
            _lines.AddChild(label);
        }
        Visible = rows.Count > 0;
    }

    private static Color ColorOf(Issue i) => i.Severity == Severity.Invalid ? SplineOverlay.Bad : SplineOverlay.Warn;
}
