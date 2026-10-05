using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitySim.Roads;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Sculpt;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Terraform;

/// <summary>
/// <c>--bake-terrain-thumbnails[=&lt;tool id&gt;]</c>, a dev tool like <see cref="RoadThumbnailBaker"/> (same studio, angle and
/// size): renders each terrain tool in <c>content/terrain/tools/</c> as a patch of ground showing what it makes, cut with
/// the real sculpt ops: a mound (Shift), a flat pad in hills (Level), a smoothed disc in rough ground (Smooth), a ramp up
/// to a plateau (Slope), a river channel with water in it (Channel). Brush tools show their ring; every picture has the contour lines the tools turn on with C. Writes
/// <c>content/terrain/thumbnails/&lt;id&gt;.png</c> and points the tool's Icon at it. Needs a window; imports at the end.
/// </summary>
public partial class TerrainThumbnailBaker : Node
{
    public const string Folder = "res://addons/citysim_roads/content/terrain/thumbnails";
    public const string ToolsFolder = "res://addons/citysim_roads/content/terrain/tools";
    private const int Cells = 160; // a 160 m patch, 1 m cells: fills the frame past its edges
    private const float Tick = 1f / 60f;
    private static readonly NumVector2 Centre = new(Cells / 2f, Cells / 2f);
    private static readonly Color Dirt = new(0.47f, 0.40f, 0.30f);
    private static readonly Color Rock = new(0.50f, 0.49f, 0.47f);

    private readonly string? _only;

    public TerrainThumbnailBaker(string? only = null) => _only = only;

    public override void _Ready() => Callable.From(() => { _ = Run(); }).CallDeferred();

    private async Task Run()
    {
        if (DisplayServer.GetName() == "headless")
        {
            GD.PrintErr("Terrain thumbnails: needs a window to render, run without --headless");
            GetTree().Quit(1);
            return;
        }
        var tools = new List<(TerrainTool Tool, string Path)>();
        foreach (string file in DirAccess.GetFilesAt(ToolsFolder).Order())
            if (file.EndsWith(".tres") && ResourceLoader.Load($"{ToolsFolder}/{file}") is TerrainTool t && (_only is null || t.Id == _only))
                tools.Add((t, $"{ToolsFolder}/{file}"));
        if (tools.Count == 0)
        {
            GD.PrintErr($"Terrain thumbnails: no terrain tools{(_only is null ? "" : $" with id \"{_only}\"")}");
            GetTree().Quit(1);
            return;
        }

