using System.Collections.Generic;
using CitySim.Roads.Geometry;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// Collects road triangles per <see cref="SurfaceKind"/> and turns them into one <see cref="ArrayMesh"/>, a surface per
/// kind. Every triangle carries the normal it's meant to face (up for tops, sideways for kerb faces), and is wound so
/// that side is its front.
/// <para>What the road shaders get per vertex (they take their noise from world position, so it's seamless everywhere):
/// <c>UV</c> = metres along the road and across it from the centre line (a painted line: across the line), for slab
/// joints and kerb stones; <c>UV2.x</c> = <see cref="RoadSection.LaneCoord"/> on traffic lanes, <see cref="NoLane"/> off
/// them, <see cref="JunctionLane"/> on a junction; <c>UV2.y</c> = how worn the road is (0..1, <see cref="Wear"/>).
/// <c>COLOR.r</c> = the road's age (0 = new, 1 = falling apart, <see cref="Age"/>): its cracks. <c>COLOR.gba</c> = the
/// vertex's <see cref="Queue"/>: how much cars stand here waiting at a junction, for the lanes running along the curve
/// (g) and against it (b), and where those lanes split across the road (a, see <see cref="SplitCode"/>).
/// Plain points (junctions, caps) get world X/Z as their UV.</para>
/// </summary>
public sealed class RoadMesh
{
    public const float NoLane = -1f;
    public const float JunctionLane = -2f;

    /// <summary>A vertex with its shader data (see the class summary).</summary>
    public readonly record struct Vertex(Vector3 Pos, Vector2 Uv, Vector2 Uv2, Vector3 Queue = default);

    /// <summary>The offset across the road where the lanes running along the curve start (right of it), as COLOR.a:
    /// metres / 32 + 0.5, so ±16 m in 8 bits.</summary>
    public static float SplitCode(float offset) => System.Math.Clamp(offset / 32f + 0.5f, 0f, 1f);

    private readonly Dictionary<SurfaceKind, SurfaceTool> _tools = new();
    private readonly Dictionary<SurfaceKind, int> _counts = new();

    /// <summary>The wear given to plain points (set per road or junction before drawing it).</summary>
    public float Wear { get; set; }

    /// <summary>The age given to every triangle from now on (set per road or junction before drawing it).</summary>
    public float Age { get; set; }

    public Vertex Plain(Vector3 p) => new(p, new Vector2(p.X, p.Z), new Vector2(JunctionLane, Wear));

    public void Tri(SurfaceKind kind, Vector3 a, Vector3 b, Vector3 c, Vector3 normal) =>
        Tri(kind, Plain(a), Plain(b), Plain(c), normal);

    public void Tri(SurfaceKind kind, Vertex a, Vertex b, Vertex c, Vector3 normal)
    {
        // Godot's front faces wind clockwise seen from the front, so cross(b − a, c − a) points away from the viewer.
        if ((b.Pos - a.Pos).Cross(c.Pos - a.Pos).Dot(normal) > 0) (b, c) = (c, b);
        var st = Tool(kind);
        foreach (var v in new[] { a, b, c })
        {
            st.SetNormal(normal);
            st.SetUV(v.Uv);
            st.SetUV2(v.Uv2);
            st.SetColor(new Color(Age, v.Queue.X, v.Queue.Y, v.Queue.Z));
            st.AddVertex(v.Pos);
        }
        _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
    }

    /// <summary>A quad a-b-c-d (in order round it).</summary>
    public void Quad(SurfaceKind kind, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal) =>
        Quad(kind, Plain(a), Plain(b), Plain(c), Plain(d), normal);

    public void Quad(SurfaceKind kind, Vertex a, Vertex b, Vertex c, Vertex d, Vector3 normal)
    {
        Tri(kind, a, b, c, normal);
        Tri(kind, a, c, d, normal);
    }

    public int Count(SurfaceKind kind) => _counts.GetValueOrDefault(kind);

    /// <summary>Commits every kind with triangles into <paramref name="mesh"/>, with its material.</summary>
    public void CommitTo(ArrayMesh mesh, System.Func<SurfaceKind, Material?> materialOf)
    {
        foreach (var (kind, st) in _tools)
        {
            // A kind with no material (an overlay a style leaves out) isn't drawn.
            if (Count(kind) == 0 || materialOf(kind) is not { } material) continue;
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
    }

    private SurfaceTool Tool(SurfaceKind kind)
    {
        if (_tools.TryGetValue(kind, out var st)) return st;
        st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        return _tools[kind] = st;
    }
}
