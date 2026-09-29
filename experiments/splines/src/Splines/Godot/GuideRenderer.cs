using System;
using System.Collections.Generic;
using Godot;
using NumVector2 = System.Numerics.Vector2;

namespace CitySim.Splines.Godot;

/// <summary>
/// Draws S3's snap guides and length ticks: thin dashed lines draped on the ground (DESIGN.md → Snapping and
/// guides), separate from <see cref="RibbonRenderer"/>'s filled corridor ribbons since guides read as thin lines,
/// not wide surfaces. Simple full rebuilds each call, like <see cref="RibbonRenderer"/> — no incremental updates
/// until S11.
/// </summary>
public sealed class GuideRenderer
{
    private const float DashOn = 2f;
    private const float DashOff = 1.5f;
    private const float TickHalfLength = 1f;
    private const float MarkerRadius = 1.2f;
    private const int MarkerSegments = 16;
    private const float Lift = 0.06f; // just above RibbonRenderer's preview lift, to avoid z-fighting with it

    private readonly Node3D _parent;
    private readonly IGround _ground;
    private MeshInstance3D? _guides;
    private MeshInstance3D? _ticks;
    private MeshInstance3D? _marker;

    public GuideRenderer(Node3D parent, IGround ground)
    {
        _parent = parent;
        _ground = ground;
    }

    /// <summary>Rebuilds the dashed guide lines. Empty or null clears them.</summary>
    public void SetGuides(IReadOnlyList<GuideLine>? guides)
    {
        if (guides is null || guides.Count == 0)
        {
            _guides?.QueueFree();
            _guides = null;
            return;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Lines);
        foreach (var guide in guides)
            AddDashedPolyline(st, guide.Points);
        _guides ??= NewInstance();
        _guides.Mesh = st.Commit();
        _guides.MaterialOverride = Material(new Color(1f, 1f, 1f, 0.85f));
    }

    /// <summary>Rebuilds short perpendicular ticks every <paramref name="step"/> metres along the preview curve.
    /// Null or a non-positive step clears them.</summary>
    public void SetLengthTicks(Curve? previewCurve, float step)
    {
        if (previewCurve is null || previewCurve.Length <= 0f || step <= 0f)
        {
            _ticks?.QueueFree();
            _ticks = null;
            return;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Lines);
        for (float s = step; s < previewCurve.Length; s += step)
        {
            var sample = previewCurve.Sample(s);
            var tick = SplineMath.Left(sample.Tangent) * TickHalfLength;
            st.AddVertex(Drape(sample.Position - tick));
            st.AddVertex(Drape(sample.Position + tick));
        }
        _ticks ??= NewInstance();
        _ticks.Mesh = st.Commit();
        _ticks.MaterialOverride = Material(new Color(1f, 1f, 1f, 0.6f));
    }

    /// <summary>A small ring at the resolved snap position — the dashed guide alone doesn't say *where* on it the
    /// cursor is actually locking (DESIGN.md's storyboard pairs every catch with a ring, not just a line). Null
    /// clears it.</summary>
    public void SetSnapMarker(NumVector2? position)
    {
        if (position is not { } p)
        {
            _marker?.QueueFree();
            _marker = null;
            return;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i < MarkerSegments; i++)
        {
            float a0 = i * MathF.Tau / MarkerSegments;
            float a1 = (i + 1) * MathF.Tau / MarkerSegments;
            st.AddVertex(Drape(p + new NumVector2(MathF.Cos(a0), MathF.Sin(a0)) * MarkerRadius));
            st.AddVertex(Drape(p + new NumVector2(MathF.Cos(a1), MathF.Sin(a1)) * MarkerRadius));
        }
        _marker ??= NewInstance();
        _marker.Mesh = st.Commit();
        _marker.MaterialOverride = Material(new Color(1f, 0.85f, 0.2f, 0.95f)); // amber, distinct from the white guide dashes
    }

    private void AddDashedPolyline(SurfaceTool st, IReadOnlyList<NumVector2> points)
    {
        float carry = 0f; // how far into the current dash phase the previous segment left off
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            float segLen = NumVector2.Distance(a, b);
            if (segLen < 1e-4f) continue;
            var dir = (b - a) / segLen;
            float travelled = 0f;
            while (travelled < segLen)
            {
                float phase = carry % (DashOn + DashOff);
                bool on = phase < DashOn;
                float remainingInPhase = (on ? DashOn : DashOn + DashOff) - phase;
                float step = MathF.Min(remainingInPhase, segLen - travelled);
                if (on)
                {
                    st.AddVertex(Drape(a + dir * travelled));
                    st.AddVertex(Drape(a + dir * (travelled + step)));
                }
                travelled += step;
                carry += step;
            }
        }
    }

    private MeshInstance3D NewInstance()
    {
        var node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _parent.AddChild(node);
        return node;
    }

    private static StandardMaterial3D Material(Color color) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = color,
    };

    private Vector3 Drape(NumVector2 plan) => new(plan.X, _ground.GetHeight(plan) + Lift, plan.Y);
}