        var (viewport, stage, camera) = RoadThumbnailBaker.Studio(this, grass: false);
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Folder));
        foreach (var (tool, path) in tools)
        {
            foreach (var child in stage.GetChildren()) child.Free();
            var map = new HeightMap(Cells + 1, Cells + 1, 1f);
            var (ring, water, frame) = Sculpt(map, tool.Tool);
            stage.AddChild(Ground(map));
            if (ring is { } r) stage.AddChild(Ring(map, r.Centre, r.Radius));
            if (water is { } level) stage.AddChild(Water(level));
            RoadThumbnailBaker.Frame(camera, frame, new Vector3(0, map.SampleHeight(Centre.X, Centre.Y), 0));

            for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string png = $"{Folder}/{tool.Id}.png";
            viewport.GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath(png));
            bool link = RoadThumbnailBaker.LinkIcon(path, png);
            GD.Print($"Terrain thumbnails: {tool.Id} → {png}{(link ? "" : " (keeps its own Icon)")}");
        }
        GD.Print($"Terrain thumbnails: {tools.Count} rendered; importing");
        int code = OS.Execute(OS.GetExecutablePath(), ["--headless", "--path", ProjectSettings.GlobalizePath("res://"), "--import"]);
        GD.Print(code == 0 ? "Terrain thumbnails: done" : $"Terrain thumbnails: --import exited with {code}, run it by hand");
        GetTree().Quit(code);
    }

    /// <summary>Shapes the patch the way the tool would and says what to draw on it: the brush ring, a water level, and the
    /// width to frame.</summary>
    private static ((NumVector2 Centre, float Radius)? Ring, float? Water, float Frame) Sculpt(HeightMap map, string tool)
    {
        void Ground(Func<float, float, float> h)
        {
            for (int z = 0; z <= Cells; z++)
                for (int x = 0; x <= Cells; x++)
                    map[x, z] = h(x, z);
            map.Invalidate();
        }
        float Hills(float x, float z, float amp) =>
            amp * (MathF.Sin(x * 0.07f + 1f) * MathF.Cos(z * 0.055f) + 0.6f * MathF.Sin(x * 0.12f + z * 0.09f));
        void Repeat(int ticks, Action step)
        {
            for (int i = 0; i < ticks; i++) step();
            map.Invalidate();
        }

        switch (tool)
        {
            case TerrainTool.ShiftTool:
            {
                var brush = new Brush(14f, 1f);
                Ground((_, _) => 0f);
                for (int i = 0; i < 6000 && map.SampleHeight(Centre.X, Centre.Y) < 10f; i++)
                    SculptOps.Shift(map, Centre, brush, 1f, Tick);
                map.Invalidate();
                return ((Centre, brush.Radius), null, 22f);
            }
            case TerrainTool.LevelTool:
            {
                var brush = new Brush(15f, 1f);
                Ground((x, z) => Hills(x, z, 5f));
                float target = map.SampleHeight(Centre.X, Centre.Y);
                Repeat(400, () => SculptOps.Level(map, Centre, brush, target, Tick));
                return ((Centre, brush.Radius), null, 26f);
            }
            case TerrainTool.SmoothTool:
            {
                var brush = new Brush(15f, 1f);
                Ground((x, z) => Hills(x, z, 1.5f) + 0.9f * MathF.Sin(x * 0.75f + 0.3f * z) * MathF.Sin(z * 0.68f - 0.2f * x)
                                 + 0.5f * MathF.Sin(x * 1.3f) * MathF.Cos(z * 1.1f));
                Repeat(600, () => SculptOps.Smooth(map, Centre, brush, Tick));
                return ((Centre, brush.Radius), null, 22f);
            }
            case TerrainTool.SlopeTool:
            {
                // A plateau 8 m up behind, a ramp from its edge down onto the low ground in front.
                const float top = 8f;
                Ground((_, z) => top * Math.Clamp((Centre.Y - 12f - z) / 3f + 0.5f, 0f, 1f));
                var a = new NumVector2(Centre.X, Centre.Y - 14f);
                var b = new NumVector2(Centre.X, Centre.Y + 20f);
                var brush = new Brush(7f, 1f);
                Repeat(600, () =>
                {
                    for (float t = 0; t <= 1f; t += 0.04f)
                        SculptOps.Slope(map, NumVector2.Lerp(a, b, t), brush, a, top, b, 0f, Tick);
                });
                return (null, null, 36f);
            }
            case TerrainTool.ChannelTool:
            {
                // A winding U channel across gentle ground, cut 3 m below the ground on its line, water 1 m below the top.
                Ground((x, z) => Hills(x, z, 0.4f));
                var profile = new ChannelProfile(ChannelShape.Rounded, 12f, 3f);
                var pts = Enumerable.Range(0, 81).Select(i =>
                {
                    float t = i / 80f;
                    return new NumVector2(10f + 140f * t, Centre.Y + 14f * MathF.Sin(t * MathF.Tau * 1.2f));
                }).ToList();
                float reference = pts.Min(p => map.SampleHeight(p.X, p.Y));
                for (int i = 1; i < pts.Count; i++)
                    ChannelOps.CarveSegment(map, new ChannelPoint(pts[i - 1], reference), new ChannelPoint(pts[i], reference),
                        profile, ChannelOps.DefaultBankDegrees, 1f);
                map.Invalidate();
                return (null, reference - 1f, 24f);
            }
            default:
                Ground((_, _) => 0f);
                return (null, null, 22f);
        }
    }

    private static Vector3 At(HeightMap map, float x, float z, float lift = 0f) =>
        new(x - Centre.X, map.SampleHeight(x, z) + lift, z - Centre.Y);

    /// <summary>The patch as a mesh: grass, dirt on slopes, rock where steep (vertex colours, like the terrain's tints).</summary>
    private static MeshInstance3D Ground(HeightMap map)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 Normal(int x, int z)
        {
            float hx = map[Math.Min(x + 1, Cells), z] - map[Math.Max(x - 1, 0), z];
            float hz = map[x, Math.Min(z + 1, Cells)] - map[x, Math.Max(z - 1, 0)];
            return new Vector3(-hx, 2f, -hz).Normalized();
        }
        void Vertex(int x, int z)
        {
            var n = Normal(x, z);
            float steep = 1f - n.Y;
            var c = RoadThumbnailBaker.Grass.Lerp(Dirt, Mathf.SmoothStep(0.06f, 0.22f, steep)).Lerp(Rock, Mathf.SmoothStep(0.3f, 0.5f, steep));
            st.SetColor(c);
            st.SetNormal(n);
            st.AddVertex(new Vector3(x - Centre.X, map[x, z], z - Centre.Y));
        }
        for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                Vertex(x, z); Vertex(x + 1, z); Vertex(x + 1, z + 1);
                Vertex(x, z); Vertex(x + 1, z + 1); Vertex(x, z + 1);
            }
        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = GroundShader } },
        };
    }

    /// <summary>Vertex colours lit like the road pictures' grass, with the game's contour lines on top (the terrain SDK's
    /// formula, every <see cref="TerrainToolController.ContourInterval"/> metres, every 5th bolder), a little wider since
    /// the picture is shown at half size.</summary>
    private static readonly string GroundShader = $$"""
        shader_type spatial;
        render_mode cull_disabled;
        const float INTERVAL = {{TerrainToolController.ContourInterval.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture)}};
        const vec3 CONTOUR = vec3(0.12, 0.09, 0.06);
        varying float height;
        void vertex() { height = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y; }
        void fragment() {
            vec3 col = pow(COLOR.rgb, vec3(2.2)); // sRGB vertex colours
            float c = height / INTERVAL + 0.5; // the scenes' flat ground (0 m, 8 m) between lines, not on one
            float fw = max(fwidth(c), 1e-4);
            float minor = 1.0 - min(abs(fract(c - 0.5) - 0.5) / (fw * 1.5), 1.0);
            float cm = c / 5.0;
            float fwm = max(fwidth(cm), 1e-4);
            float major = 1.0 - min(abs(fract(cm - 0.5) - 0.5) / (fwm * 2.4), 1.0);
            minor *= 1.0 - smoothstep(0.2, 0.5, fw);
            major *= 1.0 - smoothstep(0.2, 0.5, fwm);
            ALBEDO = mix(col, pow(CONTOUR, vec3(2.2)), max(minor * 0.45, major * 0.75));
            ROUGHNESS = 1.0;
        }
        """;

    /// <summary>The brush ring as the game draws it: an accent line on the ground, with a dark edge so it reads on grass.</summary>
    private static MeshInstance3D Ring(HeightMap map, NumVector2 c, float radius)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Band(float r0, float r1, Color col, float lift)
        {
            const int k = 96;
            for (int i = 0; i < k; i++)
            {
                float a0 = MathF.Tau * i / k, a1 = MathF.Tau * (i + 1) / k;
                Vector3 P(float a, float r) => At(map, c.X + MathF.Cos(a) * r, c.Y + MathF.Sin(a) * r, lift);
                foreach (var v in new[] { P(a0, r0), P(a0, r1), P(a1, r1), P(a0, r0), P(a1, r1), P(a1, r0) })
                {
                    st.SetColor(col);
                    st.AddVertex(v);
                }
            }
        }
        Band(radius - 0.45f, radius + 0.45f, new Color(0.05f, 0.07f, 0.08f, 0.55f), 0.12f);
        // Bluer than the UI accent: the tonemapper greys an unshaded colour out (as in the Lane Links picture).
        Band(radius - 0.22f, radius + 0.22f, new Color(0.1f, 0.6f, 1f), 0.15f);
        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
    }

    /// <summary>A still water plane at <paramref name="level"/>: the ground hides it everywhere but in the cut.</summary>
    private static MeshInstance3D Water(float level) => new()
    {
        Mesh = new PlaneMesh { Size = new Vector2(Cells, Cells) },
        Position = new Vector3(0, level, 0),
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.22f, 0.26f, 0.88f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.08f,
            Metallic = 0.2f,
        },
    };
}
