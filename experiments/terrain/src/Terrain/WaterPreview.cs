using System.Threading;
using System.Threading.Tasks;
using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>
/// Placement preview for Lake, River and Sea sources: tints the area the source would flood (<see cref="WaterFlood"/>,
/// worked out on a worker) as a flat sheet at the water's surface, cut by the ground, with a label giving its area and
/// volume, or where it spills when the level is above the hollow's rim (then the sheet is orange).
/// </summary>
public partial class WaterPreview : Node3D
{
    private static readonly Color Fills = new(0.3f, 0.75f, 1f, 0.45f);
    private static readonly Color Spills = new(1f, 0.6f, 0.2f, 0.45f);

    private Terrain? _terrain;
    private WaterSim? _sim;
    private MeshInstance3D _sheet = null!;
    private ShaderMaterial _material = null!;
    private Label3D _label = null!;
    private WaterSource? _shown;
    private CancellationTokenSource? _job;
    // Ground and depression fill on the water grid for one height version, worked out once per edit and shared by the
    // hover jobs (which are cancelled as the cursor moves; this isn't, so it finishes while the mouse keeps moving).
    private Task<(float[] Ground, float[] Filled)>? _fill;
    private int _fillVersion = -1;

    private const string ShaderCode = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
        uniform sampler2D mask : filter_linear, repeat_disable;
        uniform vec4 tint : source_color;
        void fragment() {
            float m = texture(mask, UV).r;
            if (m < 0.5) discard;
            ALBEDO = tint.rgb;
            ALPHA = tint.a;
        }
        """;

    public void Init(Terrain terrain, WaterSim sim)
    {
        _terrain = terrain;
        _sim = sim;
        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode }, RenderPriority = 15 };
        _sheet = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One },
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_sheet);
        _label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FixedSize = true,
            PixelSize = 0.0009f,
            FontSize = 30,
            OutlineSize = 10,
            RenderPriority = 30,
            OutlineRenderPriority = 29,
            Visible = false,
        };
        AddChild(_label);
    }

    /// <summary>Previews <paramref name="source"/> (local metres), or hides the preview when null.</summary>
    public void Show(WaterSource? source)
    {
        if (source is null || source.Kind == WaterSourceKind.Stream)
        {
            Hide();
            _job?.Cancel();
            _shown = null;
            return;
        }
        if (_sim is not { } sim || _terrain?.Map is not { } map) return;
        // Same cell and level as what's shown (or being worked out): nothing to do.
        if (_shown is { } old && old.Kind == source.Kind && Mathf.Abs(old.Level - source.Level) < 0.05f
            && Mathf.Abs(old.Radius - source.Radius) < 0.5f && new Vector2(old.X - source.X, old.Z - source.Z).Length() < sim.MarksCellSize * 0.5f)
        {
            Visible = true;
            return;
        }
        _shown = source;
        Visible = true;
        _job?.Cancel();
        var job = _job = new CancellationTokenSource();
        int version = _terrain.HeightVersion;
        // On the ground-marks grid: a preview needs no finer, and the flood fill is dense.
        int w = sim.MarksWidth, d = sim.MarksDepth, factor = sim.MarksTerrainFactor;
        float cell = sim.MarksCellSize;
        if (_fill is null || _fillVersion != version)
        {
            _fillVersion = version;
            _fill = Task.Run(() =>
            {
                var ground = WaterFlood.Ground(map, factor, w, d);
                return (ground, WaterFlood.Filled(ground, w, d)!);
            });
        }
        var fill = _fill;
        Task.Run(async () =>
        {
            var (ground, filled) = await fill;
            if (job.IsCancellationRequested) return;
            var result = WaterFlood.Compute(ground, filled, w, d, cell, source, job.Token);
            if (job.IsCancellationRequested) return;
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || job.IsCancellationRequested || _terrain?.Map != map) return;
                Apply(source, result, cell);
            }).CallDeferred();
        }, job.Token).ContinueWith(t =>
        {
            if (t.IsFaulted) Callable.From(() => GD.PushError($"Water preview failed: {t.Exception!.GetBaseException()}")).CallDeferred();
        });
    }

    private void Apply(WaterSource source, WaterFloodResult? r, float cell)
    {
        string kind = WaterSource.Label(source.Kind);
        float y;
        if (r is null)
        {
            _sheet.Visible = false;
            y = (_terrain?.GetHeight(source.X, source.Z) ?? 0f) + 6f;
            _label.Text = source.Kind == WaterSourceKind.Sea
                ? $"Sea at {source.Level:0.0} m: no ground below it at the map edge"
                : $"{kind} at {source.Level:0.0} m\nNo hollow here: the water runs downhill";
        }
        else
        {
            var image = Image.CreateFromData(r.Width, r.Depth, false, Image.Format.R8, r.Mask);
            _material.SetShaderParameter("mask", ImageTexture.CreateFromImage(image));
            _material.SetShaderParameter("tint", r.Spills ? Spills : Fills);
            // Texel centres on the cells: the sheet reaches half a cell past the outer ones.
            float sx = r.Width * cell, sz = r.Depth * cell;
            _sheet.Scale = new Vector3(sx, 1f, sz);
            _sheet.Position = new Vector3((r.X0 - 0.5f) * cell + sx * 0.5f, r.Surface + 0.05f, (r.Z0 - 0.5f) * cell + sz * 0.5f);
            _sheet.Visible = true;
            y = r.Surface + 6f;
            string size = $"{Area(r.Area)} · {Volume(r.Volume)}";
            _label.Text = r.Spills
                ? $"{kind} at {source.Level:0.0} m\nSpills over at {r.Surface:0.0} m · {size}"
                : $"{kind} at {source.Level:0.0} m\n{size}";
        }
        var at = source.Kind == WaterSourceKind.Sea && r is not null
            ? _sheet.Position with { Y = y }
            : new Vector3(source.X, y, source.Z);
        _label.Position = at;
        _label.Modulate = r is { Spills: true } ? new Color(1f, 0.85f, 0.6f) : Colors.White;
        _label.Visible = true;
        Position = _terrain?.GlobalPosition ?? Vector3.Zero;
    }

    public static string Area(float m2) => m2 >= 1e6f ? $"{m2 / 1e6f:0.##} km²" : $"{m2 / 1e4f:0.##} ha";

    public static string Volume(float m3) => m3 >= 1e6f ? $"{m3 / 1e6f:0.##} million m³" : $"{m3:#,0} m³";
}
