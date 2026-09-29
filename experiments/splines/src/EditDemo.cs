using System;
using System.Linq;
using CitySim.TerrainSystem;
using Godot;

namespace CitySim.Splines;

/// <summary>
/// <c>--demo-edit</c>: checks the terrain package's edit API from outside the terrain experiment. Levels a 200 m pad at
/// the map's centre through <see cref="Terrain.BeginEdit"/>, then checks undo, redo, cancel and the
/// <see cref="Terrain.HeightsChanged"/> event (prints <c>Demo edit: all ok</c>).
/// </summary>
public partial class EditDemo : Node
{
    [Export] public Terrain? Terrain { get; set; }

    public override void _Ready()
    {
        if (OS.GetCmdlineUserArgs().Contains("--demo-edit")) Callable.From(Run).CallDeferred();
    }

    private async void Run()
    {
        if (Terrain?.Map is not { } map) { GD.PrintErr("Demo edit: no map"); return; }
        var terrain = Terrain;
        var seen = VertexRect.Empty;
        terrain.HeightsChanged += r => seen = seen.Union(r);

        float cx = (map.Width - 1) * map.CellSize / 2, cz = (map.Depth - 1) * map.CellSize / 2;
        var rect = VertexRect.FromMapRect(cx - 100, cz - 100, cx + 100, cz + 100, map.CellSize, map.Width, map.Depth);
        var before = map.CopyRegion(rect);
        float level = before.Average();

        using (var edit = terrain.BeginEdit())
        {
            edit.Touch(rect);
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                    edit.Heights[x, z] = level;
        }
        bool flat = IsLevel(map, rect, level);
        bool originalKept = true;

        terrain.Undo();
        bool undone = map.CopyRegion(rect).SequenceEqual(before);
        terrain.Redo();
        bool redone = IsLevel(map, rect, level);

        var raise = terrain.BeginEdit();
        raise.Touch(rect);
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                originalKept &= raise.Original(x, z) == level;
                raise.Heights[x, z] = level + 50f;
            }
        raise.Cancel();
        bool cancelled = IsLevel(map, rect, level) && !terrain.History.InStroke;

        bool busy = false;
        using (terrain.BeginEdit())
            try { terrain.BeginEdit(); } catch (InvalidOperationException) { busy = true; }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool evented = !seen.IsEmpty && seen.Union(rect) == seen;

        GD.Print($"Demo edit: pad {rect.Width}x{rect.Depth} verts at {level:0.0} m {Ok(flat)}, undo {Ok(undone)}, redo {Ok(redone)}, " +
                 $"original {Ok(originalKept)}, cancel {Ok(cancelled)}, one edit at a time {Ok(busy)}, HeightsChanged {Ok(evented)}");
        GD.Print($"Demo edit: {(flat && undone && redone && originalKept && cancelled && busy && evented ? "all ok" : "FAILED")}");
    }

    private static bool IsLevel(HeightMap map, VertexRect rect, float level)
    {
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            for (int x = rect.MinX; x <= rect.MaxX; x++)
                if (map[x, z] != level) return false;
        return true;
    }

    private static string Ok(bool b) => b ? "ok" : "FAILED";
}
