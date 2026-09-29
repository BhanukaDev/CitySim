using System;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Turns <see cref="RibbonGeometry"/> slices into flat-ribbon meshes draped on the ground (DESIGN.md's placeholder
/// flat-ribbon visual): the preview is a translucent light-blue "ghost" (its outline and dashed legs are drawn by
/// <see cref="SplineOverlay"/>), with an amber halo under it when a corner was clamped; a built spline is its profile
/// colour with a lighter dashed centre line. Simple full rebuilds — no incremental updates until S11.
/// </summary>
public sealed class RibbonRenderer
{
    private const float Lift = 0.05f; // avoids z-fighting with the terrain
    private const float CentreWidth = 0.5f;
    private const float DashOn = 3f, DashOff = 3f;

    private static readonly Color GhostFill = new(0.42f, 0.72f, 1f, 0.45f);
    private static readonly Color Halo = SplineOverlay.Warn with { A = 0.45f };

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private MeshInstance3D? _preview;

    public RibbonRenderer(Node3D parent, IGround ground)
    {
        _parent = parent;
        _ground = ground;
    }

    /// <summary>Rebuilds the preview ghost from the in-progress alignment. Null clears it.</summary>
    public void SetPreview(Alignment? alignment, float width)
    {
        if (alignment is null || alignment.Curve.Length <= 0f)
        {
            _preview?.QueueFree();
            _preview = null;
            return;
        }
        _preview ??= NewInstance();
        var mesh = new ArrayMesh();
        if (alignment.AnyClamped) AddSurface(mesh, Strip(alignment.Curve, width + 5f, 0, Lift * 0.5f), Halo);
        AddSurface(mesh, Strip(alignment.Curve, width, 0, Lift), GhostFill);
        _preview.Mesh = mesh;
    }

    /// <summary>Adds one permanent built ribbon in the profile's colour. Never rebuilt after.</summary>
    public void AddBuilt(SplineProfile profile, Alignment alignment)
    {
        var node = NewInstance();
        var mesh = new ArrayMesh();
        AddSurface(mesh, Strip(alignment.Curve, profile.Width, 0, Lift), profile.Color, opaque: true);
        AddSurface(mesh, Strip(alignment.Curve, CentreWidth, DashOn, Lift * 2), profile.Color.Lightened(0.55f), opaque: true);
        node.Mesh = mesh;
    }

    private MeshInstance3D NewInstance()
    {
        var node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _parent.AddChild(node);
        return node;
    }

    private static void AddSurface(ArrayMesh mesh, SurfaceTool? st, Color color, bool opaque = false)
    {
        if (st is null) return;
        st.Commit(mesh);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = opaque ? BaseMaterial3D.TransparencyEnum.Disabled : BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = color,
        });
    }

    /// <summary>A ribbon <paramref name="width"/> wide along the curve; dashed (<paramref name="dash"/> on, the same
    /// off) when <paramref name="dash"/> &gt; 0. Null when there's nothing to draw.</summary>
    private SurfaceTool? Strip(Curve curve, float width, float dash, float lift)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = 0;
        if (dash <= 0) quads += Span(st, curve, 0, curve.Length, width, lift);
        else
            for (float s = DashOff / 2; s < curve.Length; s += DashOn + DashOff)
                quads += Span(st, curve, s, MathF.Min(s + DashOn, curve.Length), width, lift);
        return quads > 0 ? st : null;
    }

    private int Span(SurfaceTool st, Curve curve, float s0, float s1, float width, float lift)
    {
        float half = width / 2f;
        int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 2f));
        Vector3? l0 = null, r0 = null;
        for (int k = 0; k <= n; k++)
        {
            var sample = curve.Sample(s0 + (s1 - s0) * k / n);
            var left = SplineMath.Left(sample.Tangent) * half;
            var l1 = Drape(sample.Position + left, lift);
            var r1 = Drape(sample.Position - left, lift);
            if (l0 is { } a && r0 is { } b)
            {
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(l1);
                st.AddVertex(l1); st.AddVertex(b); st.AddVertex(r1);
            }
            l0 = l1;
            r0 = r1;
        }
        return n;
    }

    private Vector3 Drape(NumVector2 plan, float lift) => new(plan.X, _ground.GetHeight(plan) + lift, plan.Y);
}
