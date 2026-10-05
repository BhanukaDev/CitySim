using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CitySim.Splines.Godot;

/// <summary>
/// Draws an input like <c>"Shift+wheel"</c>, <c>"RMB"</c> or <c>"[ ] · Shift+[ ]"</c>: mouse buttons as icons from
/// <c>icons/</c> (see its LICENSE.md), keyboard keys as outlined keycaps with the key's name (a key icon read as the
/// wrong key: Del's ⌦ looked like X). <c>+</c> joins a combo (side by side), <c>·</c> separates alternatives (drawn as
/// <c>/</c>). A part that is neither (say <c>"Release"</c>) is drawn as text, so any consumer can pass its own keys.
/// </summary>
public static class KeyGlyphs
{
    private const string IconDir = "res://addons/citysim_splines/icons/";
    private const float Gap = 2f;

    /// <summary>Each mouse input's icons, then any text after them (<c>"×2"</c>). Matched ignoring case.</summary>
    private static readonly Dictionary<string, (string[] Icons, string? Text)> Mouse = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LMB"] = (new[] { "mouse-left" }, null),
        ["click"] = (new[] { "mouse-left" }, null),
        ["RMB"] = (new[] { "mouse-right" }, null),
        ["wheel"] = (new[] { "mouse-wheel" }, null),
        ["drag"] = (new[] { "mouse-left", "arrows-move" }, null),
        ["Double-click"] = (new[] { "mouse-left" }, "×2"),
    };

    /// <summary>Keyboard keys, drawn as keycaps with these names. Matched ignoring case.</summary>
    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shift"] = "Shift", ["Ctrl"] = "Ctrl", ["Alt"] = "Alt", ["Esc"] = "Esc", ["Del"] = "Del", ["Space"] = "Space",
        ["Enter"] = "Enter", ["[ ]"] = "[ ]", ["A"] = "A", ["Z"] = "Z",
    };

    private const float CapPadX = 4f;
    private static readonly Dictionary<Color, StyleBoxFlat> CapStyles = new();

    private static readonly Dictionary<(string, int), Texture2D?> Cache = new();

    /// <summary>The width <see cref="Draw"/> takes for <paramref name="key"/>.</summary>
    public static float Width(string key, Font font, int fontSize, int iconPx)
    {
        float w = 0;
        foreach (var (icon, text, cap) in Tokens(key))
            w += (cap ? CapWidth(text, font, fontSize) : icon is null ? font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X : iconPx) + Gap;
        return MathF.Max(0, w - Gap);
    }

    /// <summary>Draws <paramref name="key"/> from the text baseline at <paramref name="pen"/>, icons centred on the
    /// text and tinted <paramref name="color"/>, keycaps outlined in it. Returns the width drawn.</summary>
    public static float Draw(CanvasItem canvas, Vector2 pen, string key, Font font, int fontSize, int iconPx, Color color)
    {
        float x = pen.X;
        float top = pen.Y - fontSize * 0.35f - iconPx / 2f;
        foreach (var (icon, text, cap) in Tokens(key))
        {
            if (cap)
            {
                int size = CapFontSize(fontSize);
                float w = CapWidth(text, font, fontSize), h = size + 6f;
                var box = new Rect2(x, pen.Y - fontSize * 0.35f - h / 2f, w, h);
                canvas.DrawStyleBox(CapStyle(color), box);
                canvas.DrawString(font, new Vector2(x + CapPadX, box.Position.Y + (h + font.GetAscent(size) - font.GetDescent(size)) / 2f),
                    text, HorizontalAlignment.Left, -1, size, color);
                x += w + Gap;
                continue;
            }
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

    /// <summary>Whether every part of <paramref name="key"/>'s combo is a mouse input or a key (<c>"Shift+click"</c> yes,
    /// <c>"junction"</c> no).</summary>
    public static bool Known(string key) => key.Split('+').All(part => Mouse.ContainsKey(part.Trim()) || Keys.ContainsKey(part.Trim()));

    /// <summary>Whether <paramref name="part"/> (one part of a combo) is a mouse input, drawn as an icon.</summary>
    public static bool IsMouse(string part) => Mouse.ContainsKey(part.Trim());

    private static int CapFontSize(int fontSize) => Math.Max(9, fontSize - 2);

    private static float CapWidth(string text, Font font, int fontSize) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, CapFontSize(fontSize)).X + 2 * CapPadX;

    private static StyleBoxFlat CapStyle(Color color)
    {
        if (CapStyles.TryGetValue(color, out var sb)) return sb;
        sb = new StyleBoxFlat { BgColor = new Color(color, 0.12f), BorderColor = new Color(color, 0.7f) };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(3);
        return CapStyles[color] = sb;
    }

    /// <summary>Appends <paramref name="key"/> to a rich text label: icons inline, tinted <paramref name="color"/>, keys
    /// as their names on a faint background.</summary>
    public static void Append(RichTextLabel label, string key, int iconPx, Color color)
    {
        bool first = true;
        foreach (var (icon, text, cap) in Tokens(key))
        {
            if (!first) label.AddText("\u2009");
            first = false;
            if (cap)
            {
                label.PushBgcolor(new Color(color, 0.18f));
                label.PushColor(color);
                label.AddText($"\u2009{text}\u2009");
                label.Pop();
                label.Pop();
            }
            else if (icon is null)
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

    /// <summary>The key cut into icons (Icon set), keycaps (Cap, the key's name in Text) and text runs.</summary>
    private static IEnumerable<(string? Icon, string Text, bool Cap)> Tokens(string key)
    {
        var alternatives = key.Split(" · ");
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) yield return (null, "/", false);
            foreach (var part in alternatives[a].Split('+'))
            {
                if (Keys.TryGetValue(part.Trim(), out var name))
                {
                    yield return (null, name, true);
                    continue;
                }
                if (!Mouse.TryGetValue(part.Trim(), out var p))
                {
                    yield return (null, part, false);
                    continue;
                }
                foreach (var icon in p.Icons) yield return (icon, "", false);
                if (p.Text is not null) yield return (null, p.Text, false);
            }
        }
    }
}
