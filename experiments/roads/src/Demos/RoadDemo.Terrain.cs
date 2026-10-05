using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.Splines;
using CitySim.Terraform;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Demos;

public partial class RoadDemo
{
    /// <summary>
    /// <c>--demo-terrain</c>: the Terrain tray's tools on flat ground with a two-lane road. Shift raised across the road
    /// leaves the road and the ground under it where they were and raises the rest; one undo puts every height back (the
    /// stroke and the roads' shaping back after it), redo raises it again. A stroke ends the road undo history, a road
    /// ends the terrain one. Level, Smooth, Slope and Channel (both modes) each change the ground and undo exactly; Shift
    /// lowers in Lower mode and Alt flips it; RMB mid-stroke puts the ground back and records nothing. Paint and Erase
    /// change only the paint, undo exactly, and end the road undo history. Ends with Shift picked, contours on and the
    /// brush over the road, for a screenshot. Prints "Demo terrain: all ok".
    /// </summary>
    private async void RunTerrain()
    {
        var problems = new List<string>();
        var tools = GetTree().Root.FindChild(nameof(TerrainToolController), true, false) as TerrainToolController;
        if (Host?.ProfileFor("two_lane") is not { } profile || Host.Hud is not { } hud || Network?.Terrain is not { Map: not null } terrain || tools is null)
        {
            GD.PrintErr("Demo terrain: no two_lane road, HUD, terrain or terrain tools");
            return;
        }
        var map = terrain.Map;
        float H(float x, float z) => terrain.GetHeightAtMap(x, z);
        void Pick(string id)
        {
            hud.OpenById("terrain");
            hud.Tray.Pick(hud.Library.Item(id));
        }
        bool Same(float[] a) => map.Snapshot().AsSpan().SequenceEqual(a);

        Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(400, RoadZ)), new Pi(new NumVector2(1000, RoadZ))]), profile.ToRules()));
        await Frames(2);
        float road = Network.Graph.Edges.Single().Heights!.At(300);
        var before = map.Snapshot();
        float baseH = H(100, 100);

        // Shift across the road.
        Pick("shift");
        if (tools.Kind != TerrainToolKind.Shift) problems.Add($"picked Shift, tool is {tools.Kind}");
        tools.BrushRadius = 40;
        tools.BrushStrength = 1;
        tools.StrokeForTest(new Vector2(700, RoadZ + 20), new Vector2(700, RoadZ + 20), 120);
        await Frames(4);
        float raised = H(700, RoadZ + 40) - baseH;
        GD.Print($"Demo terrain: Shift raised {raised:0.00} m beside the road, ground under it {H(700, RoadZ) - road:+0.000;-0.000} m off it");
        if (raised < 1) problems.Add($"Shift raised the ground {raised:0.00} m beside the road");
        if (MathF.Abs(H(700, RoadZ) - road) > 0.05f) problems.Add($"ground under the road {H(700, RoadZ) - road:0.00} m off it after Shift");
        if (Network.Graph.Edges.Single().Heights!.At(300) != road) problems.Add("the road moved with the ground");
        if (Network.Undo()) problems.Add("road undo still reaches past a terrain stroke");

        tools.Undo();
        await Frames(4);
        if (!Same(before)) problems.Add("undo didn't put every height back (stroke and shaping back)");
        tools.Redo();
        await Frames(4);
        if (MathF.Abs(H(700, RoadZ + 40) - baseH - raised) > 1e-3f) problems.Add("redo didn't raise it again");
        if (MathF.Abs(H(700, RoadZ) - road) > 0.05f) problems.Add("redo left the ground under the road off it");
        tools.Undo();
        await Frames(4);
        if (!Same(before)) problems.Add("second undo didn't put every height back");

        // A road change ends the terrain undo history.
        tools.StrokeForTest(new Vector2(300, 800), new Vector2(300, 800), 30);
        await Frames(2);
        Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(400, 300)), new Pi(new NumVector2(600, 300))]), profile.ToRules()));
        await Frames(2);
        if (tools.CanUndo) problems.Add("terrain undo still reaches past a road change");
        before = map.Snapshot();

        // Each tool changes the ground away from the roads, and undoes exactly.
        async System.Threading.Tasks.Task Check(string id, string what, Func<bool> changed, Action stroke)
        {
            var at = map.Snapshot();
            Pick(id);
            stroke();
            await Frames(3);
            if (!changed()) problems.Add($"{id}: {what}");
            tools.Undo();
            await Frames(3);
            if (!Same(at)) problems.Add($"{id}: undo didn't put the heights back");
            tools.Redo();
            await Frames(3);
        }
        float flat = H(200, 800);
        await Check("shift", "no hill", () => H(200, 800) > flat + 1,
            () => tools.StrokeForTest(new Vector2(200, 800), new Vector2(200, 800), 120));
        float top = H(200, 800);
        await Check("smooth", "the hill isn't lower", () => H(200, 800) < top - 0.05f,
            () => tools.StrokeForTest(new Vector2(200, 800), new Vector2(200, 800), 60));
        await Check("level", "not levelled to the picked height", () => MathF.Abs(H(200, 800) - flat) < 0.5f, () =>
        {
            tools.StrokeForTest(new Vector2(300, 700), new Vector2(300, 700), 0, alt: true); // pick the flat height
            tools.StrokeForTest(new Vector2(200, 800), new Vector2(200, 800), 240);
        });
        await Check("slope", "no ramp", () => H(250, 650) > flat + 3 && H(250, 650) < flat + 7, () =>
        {
            tools.SlopeAnchor = terrain.MapToWorld(200, 650, flat - terrain.GlobalPosition.Y + 10);
            tools.BrushRadius = 15;
            tools.StrokeForTest(new Vector2(300, 650), new Vector2(200, 650), 240);
        });
        tools.ChannelMode = ChannelMode.FollowGround;
        await Check("channel", "no cut", () => H(250, 950) < flat - 2,
            () => tools.StrokeForTest(new Vector2(150, 950), new Vector2(350, 950), 120));
        tools.ChannelMode = ChannelMode.Graded;
        await Check("channel", "graded: no cut", () => H(150, 1050) < flat - 2, () =>
        {
            tools.StrokeForTest(new Vector2(100, 1050), default, 0); // A
            tools.StrokeForTest(new Vector2(200, 1050), default, 0); // B
            tools.StrokeForTest(new Vector2(200, 1050), default, 0); // cut A → B
        });
        tools.ChannelMode = ChannelMode.FollowGround;

        // Shift: Lower mode lowers, Alt flips it; RMB mid-stroke puts the ground back.
        await Check("shift", "Lower didn't lower", () => H(500, 1000) < flat - 1, () =>
        {
            tools.ShiftMode = ShiftMode.Lower;
            tools.StrokeForTest(new Vector2(500, 1000), new Vector2(500, 1000), 120);
        });
        await Check("shift", "Alt+Lower didn't raise", () => H(600, 1000) > flat + 1,
            () => tools.StrokeForTest(new Vector2(600, 1000), new Vector2(600, 1000), 120, alt: true));
        tools.ShiftMode = ShiftMode.Raise;
        var noCancel = map.Snapshot();
        bool couldUndo = tools.CanUndo;
        tools.StrokeForTest(new Vector2(700, 1000), new Vector2(700, 1000), 120, cancel: true);
        await Frames(3);
        if (!Same(noCancel)) problems.Add("RMB mid-stroke didn't put the ground back");
        if (tools.CanUndo != couldUndo || tools.CanRedo) problems.Add("a cancelled stroke went on the history");

        // Paint and Erase: only the paint changes, undo puts it back, and a road undo doesn't reach past them.
        if (terrain.Splat is not { } splat || hud.Library.Item("paint_dirt") is null) problems.Add("no splat map or no Paint card for dirt");
        else
        {
            uint At() => splat.Get((int)(300 / map.CellSize), (int)(1150 / map.CellSize));
            Network.Apply(g => g.AddSpline(new Alignment([new Pi(new NumVector2(400, 1150)), new Pi(new NumVector2(600, 1150))]), profile.ToRules()));
            await Frames(2);
            var heights = map.Snapshot();
            var paint = splat.Snapshot();
            Pick("paint_dirt");
            if (tools.Kind != TerrainToolKind.Paint) problems.Add($"picked dirt, tool is {tools.Kind}");
            tools.StrokeForTest(new Vector2(300, 1150), new Vector2(300, 1150), 60);
            await Frames(3);
            if (SplatMap.Coverage(At()) < 200) problems.Add($"Paint: coverage {SplatMap.Coverage(At())} under the brush");
            if (!Same(heights)) problems.Add("Paint changed the heights");
            if (Network.Undo()) problems.Add("road undo still reaches past a paint stroke");
            Pick("erase");
            tools.StrokeForTest(new Vector2(300, 1150), new Vector2(300, 1150), 60);
            await Frames(3);
            if (SplatMap.Coverage(At()) > 20) problems.Add($"Erase: coverage {SplatMap.Coverage(At())} left");
            tools.Undo();
            await Frames(3);
            if (SplatMap.Coverage(At()) < 200) problems.Add("undoing Erase didn't bring the paint back");
            tools.Undo();
            await Frames(3);
            if (!splat.Snapshot().AsSpan().SequenceEqual(paint)) problems.Add("undoing Paint didn't put the paint back");
            if (!Same(heights)) problems.Add("Paint undo changed the heights");
        }

        foreach (var p in problems) GD.PrintErr($"Demo terrain: {p}");
        GD.Print(problems.Count == 0 ? "Demo terrain: all ok" : $"Demo terrain: {problems.Count} problem(s)");

        Pick("shift");
        tools.BrushRadius = 30;
        tools.ShowContours = true;
        tools.ForcedCursor = terrain.MapToWorld(760, RoadZ + 25, H(760, RoadZ + 25) - terrain.GlobalPosition.Y);
    }
}
