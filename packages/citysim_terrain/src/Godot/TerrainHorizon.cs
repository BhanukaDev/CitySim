using System;
using System.Collections.Generic;
using Godot;

namespace CitySim.TerrainSystem;

/// <summary>
/// A ring of fake ground around the terrain that hides its hard edges. Its inner loop follows the terrain's drawn border
/// vertices exactly (so there is no gap or cliff). Each vertex carries two heights, and the shader
/// (shaders/terrain_horizon.gdshader) picks one by <c>horizon_mix</c>:
/// <list type="bullet">
/// <item>the horizon (VERTEX.y): low hills carrying on from the border, smoothed more and more with distance, plus noise
/// and a gentle rise, lost in the haze a few km out. Where the border is under the sea, it stays under it.</item>
/// <item>the fog floor (UV2.x): sinks below the lowest point and spreads to the horizon in the edge fog colour.</item>
/// </list>
/// Not editable, no simulation, not part of the map. Rebuilt when a sculpt edit touches the border.
/// </summary>
public partial class TerrainHorizon : MeshInstance3D
{
    // Distances of each loop from the terrain edge, in metres. The horizon's hills end in haze by RingEnd (6000 m by
    // default); the loops past it are the fog floor out to 150 km, so its end stays out of frame from the whole-map zoom on a 28.7 km map.
    private static readonly float[] RingDistances =
        [0f, 30f, 90f, 200f, 400f, 650f, 950f, 1300f, 1700f, 2200f, 2800f, 3500f, 4300f, 5100f, 6000f, 11000f, 30000f, 70000f, 150000f];
    // Rounds each corner with this many segments, so the outer loops don't leave a wedge-shaped gap.
    private const int CornerSegments = 8;

    private HeightMap? _map;
    private int _lastX, _lastZ;

    /// <summary>Where the hills reach full height and end in haze (m past the border; the loops past it are the fog floor).</summary>
    public float RingEnd { get; set; } = 6000f;
    /// <summary>The fog floor drops this far below the lowest terrain point (m), over <see cref="SinkDistance"/>.</summary>
    public float SinkDepth { get; set; } = 10f;
    public float SinkDistance { get; set; } = 600f;
    /// <summary>The least hill height past the border (m) at the default relief, for flat maps.</summary>
    public float MinRelief { get; set; } = 200f;

    /// <summary>Hill height past the border as a share of the map's height range (theme uniform <c>horizon_relief</c>).</summary>
    public float Relief { get; set; } = 0.35f;
    /// <summary>The sea's level past the border (the map's Sea source), or null: the horizon stays below it where the border does.</summary>
    public float? SeaLevel { get; set; }
    /// <summary>How long the last <see cref="Rebuild"/> took, its vertex count and the horizon's height range.</summary>
    public double LastBuildMs { get; private set; }
    public int LastVertexCount { get; private set; }
    public (float Min, float Max) LastHeightRange { get; private set; }

    /// <summary>The inner loop runs along vertices 0..<paramref name="lastX"/> × 0..<paramref name="lastZ"/> (the drawn area).</summary>
    public void Init(HeightMap map, int lastX, int lastZ)
    {
        _map = map;
        _lastX = lastX;
        _lastZ = lastZ;
        Name = "Horizon";
        CastShadow = ShadowCastingSetting.Off;
    }

    // One loop point: map position, outward direction, place along the loop (0-4, one unit per side) and the side and
    // vertex index along it (for the smoothed border height).
    private readonly record struct LoopPoint(Vector2 Pos, Vector2 Dir, float U, int Side, int Index);

