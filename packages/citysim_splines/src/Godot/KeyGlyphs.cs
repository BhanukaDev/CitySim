using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// Draws an input like <c>"Shift+wheel"</c>, <c>"RMB"</c> or <c>"[ ] · Shift+[ ]"</c> as mouse and keycap icons from
/// <c>icons/</c> (see its LICENSE.md). <c>+</c> joins a combo (drawn as icons side by side), <c>·</c> separates
/// alternatives (drawn as <c>/</c>). A part with no icon (say <c>"Release"</c>) is drawn as text, so any consumer can
/// pass its own keys.
/// </summary>
public static class KeyGlyphs
{
    private const string IconDir = "res://addons/citysim_splines/icons/";
    private const float Gap = 2f;

    /// <summary>Each input's icons, then any text after them (<c>"×2"</c>). Keys are matched ignoring case.</summary>
    private static readonly Dictionary<string, (string[] Icons, string? Text)> Parts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LMB"] = (new[] { "mouse-left" }, null),
        ["click"] = (new[] { "mouse-left" }, null),
        ["RMB"] = (new[] { "mouse-right" }, null),
        ["wheel"] = (new[] { "mouse-wheel" }, null),
        ["drag"] = (new[] { "mouse-left", "arrows-move" }, null),
        ["Double-click"] = (new[] { "mouse-left" }, "×2"),
        ["Shift"] = (new[] { "key-shift" }, null),
        ["Ctrl"] = (new[] { "key-ctrl" }, null),
        ["Alt"] = (new[] { "key-alt" }, null),
        ["Esc"] = (new[] { "key-esc" }, null),
        ["Del"] = (new[] { "key-del" }, null),
        ["Space"] = (new[] { "key-space" }, null),
        ["Enter"] = (new[] { "key-enter" }, null),
        ["[ ]"] = (new[] { "key-brackets" }, null),
        ["A"] = (new[] { "key-a" }, null),
        ["Z"] = (new[] { "key-z" }, null),
    };

    private static readonly Dictionary<(string, int), Texture2D?> Cache = new();

    /// <summary>The width <see cref="Draw"/> takes for <paramref name="key"/>.</summary>
    public static float Width(string key, Font font, int fontSize, int iconPx)
    {
        float w = 0;
        foreach (var (icon, text) in Tokens(key))
            w += (icon is null ? font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X : iconPx) + Gap;
        return MathF.Max(0, w - Gap);
    }

    /// <summary>Draws <paramref name="key"/> from the text baseline at <paramref name="pen"/>, icons centred on the
    /// text and tinted <paramref name="color"/>. Returns the width drawn.</summary>
    public static float Draw(CanvasItem canvas, Vector2 pen, string key, Font font, int fontSize, int iconPx, Color color)
    {
        float x = pen.X;
        float top = pen.Y - fontSize * 0.35f - iconPx / 2f;
        foreach (var (icon, text) in Tokens(key))
        {
            if (icon is null)
            {
                canvas.DrawString(font, new Vector2(x, pen.Y), text, HorizontalAlignment.Left, -1, fontSize, color);
                x += font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X + Gap;
                continue;
            }
            if (Icon(icon, iconPx) is { } tex) canvas.DrawTextureRect(tex, new Rect2(x, top, iconPx, iconPx), false, color);
            x += iconPx + Gap;
        }
        return MathF.Max(0, x - pen.X - Gap);
    }

    /// <summary>Whether every part of <paramref name="key"/>'s combo has an icon (<c>"Shift+click"</c> yes,
    /// <c>"junction"</c> no).</summary>
    public static bool Known(string key) => key.Split('+').All(part => Parts.ContainsKey(part.Trim()));

    /// <summary>Appends <paramref name="key"/> to a rich text label: icons inline, tinted <paramref name="color"/>.</summary>
    public static void Append(RichTextLabel label, string key, int iconPx, Color color)
    {
        bool first = true;
        foreach (var (icon, text) in Tokens(key))
        {
            if (!first) label.AddText("\u2009");
            first = false;
            if (icon is null)
            {
                label.PushColor(color);
                label.AddText(text);
                label.Pop();
            }
            else if (Icon(icon, iconPx) is { } tex) label.AddImage(tex, iconPx, iconPx, color, InlineAlignment.Center);
        }
    }

    /// <summary>A white icon from <c>icons/</c> rasterised once at <paramref name="px"/> (crisper than scaling an
    /// imported 64 px texture down), or null with a warning when it's missing.</summary>
    public static Texture2D? Icon(string name, int px)
    {
        if (Cache.TryGetValue((name, px), out var tex)) return tex;
        string path = IconDir + name + ".svg";
        var image = new Image();
        tex = FileAccess.FileExists(path) && image.LoadSvgFromString(FileAccess.GetFileAsString(path), px / 64f) == Error.Ok
            ? ImageTexture.CreateFromImage(image) : null;
        if (tex is null) GD.PushWarning($"KeyGlyphs: no icon {path}");
        return Cache[(name, px)] = tex;
    }

    /// <summary>The key cut into icons (Text null) and text runs (Icon null).</summary>
    private static IEnumerable<(string? Icon, string Text)> Tokens(string key)
    {
        var alternatives = key.Split(" · ");
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) yield return (null, "/");
            foreach (var part in alternatives[a].Split('+'))
            {
                if (!Parts.TryGetValue(part.Trim(), out var p))
                {
                    yield return (null, part);
                    continue;
                }
                foreach (var icon in p.Icons) yield return (icon, "");
                if (p.Text is not null) yield return (null, p.Text);
            }
        }
    }
}
