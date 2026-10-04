using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CitySim.Splines;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Roads;

/// <summary>
/// <c>--bake-road-thumbnails[=&lt;road id&gt;]</c>, a dev tool: renders every road type in <c>content/roads/types/</c>
/// (hidden ones too) as a short straight piece of real road (<see cref="RoadVisual"/>, the road style's materials) on
/// grass, from a three-quarter view, into <c>content/roads/thumbnails/&lt;id&gt;.png</c>, and points the road's
/// <see cref="Content.BuildItem.Icon"/> at it. A road whose Icon is already something else (hand-made art) gets its
/// PNG but keeps its Icon. Needs a window (not <c>--headless</c>), and runs <c>--import</c> at the end, since a road
/// type linked to a PNG Godot hasn't imported yet won't load. The PNGs are checked in, so the game never renders thumbnails itself.
/// </summary>
public partial class RoadThumbnailBaker : Node
{
    public const string Folder = "res://content/roads/thumbnails";
    public const string TypesFolder = "res://content/roads/types";
    private const string StylePath = "res://content/roads/styles/default.tres";
    // Twice the card's picture (120 × 70), so it stays sharp when scaled down.
    private static readonly Vector2I Size = new(240, 140);
    private const float Length = 240f; // the road runs well past the frame, so no dead-end caps show

    private readonly string? _only;

    public RoadThumbnailBaker(string? only = null) => _only = only;

    public override void _Ready() => Callable.From(() => { _ = Run(); }).CallDeferred();

    private async Task Run()
    {
        if (DisplayServer.GetName() == "headless")
        {
            GD.PrintErr("Road thumbnails: needs a window to render, run without --headless");
            GetTree().Quit(1);
            return;
        }
        if (ResourceLoader.Load(StylePath) is not RoadStyle style)
        {
            GD.PrintErr($"Road thumbnails: no road style at {StylePath}");
            GetTree().Quit(1);
            return;
        }
        var roads = new List<(RoadType Road, string Path)>();
        foreach (string file in DirAccess.GetFilesAt(TypesFolder).Order())
            if (file.EndsWith(".tres") && ResourceLoader.Load($"{TypesFolder}/{file}") is RoadType r && (_only is null || r.Id == _only))
                roads.Add((r, $"{TypesFolder}/{file}"));
        if (roads.Count == 0)
        {
            GD.PrintErr($"Road thumbnails: no road types{(_only is null ? "" : $" with id \"{_only}\"")}");
            GetTree().Quit(1);
            return;
        }

