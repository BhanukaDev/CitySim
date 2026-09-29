using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// The Draw tool's cursor tag (DESIGN.md → Feedback): segment length, heading, turn angle at the last corner, and
/// the pending radius, in mono font next to the mouse. A red hint flashes briefly when Alt+click asked for a hard
/// corner a profile doesn't allow.
/// </summary>
public sealed class DrawCursorTag
{
    private readonly Label _label;
    private double _hintUntil;

    public DrawCursorTag(CanvasLayer layer)
    {
        _label = new Label
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 10,
        };
        _label.AddThemeFontSizeOverride("font_size", 14);
        // DESIGN.md asks for a mono font; the repo has no bundled one yet, so this uses the default theme font
        // until one is added.
        layer.AddChild(_label);
    }

    public void Update(Vector2 screenPos, float length, float headingDeg, float turnDeg, float radius, string? snapTag = null)
    {
        _label.Visible = true;
        string text = $"{length:0.#} m · {headingDeg:0}°";
        if (!float.IsNaN(turnDeg)) text += $" · turn {turnDeg:+0;-0}°";
        text += radius > 0 ? $" · R {radius:0} m" : " · hard";
        if (!string.IsNullOrEmpty(snapTag)) text += $"\n{snapTag}";
        _label.Text = _hintUntil > 0 ? text + "\nhard corners not allowed" : text;
        _label.Position = screenPos + new Vector2(16, 16);
    }

    /// <summary>Before the first PI is placed there's no leg to read a length/heading/radius off, but a node,
    /// edge or guide can still catch — DESIGN.md's snap tag is shown either way, so the player knows where their
    /// first click will land.</summary>
    public void ShowSnapOnly(Vector2 screenPos, string tag)
    {
        _label.Visible = true;
        _label.Text = tag;
        _label.Position = screenPos + new Vector2(16, 16);
    }

    /// <summary>Flashes the "hard corners not allowed" hint for a moment.</summary>
    public void FlashHardCornerHint(double now) => _hintUntil = now + 1.2;

    public void Tick(double now)
    {
        if (_hintUntil > 0 && now > _hintUntil) _hintUntil = 0;
    }

    public void Hide() => _label.Visible = false;
}
