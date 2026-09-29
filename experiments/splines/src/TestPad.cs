using System;
using System.Globalization;
using System.Linq;
using CitySim.TerrainSystem;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--test-pad[=size]</c>: levels a big square (default 2000 m, capped to the map) at the map's centre for testing
/// splines on flat ground, and paints it <see cref="Material"/> so its edge is easy to see. Outside the square the
/// ground ramps back to the original terrain over <see cref="Blend"/> metres. One terrain edit, applied at start-up.
/// </summary>
public partial class TestPad : Node
{
    [Export] public Terrain? Terrain { get; set; }
    /// <summary>The theme material painted on the pad.</summary>
    [Export] public string Material { get; set; } = "sand";
    /// <summary>Width of the ramp from the pad back to the original ground, in metres.</summary>
    [Export] public float Blend { get; set; } = 200f;

    public override void _Ready()
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a == "--test-pad" || a.StartsWith("--test-pad="));
        if (arg is null) return;
        float size = arg.Length > "--test-pad=".Length ? float.Parse(arg["--test-pad=".Length..], CultureInfo.InvariantCulture) : 2000f;
        Callable.From(() => Apply(size)).CallDeferred();
    }

    private void Apply(float size)
    {
        if (Terrain?.Map is not { } map || Terrain.Splat is not { } splat) { GD.PrintErr("Test pad: no map"); return; }
        float cell = map.CellSize;
        float mapW = (map.Width - 1) * cell, mapD = (map.Depth - 1) * cell;
        float half = MathF.Min(size, MathF.Min(mapW, mapD) - 2 * Blend - 100f) / 2;
        if (half <= 0) { GD.PrintErr("Test pad: map too small"); return; }
        float cx = mapW / 2, cz = mapD / 2, outer = half + Blend;

        var inner = VertexRect.FromMapRect(cx - half, cz - half, cx + half, cz + half, cell, map.Width, map.Depth);
        var rect = VertexRect.FromMapRect(cx - outer, cz - outer, cx + outer, cz + outer, cell, map.Width, map.Depth);
        float level = map.CopyRegion(inner).Average();

        int layer = Array.IndexOf(splat.Palette, Material);
        if (layer < 0) layer = Terrain.Theme?.IndexOf(Material) ?? -1;
        if (layer < 0) GD.PrintErr($"Test pad: no material '{Material}' in the theme, left unpainted");

        using (var edit = Terrain.BeginEdit(paint: layer >= 0))
        {
            edit.Touch(rect);
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                {
                    // Distance outside the square (0 inside), eased from the pad level back to the ground.
                    float dx = MathF.Max(MathF.Abs(x * cell - cx) - half, 0), dz = MathF.Max(MathF.Abs(z * cell - cz) - half, 0);
                    float t = Math.Clamp(MathF.Sqrt(dx * dx + dz * dz) / Blend, 0, 1);
                    t = t * t * (3 - 2 * t);
                    edit.Heights[x, z] = level + (edit.Original(x, z) - level) * t;
                    if (layer >= 0 && t <= 0) edit.Splat.Set(x, z, SplatMap.Encode(layer, layer, 0, 255));
                }
        }
        GD.Print($"Test pad: {half * 2:0} m square at map ({cx:0}, {cz:0}), {level:0} m high, painted {Material}");
    }
}
