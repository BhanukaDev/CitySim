using System.Collections.Generic;
using CitySim.Roads.Geometry;
using Godot;

namespace CitySim.Roads;

/// <summary>
/// Collects road triangles per <see cref="SurfaceKind"/> and turns them into one <see cref="ArrayMesh"/>, a surface per
/// kind. Every triangle carries the normal it's meant to face (up for tops, sideways for kerb faces), and is wound so
/// that side is its front. UVs are world metres / 4, ready for tiling textures.
/// </summary>
public sealed class RoadMesh
{
    private const float UvScale = 0.25f;
    private readonly Dictionary<SurfaceKind, SurfaceTool> _tools = new();
    private readonly Dictionary<SurfaceKind, int> _counts = new();

    public void Tri(SurfaceKind kind, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        // Godot's front faces wind clockwise seen from the front, so cross(b − a, c − a) points away from the viewer.
        if ((b - a).Cross(c - a).Dot(normal) > 0) (b, c) = (c, b);
        var st = Tool(kind);
        foreach (var v in new[] { a, b, c })
        {
            st.SetNormal(normal);
            st.SetUV(new Vector2(v.X, v.Z) * UvScale);
            st.AddVertex(v);
        }
        _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
    }

    /// <summary>A quad a-b-c-d (in order round it).</summary>
    public void Quad(SurfaceKind kind, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
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
            if (Count(kind) == 0) continue;
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materialOf(kind));
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
