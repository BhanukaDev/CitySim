using System.Collections.Generic;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// A ring of fake ground around the terrain that hides its hard edges. Its inner loop follows the
/// terrain's border vertices exactly (so there is no gap or cliff), then it sinks and spreads outward to
/// the horizon. It uses the terrain material, whose edge fog paints everything outside the bounds in the
/// fog colour, so the skirt reads as a fog bank the map dissolves into.
/// </summary>
public partial class TerrainSkirt : MeshInstance3D
{
    // Distances of each ring from the terrain edge, in metres. The last ring sits near the camera's far plane.
    private static readonly float[] RingDistances = [0f, 20f, 60f, 150f, 350f, 800f, 2000f, 5000f, 11000f];
    // Rounds each corner with this many segments, so the outer rings don't leave a wedge-shaped gap.
    private const int CornerSegments = 8;
    // How far the skirt drops below the lowest terrain point, and over what distance it gets there.
    private const float SinkDepth = 10f;
    private const float SinkDistance = 600f;

    private HeightMap? _map;

    public void Init(HeightMap map)
    {
        _map = map;
        Name = "Skirt";
        CastShadow = ShadowCastingSetting.Off;
    }

    public void Rebuild()
    {
        if (_map is null) return;
        var map = _map;
        float baseHeight = map.GetRange().Min - SinkDepth;

        // Walk the border counter-clockwise seen from above: (0,0) -> (W,0) -> (W,D) -> (0,D).
        // Each entry is a border position, its height and the outward direction its rings extend along.
        var border = new List<(Vector2 Pos, float Height, Vector2 Dir)>();
        int w = map.Width - 1, d = map.Depth - 1;
        (int X, int Z)[] corners = [(0, 0), (w, 0), (w, d), (0, d)];
        Vector2[] outward = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];
        for (int side = 0; side < 4; side++)
        {
            var (x0, z0) = corners[side];
            var (x1, z1) = corners[(side + 1) % 4];
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(z1 - z0));
            int sx = Mathf.Sign(x1 - x0), sz = Mathf.Sign(z1 - z0);
            var cornerPos = new Vector2(x0, z0) * map.CellSize;
            // Fan around the corner from the previous side's normal to this one's.
            float a0 = outward[(side + 3) % 4].Angle(), a1 = outward[side].Angle();
            for (int k = 0; k < CornerSegments; k++)
            {
                float a = Mathf.LerpAngle(a0, a1, k / (float)CornerSegments);
                border.Add((cornerPos, map[x0, z0], Vector2.FromAngle(a)));
            }
            for (int i = 0; i < steps; i++)
            {
                int x = x0 + sx * i, z = z0 + sz * i;
                border.Add((new Vector2(x, z) * map.CellSize, map[x, z], outward[side]));
            }
        }

        int n = border.Count, rings = RingDistances.Length;
        var vertices = new Vector3[n * rings];
        var normals = new Vector3[n * rings];
        for (int r = 0; r < rings; r++)
        {
            float dist = RingDistances[r];
            float sink = Mathf.SmoothStep(0f, SinkDistance, dist);
            for (int i = 0; i < n; i++)
            {
                var (pos, h, dir) = border[i];
                var p = pos + dir * dist;
                vertices[r * n + i] = new Vector3(p.X, Mathf.Lerp(h, baseHeight, sink), p.Y);
                normals[r * n + i] = Vector3.Up;
            }
        }

        // Quads between ring r (inner) and r + 1 (outer), clockwise seen from above like TerrainChunk.
        var indices = new int[n * (rings - 1) * 6];
        int t = 0;
        for (int r = 0; r < rings - 1; r++)
        {
            for (int i = 0; i < n; i++)
            {
                int i1 = (i + 1) % n;
                int inA = r * n + i, inB = r * n + i1;
                int outA = inA + n, outB = inB + n;
                indices[t++] = outA; indices[t++] = outB; indices[t++] = inA;
                indices[t++] = outB; indices[t++] = inB; indices[t++] = inA;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[n * rings];
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
    }
}