        var (viewport, stage, camera) = Studio();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Folder));
        string[] ids = roads.Select(r => r.Road.Id).ToArray();
        var defs = roads.ToDictionary(r => r.Road.Id, r => r.Road.ToDef());
        int linked = 0;
        foreach (var (road, path) in roads)
        {
            foreach (var child in stage.GetChildren()) child.Free();
            var def = defs[road.Id];
            var graph = new SplineGraph();
            graph.AddSpline(new Alignment([new Pi(new NumVector2(-Length / 2, 0)), new Pi(new NumVector2(Length / 2, 0))]),
                RoadProfiles.From(road, ids).ToRules());
            var visual = new RoadVisual(stage, new FlatGround(), style, id => defs.GetValueOrDefault(id));
            visual.SetNetwork(graph, new Dictionary<int, JunctionFootprint>(), [], null);
            Frame(camera, def.Width);

            // One frame to build the scene, one to render it with everything (shaders compiled, shadows) in place.
            for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = viewport.GetTexture().GetImage();
            string png = $"{Folder}/{road.Id}.png";
            image.SavePng(ProjectSettings.GlobalizePath(png));
            bool link = LinkIcon(path, png);
            if (link) linked++;
            GD.Print($"Road thumbnails: {road.Id} → {png}{(link ? "" : " (keeps its own Icon)")}");
        }
        // A road type whose Icon points at a PNG Godot hasn't imported yet doesn't load at all, so import right away.
        GD.Print($"Road thumbnails: {roads.Count} rendered, {linked} linked as Icon; importing");
        int code = OS.Execute(OS.GetExecutablePath(), ["--headless", "--path", ProjectSettings.GlobalizePath("res://"), "--import"]);
        GD.Print(code == 0 ? "Road thumbnails: done" : $"Road thumbnails: --import exited with {code}, run it by hand");
        GetTree().Quit(code);
    }

    /// <summary>A world of its own: a sun, a sky like the game's, a grass plane, a camera and a node the road goes in.</summary>
    private (SubViewport, Node3D, Camera3D) Studio()
    {
        var viewport = new SubViewport
        {
            Size = Size,
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa8X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.32f, 0.55f, 0.88f),
            SkyHorizonColor = new Color(0.80f, 0.84f, 0.95f),
            GroundBottomColor = new Color(0.62f, 0.68f, 0.7f),
            GroundHorizonColor = new Color(0.80f, 0.84f, 0.95f),
        };
        viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
                AdjustmentEnabled = true,
                AdjustmentContrast = 1.05f,
                AdjustmentSaturation = 1.1f,
            },
        });
        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.1f, DirectionalShadowMaxDistance = 200 };
        viewport.AddChild(sun);
        sun.RotationDegrees = new Vector3(-50, -35, 0);

        // The terrain package's default grass tint, flat: the road is the subject.
        var grass = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.45f, 0.27f), Roughness = 1 };
        viewport.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1000, 1000), Material = grass } });

        var stage = new Node3D { Name = "Stage" };
        viewport.AddChild(stage);
        var camera = new Camera3D { Fov = 35, Current = true, Far = 2000 };
        viewport.AddChild(camera);
        return (viewport, stage, camera);
    }

    /// <summary>Looks down the road at a slant from one side, close enough that its width fills most of the frame.</summary>
    private static void Frame(Camera3D camera, float width)
    {
        float distance = width * 1.9f + 4f;
        var look = new Vector3(0, 0, 0);
        var from = Basis.FromEuler(new Vector3(Mathf.DegToRad(-48), Mathf.DegToRad(-28), 0)) * new Vector3(0, 0, distance);
        camera.Position = look + from;
        camera.LookAt(look, Vector3.Up);
    }

    /// <summary>Sets the road's Icon to the PNG in its .tres text, unless it already has an Icon that isn't a baked
    /// thumbnail. Edits the text so the rest of the file stays as written. False when it keeps its own Icon.</summary>
    private static bool LinkIcon(string tresPath, string png)
    {
        string file = ProjectSettings.GlobalizePath(tresPath);
        var lines = File.ReadAllLines(file).ToList();
        const string id = "thumbnail";
        int icon = lines.FindIndex(l => l.StartsWith("Icon = "));
        if (icon >= 0) return lines[icon] == $"Icon = ExtResource(\"{id}\")";

        int lastExt = lines.FindLastIndex(l => l.StartsWith("[ext_resource"));
        int script = lines.FindIndex(l => l.StartsWith("script = "));
        if (lastExt < 0 || script < 0) { GD.PushWarning($"Road thumbnails: {tresPath} doesn't look like a road type file"); return false; }
        lines.Insert(script + 1, $"Icon = ExtResource(\"{id}\")");
        lines.Insert(lastExt + 1, $"[ext_resource type=\"Texture2D\" path=\"{png}\" id=\"{id}\"]");
        File.WriteAllLines(file, lines);
        return true;
    }

    private sealed class FlatGround : IGround
    {
        public bool Raycast(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, out System.Numerics.Vector3 hit)
        {
            hit = default;
            if (MathF.Abs(direction.Y) < 1e-6f) return false;
            float t = -origin.Y / direction.Y;
            hit = origin + direction * t;
            return t >= 0;
        }

        public float GetHeight(NumVector2 planPosition) => 0f;
    }
}
