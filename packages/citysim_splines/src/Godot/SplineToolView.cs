using System;
using System.Collections.Generic;
using System.Linq;
using CitySim.CameraSystem;
using CitySim.TerrainSystem;
using Godot;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;

namespace CitySim.Splines.Godot;

/// <summary>Modifier keys a scripted frame can hold down (<see cref="SplineToolView.ForcedModifiers"/>).</summary>
[Flags]
public enum DrawModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Space = 8 }

/// <summary>
/// What the spline tools (Draw, Edit) share about the view: the ground point under the mouse, plan ↔ screen, screen
/// pixels → metres for catch distances, the held modifier keys, and the built network as snap sources. A scripted
/// frame replaces the mouse with <see cref="ForcedPlanCursor"/> and holds keys with <see cref="ForcedModifiers"/>.
/// </summary>
public sealed class SplineToolView
{
    private readonly Node _owner;

    public SplineToolView(Node owner) => _owner = owner;

    public Terrain? Terrain { get; set; }
    public CityCamera? CityCamera { get; set; }
    public IGround? Ground { get; set; }

    /// <summary>The current ground hit, world space. Null off the terrain or over UI.</summary>
    public NumVector3? Cursor { get; private set; }
    /// <summary>Scripted-demo override: a plan-space (map metres) position that replaces the mouse raycast.</summary>
    public NumVector2? ForcedPlanCursor { get; set; }
    /// <summary>Scripted-demo override: modifier keys treated as held (added to the real ones).</summary>
    public DrawModifiers ForcedModifiers { get; set; }

    public void UpdateCursor()
    {
        if (Terrain is null || Ground is null) { Cursor = null; return; }
        if (ForcedPlanCursor is { } forced)
        {
            var world = Terrain.MapToWorld(forced.X, forced.Y, Ground.GetHeight(forced));
            Cursor = new NumVector3(world.X, world.Y, world.Z);
            return;
        }
        Cursor = null;
        if (CityCamera?.Camera is not { } cam) return;
        var viewport = _owner.GetViewport();
        if (viewport.GuiGetHoveredControl() is not null) return;
        var mouse = viewport.GetMousePosition();
        var origin = ToNumerics(cam.ProjectRayOrigin(mouse));
        var dir = ToNumerics(cam.ProjectRayNormal(mouse));
        if (Ground.Raycast(origin, dir, out var hit)) Cursor = hit;
    }

    public DrawModifiers Modifiers()
    {
        var m = ForcedModifiers;
        if (Input.IsKeyPressed(Key.Ctrl)) m |= DrawModifiers.Ctrl;
        if (Input.IsKeyPressed(Key.Shift)) m |= DrawModifiers.Shift;
        if (Input.IsKeyPressed(Key.Alt)) m |= DrawModifiers.Alt;
        if (Input.IsKeyPressed(Key.Space)) m |= DrawModifiers.Space;
        return m;
    }

    /// <summary>Every edge of <paramref name="graph"/> as a snap source, but <paramref name="except"/>; extension
    /// guides only leave dead ends.</summary>
    public static List<SnapCandidate> Candidates(SplineGraph graph, IReadOnlySet<int>? except = null) =>
        graph.Edges.Where(e => except?.Contains(e.Id) != true).Select(e => new SnapCandidate(e.Alignment, e.Rules.Width,
            OpenStart: graph.Node(e.Start).Edges.Count == 1, OpenEnd: graph.Node(e.End).Edges.Count == 1)
        {
            HiddenCorners = Enumerable.Range(1, Math.Max(0, e.Alignment.Pis.Count - 2)).Where(i => !graph.ShowsRoadPoint(e, i)).ToList(),
        }).ToList();

    /// <summary>Every point a road can connect to, shown as a dot in every mode as a guide: the nodes, and each
    /// corner's point on the road (<see cref="Alignment.RoadPoint"/>: a joint between chained curves, an arc's
    /// middle).</summary>
    public static List<NumVector2> Points(SplineGraph graph) =>
        graph.Nodes.Select(n => n.Position).Concat(RoadPoints(graph)).ToList();

    /// <summary>Each corner's point on the road, inside the edges, but those a junction stands in for
    /// (<see cref="SplineGraph.ShowsRoadPoint"/>).</summary>
    public static IEnumerable<NumVector2> RoadPoints(SplineGraph graph) =>
        graph.Edges.SelectMany(e => Enumerable.Range(1, Math.Max(0, e.Alignment.Pis.Count - 2))
            .Where(i => graph.ShowsRoadPoint(e, i)).Select(e.Alignment.RoadPoint));

    /// <summary>Converts a screen-pixel distance to plan units at <paramref name="worldHit"/>'s depth, so the catch
    /// distance feels the same at every zoom (DESIGN.md → Snapping and guides).</summary>
    public float PixelsToPlanUnits(float pixels, NumVector3 worldHit)
    {
        var world = new Vector3(worldHit.X, worldHit.Y, worldHit.Z);
        if (CityCamera?.Camera is not { } cam || cam.IsPositionBehind(world)) return pixels;
        var origin = cam.UnprojectPosition(world);
        var offset = cam.UnprojectPosition(world + new Vector3(1f, 0, 0));
        float pxPerMeter = origin.DistanceTo(offset);
        return pxPerMeter > 1e-3f ? pixels / pxPerMeter : pixels;
    }

    /// <summary>A plan point draped on the ground, on screen (null when behind the camera) — for the overlay.</summary>
    public Vector2? ProjectPlan(NumVector2 plan)
    {
        if (Terrain is null || Ground is null || CityCamera?.Camera is not { } cam) return null;
        var o = Terrain.GlobalPosition;
        var world = new Vector3(o.X + plan.X, Ground.GetHeight(plan), o.Z + plan.Y);
        return cam.IsPositionBehind(world) ? null : cam.UnprojectPosition(world);
    }

    /// <summary>The mouse on screen, or where the forced cursor projects to in a scripted frame.</summary>
    public Vector2 MouseScreen() =>
        ForcedPlanCursor is { } forced && ProjectPlan(forced) is { } p ? p : _owner.GetViewport().GetMousePosition();

    public NumVector2 PlanOf(NumVector3 worldHit)
    {
        var map = Terrain!.WorldToMap(new Vector3(worldHit.X, worldHit.Y, worldHit.Z));
        return new NumVector2(map.X, map.Y);
    }

    private static NumVector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
}
