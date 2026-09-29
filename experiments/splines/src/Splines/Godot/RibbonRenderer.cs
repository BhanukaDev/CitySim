using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Turns <see cref="RibbonGeometry"/> slices into a flat-ribbon mesh draped on the ground (DESIGN.md's placeholder
/// flat-ribbon visual): one rebuilt-wholesale blue preview while drawing, and one permanent mesh per finished
/// spline in its profile's colour. Simple full rebuilds, like the terrain package's debug markers — no incremental
/// updates until S11.
/// </summary>
public sealed class RibbonRenderer
{
    private const float Lift = 0.05f; // avoids z-fighting with the terrain

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private MeshInstance3D? _preview;

    public RibbonRenderer(Node3D parent, IGround ground)
    {
        _parent = parent;
        _ground = ground;
    }

    /// <summary>Rebuilds the blue preview ribbon from the in-progress alignment. Null clears it.</summary>
    public void SetPreview(Alignment? alignment, float width)
    {
        if (alignment is null || alignment.Curve.Length <= 0f)
        {
            _preview?.QueueFree();
            _preview = null;
            return;
        }
        _preview ??= NewInstance();
        _preview.Mesh = BuildMesh(alignment.Curve, width);
        _preview.MaterialOverride = Material(new Color(0.25f, 0.55f, 1f, 0.55f), unshadedAlpha: true);
    }

    /// <summary>Adds one permanent built ribbon in the profile's colour. Never rebuilt after.</summary>
    public void AddBuilt(SplineProfile profile, Alignment alignment)
    {
        var node = NewInstance();
        node.Mesh = BuildMesh(alignment.Curve, profile.Width);
        node.MaterialOverride = Material(profile.Color, unshadedAlpha: false);
    }

    private MeshInstance3D NewInstance()
    {
        var node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _parent.AddChild(node);
        return node;
    }

    private static StandardMaterial3D Material(Color color, bool unshadedAlpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = unshadedAlpha ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = color,
    };

    private ArrayMesh BuildMesh(Curve curve, float width)
    {
        var slices = RibbonGeometry.BuildSlices(curve, width);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i + 1 < slices.Count; i++)
        {
            var (l0, r0) = (Drape(slices[i].Left), Drape(slices[i].Right));
            var (l1, r1) = (Drape(slices[i + 1].Left), Drape(slices[i + 1].Right));
            st.AddVertex(l0); st.AddVertex(r0); st.AddVertex(l1);
            st.AddVertex(l1); st.AddVertex(r0); st.AddVertex(r1);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    private Vector3 Drape(NumVector2 plan) => new(plan.X, _ground.GetHeight(plan) + Lift, plan.Y);
}