    public void Rebuild()
    {
        if (_map is null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var map = _map;
        var (minH, maxH) = map.GetRange();
        float baseHeight = minH - SinkDepth;
        float range = Math.Max(maxH - minH, 1f);
        float cell = map.CellSize;
        int w = _lastX, d = _lastZ;
        (int X, int Z)[] corners = [(0, 0), (w, 0), (w, d), (0, d)];
        Vector2[] outward = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

        // Border heights per side (both corners included) and their prefix sums, for averages along the border.
        var sideLength = new int[4];
        var prefix = new double[4][];
        for (int side = 0; side < 4; side++)
        {
            var (x0, z0) = corners[side];
            var (x1, z1) = corners[(side + 1) % 4];
            int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
            int sx = Math.Sign(x1 - x0), sz = Math.Sign(z1 - z0);
            sideLength[side] = steps;
            var p = prefix[side] = new double[steps + 2];
            for (int i = 0; i <= steps; i++) p[i + 1] = p[i] + map[x0 + sx * i, z0 + sz * i];
        }
        float BorderHeight(int side, int index, float radiusMetres)
        {
            int r = (int)(radiusMetres / cell);
            int n = sideLength[side];
            int a = Math.Clamp(index - r, 0, n), b = Math.Clamp(index + r, 0, n);
            return (float)((prefix[side][b + 1] - prefix[side][a]) / (b - a + 1));
        }

        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
            Frequency = 1f / 1800f,
            Seed = (int)(map.Width * 7919 + minH * 131 + maxH * 17),
        };

        // The loops: full resolution at the border, coarser further out (segments ~6 % of the distance, ≤ 250 m).
        int rings = RingDistances.Length;
        var loops = new List<LoopPoint>[rings];
        for (int r = 0; r < rings; r++)
        {
            float segment = Math.Clamp(RingDistances[r] * 0.06f, cell, 250f);
            int stride = r < 2 ? 1 : Math.Max(1, (int)MathF.Round(segment / cell));
            var loop = loops[r] = new List<LoopPoint>();
            for (int side = 0; side < 4; side++)
            {
                var (x0, z0) = corners[side];
                var (x1, z1) = corners[(side + 1) % 4];
                int steps = sideLength[side];
                int sx = Math.Sign(x1 - x0), sz = Math.Sign(z1 - z0);
                var cornerPos = new Vector2(x0, z0) * cell;
                // Fan around the corner from the previous side's normal to this one's.
                float a0 = outward[(side + 3) % 4].Angle(), a1 = outward[side].Angle();
                for (int k = 0; k < CornerSegments; k++)
                    loop.Add(new LoopPoint(cornerPos, Vector2.FromAngle(Mathf.LerpAngle(a0, a1, k / (float)CornerSegments)),
                        side + 0.001f * k / CornerSegments, side, 0));
                for (int i = 0; i < steps; i += stride)
                    loop.Add(new LoopPoint(new Vector2(x0 + sx * i, z0 + sz * i) * cell, outward[side],
                        side + 0.001f + 0.998f * i / Math.Max(steps, 1), side, i));
            }
        }

        // Vertices: the horizon height in Y, the fog floor's in UV2.x.
        var offsets = new int[rings + 1];
        for (int r = 0; r < rings; r++) offsets[r + 1] = offsets[r] + loops[r].Count;
        int count = offsets[rings];
        var vertices = new Vector3[count];
        var uv2 = new Vector2[count];
        float yMin = float.MaxValue, yMax = float.MinValue;
        for (int r = 0; r < rings; r++)
        {
            float dist = RingDistances[r];
            float sink = Mathf.SmoothStep(0f, SinkDistance, dist);
            // Hills at least MinRelief high, so a flat map still gets a skyline.
            float relief = Math.Max(range * Relief, MinRelief * Math.Min(Relief / 0.35f, 1f));
            float amp = Mathf.SmoothStep(0f, 2000f, dist) * relief;
            float rise = Mathf.SmoothStep(1000f, RingEnd, Math.Min(dist, RingEnd)) * relief * 0.6f;
            // Past the ring's end (in haze), settle toward the fog floor.
            float settle = dist > RingEnd ? Mathf.SmoothStep(RingEnd, 30000f, dist) : 0f;
            for (int i = 0; i < loops[r].Count; i++)
            {
                var pt = loops[r][i];
                var p = pt.Pos + pt.Dir * dist;
                float border = BorderHeight(pt.Side, pt.Index, dist * 0.6f);
                // Fbm rarely leaves ±0.5, so ×2 brings the hills to about ±amp.
                float h = r == 0 ? border : border + Math.Clamp(noise.GetNoise2D(p.X, p.Y) * 2f, -1f, 1f) * amp + rise;
                if (SeaLevel is { } sea && border < sea)
                    // Under the sea at the border: stay under it, a little deeper further out.
                    h = Math.Min(h, sea - 1f - dist * 0.004f);
                h = Mathf.Lerp(h, baseHeight, settle);
                float floor = Mathf.Lerp(map[Math.Clamp((int)(pt.Pos.X / cell), 0, w), Math.Clamp((int)(pt.Pos.Y / cell), 0, d)],
                    baseHeight, sink);
                if (r == 0) h = floor;
                vertices[offsets[r] + i] = new Vector3(p.X, h, p.Y);
                uv2[offsets[r] + i] = new Vector2(floor, 0f);
                yMin = Math.Min(yMin, Math.Min(h, floor));
                yMax = Math.Max(yMax, Math.Max(h, floor));
            }
        }

        // Triangles between loop r (inner) and r + 1 (outer), zipped along the loop place U (the loops have different
        // counts), clockwise seen from above.
        var indices = new List<int>(count * 6);
        for (int r = 0; r < rings - 1; r++)
        {
            var inner = loops[r];
            var outer = loops[r + 1];
            int ni = inner.Count, no = outer.Count;
            int oi = offsets[r], oo = offsets[r + 1];
            float U(List<LoopPoint> loop, int k) => k < loop.Count ? loop[k].U : 4f + loop[k - loop.Count].U;
            int a = 0, b = 0;
            while (a < ni || b < no)
            {
                bool advanceInner = b >= no || (a < ni && U(inner, a + 1) <= U(outer, b + 1));
                int ia = oi + a % ni, ob = oo + b % no;
                if (advanceInner)
                {
                    indices.Add(ia); indices.Add(ob); indices.Add(oi + (a + 1) % ni);
                    a++;
                }
                else
                {
                    indices.Add(ia); indices.Add(ob); indices.Add(oo + (b + 1) % no);
                    b++;
                }
            }
        }

        // Normals of the horizon heights (the fog floor is unlit).
        var normals = new Vector3[count];
        for (int t = 0; t < indices.Count; t += 3)
        {
            var v0 = vertices[indices[t]];
            var n = (vertices[indices[t + 2]] - v0).Cross(vertices[indices[t + 1]] - v0);
            if (n.Y < 0) n = -n;
            normals[indices[t]] += n;
            normals[indices[t + 1]] += n;
            normals[indices[t + 2]] += n;
        }
        for (int i = 0; i < count; i++) normals[i] = normals[i].LengthSquared() > 0 ? normals[i].Normalized() : Vector3.Up;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[count];
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
        // The shader moves vertices between the two heights and up to the sea: cull on both.
        float top = Math.Max(yMax, SeaLevel ?? yMax) + 1f;
        var size = new Vector3((w * cell) + 2f * 150000f, top - yMin + 2f, (d * cell) + 2f * 150000f);
        CustomAabb = new Aabb(new Vector3(-150000f, yMin - 1f, -150000f), size);
        LastBuildMs = sw.Elapsed.TotalMilliseconds;
        LastVertexCount = count;
        LastHeightRange = (yMin, yMax);
    }
}
