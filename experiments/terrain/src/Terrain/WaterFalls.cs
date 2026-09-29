using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using CitySim.WaterSystem;

namespace CitySim.TerrainSystem;

/// <summary>Waterfall curtains and mist (graphics setting; <see cref="Terrain.WaterfallFx"/>).</summary>
public enum WaterfallQuality { Off, Low, High }

/// <summary>
/// Look-only waterfall effects on top of the simulated water: a curtain arcing off each fall's lip and mist puffs where
/// it lands. Nothing here feeds back into the sim.
/// Kept cheap because it's only a visual:
/// - Only falls near the camera are looked for (<see cref="CurtainDistance"/>), in a window of the sim's snapshot copied
///   under one lock, on a background task, a few times a second (sooner when the camera moves or turns a lot); none at
///   all while the camera is higher above the ground than that.
/// - Falls outside the camera's view (plus a margin) are skipped, and far curtains get fewer rows and mist fewer puffs.
/// - Both fade out with distance in the shader before the search radius, so nothing pops at the edge.
/// - One mesh for every curtain and one MultiMesh for every puff (2 draw calls); the mist is animated on the GPU from a
///   seed per puff, so a refresh doesn't restart it. No shadows, no lighting, capped counts.
/// </summary>
public partial class WaterFalls : Node3D
{
    public const int MaxSegments = 1500;
    public const int MaxPuffs = 600;
    private const float Gravity = 9.81f;
    /// <summary>Ground drop per cell (as a slope) where a lip starts: tan 30°.</summary>
    private const float LipSlope = 0.577f;
    /// <summary>A fall is at least this high (m) and this steep on average (drop / run: tan 30°), else it's rapids.</summary>
    private const float MinHeight = 3f, MinSteepness = 0.577f;
    /// <summary>Flux (depth × speed, m²/s) where a curtain starts to show and where it's full.</summary>
    private const float MinFlux = 0.02f, FullFlux = 0.4f;
    /// <summary>
    /// Mist is gathered in bins this size (m), so a wide fall doesn't get a puff per cell; small enough that the puffs of
    /// neighbouring bins overlap into one bank along the foot.
    /// </summary>
    private const float MistBin = 6f;

    private Terrain? _terrain;
    private WaterSim? _sim;
    private MeshInstance3D _curtains = null!;
    private ArrayMesh _curtainMesh = null!;
    private MultiMeshInstance3D _mistNode = null!;
    private MultiMesh _mist = null!;
    private ShaderMaterial? _curtainMaterial, _mistMaterial;
    private Task<Result>? _job;
    private float[] _window = [];
    private double _timer;
    private Vector3 _lastCamPos, _lastCamDir;
    private long _lastPublish = -1;

    private WaterfallQuality _quality = WaterfallQuality.High;
    public WaterfallQuality Quality
    {
        get => _quality;
        set
        {
            _quality = value;
            ApplyQuality();
            _timer = 0;
            _lastPublish = -1;
            if (value == WaterfallQuality.Off) Clear();
        }
    }

    /// <summary>Farthest curtain drawn (m from the camera); the mist stops sooner.</summary>
    public float CurtainDistance => _quality == WaterfallQuality.High ? 700f : 350f;
    public float MistDistance => _quality == WaterfallQuality.High ? 450f : 220f;
    private float MistAmount => _quality == WaterfallQuality.High ? 1f : 0.5f;

    /// <summary>Segments and puffs last drawn, and how long the last search took (ms, on the task).</summary>
    public int Segments { get; private set; }
    public int Puffs { get; private set; }
    public double ScanMs { get; private set; }

    public void Init(Terrain terrain, WaterSim sim, Material? curtain, Material? mist)
    {
        _terrain = terrain;
        _sim = sim;
        _curtainMaterial = curtain as ShaderMaterial;
        _mistMaterial = mist as ShaderMaterial;
        _curtainMesh = new ArrayMesh();
        _curtains = new MeshInstance3D
        {
            Mesh = _curtainMesh,
            MaterialOverride = curtain,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_curtains);
        _mist = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = MaxPuffs,
            VisibleInstanceCount = 0,
        };
        _mistNode = new MultiMeshInstance3D
        {
            Multimesh = _mist,
            MaterialOverride = mist,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_mistNode);
        ApplyQuality();
        VisibilityChanged += () => { _timer = 0; _lastPublish = -1; };
    }

    private void ApplyQuality()
    {
        // The shaders fade out before the search radius, so falls leave the search already invisible.
        _curtainMaterial?.SetShaderParameter("fade_distance", new Vector2(CurtainDistance * 0.6f, CurtainDistance * 0.95f));
        _mistMaterial?.SetShaderParameter("fade_distance", new Vector2(MistDistance * 0.55f, MistDistance * 0.95f));
    }

    private void Clear()
    {
        _curtainMesh?.ClearSurfaces();
        if (_mist is not null) _mist.VisibleInstanceCount = 0;
        Segments = Puffs = 0;
    }

    public override void _Process(double delta)
    {
        if (_job is { IsCompleted: true } done)
        {
            _job = null;
            if (done.IsCompletedSuccessfully && _quality != WaterfallQuality.Off) Apply(done.Result);
        }
        if (!Visible || _quality == WaterfallQuality.Off || _job is not null || _sim is not { } sim || _terrain is not { } terrain
            || GetViewport().GetCamera3D() is not { } cam) return;
        _timer -= delta;
        var camPos = cam.GlobalPosition;
        var camDir = -cam.GlobalBasis.Z;
        // Refresh twice a second while the water or camera changes, sooner (≤ 5×/s) after a big move or turn.
        bool bigMove = camPos.DistanceTo(_lastCamPos) > CurtainDistance * 0.05f || camDir.Dot(_lastCamDir) < 0.99f;
        bool changed = bigMove || camPos != _lastCamPos || camDir != _lastCamDir || sim.Publishes != _lastPublish;
        bool due = _timer <= 0 || (bigMove && _timer <= 0.3);
        if (!due || !changed) return;
        _timer = 0.5;
        _lastCamPos = camPos;
        _lastCamDir = camDir;
        _lastPublish = sim.Publishes;

        // Too high up for any fall to be within reach (the god view): nothing to search.
        if (camPos.Y - terrain.GetHeight(camPos.X, camPos.Z) > CurtainDistance)
        {
            if (Segments > 0 || Puffs > 0) Clear();
            return;
        }
        var origin = terrain.GlobalPosition;
        var planes = new List<Plane>();
        foreach (var p in cam.GetFrustum()) planes.Add(p);
        var job = new Job(camPos - origin, origin, planes.ToArray(), CurtainDistance, MistDistance, MistAmount);
        _job = Task.Run(() => Scan(sim, job));
    }

    private readonly record struct Job(Vector3 Cam, Vector3 Origin, Plane[] Frustum, float CurtainDistance, float MistDistance,
        float MistAmount);

    private sealed class Result
    {
        public readonly List<Vector3> Verts = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<Vector2> Uv2s = new();
        public readonly List<int> Indices = new();
        public readonly List<(Transform3D At, Color Custom)> Puffs = new();
        public int Segments;
        public double Ms;
    }

    private sealed class MistBinData
    {
        public float Weight, Height, BaseY;
        public Vector2 Sum;
        public float SumW;
        public Vector2 Dir;
        public float Fwd;
    }

    /// <summary>The search, on a task: lips in the window around the camera, curtain vertices, mist puffs.</summary>
    private Result Scan(WaterSim sim, Job job)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = new Result();
        float cs = sim.CellSize;
        var ground = sim.Ground;
        int x0 = (int)MathF.Floor((job.Cam.X - job.CurtainDistance) / cs), z0 = (int)MathF.Floor((job.Cam.Z - job.CurtainDistance) / cs);
        int w = (int)MathF.Ceiling(job.CurtainDistance * 2f / cs) + 1, d = w;
        if (_window.Length < w * d * 4) _window = new float[w * d * 4];
        var win = _window;
        if (!sim.CopyWindow(x0, z0, w, d, win)) { r.Ms = clock.Elapsed.TotalMilliseconds; return r; }

        float G(Vector2 p) => ground.SampleHeight(p.X, p.Y);
        // Water surface at a position if the window holds water there, else the ground.
        float Surface(Vector2 p)
        {
            int i = (int)MathF.Round(p.X / cs) - x0, j = (int)MathF.Round(p.Y / cs) - z0;
            if (i >= 0 && j >= 0 && i < w && j < d && win[(j * w + i) * 4 + 1] > 0.02f) return win[(j * w + i) * 4];
            return G(p);
        }
        bool InView(Vector3 local, float radius)
        {
            var world = local + job.Origin;
            foreach (var pl in job.Frustum)
                if (pl.DistanceTo(world) > radius) return false;
            return true;
        }

        float threshold = LipSlope * cs;
        var profile = new List<float>();
        var bins = new Dictionary<(int, int), MistBinData>();
        for (int j = 0; j < d && r.Segments < MaxSegments; j++)
            for (int i = 0; i < w && r.Segments < MaxSegments; i++)
            {
                int c = (j * w + i) * 4;
                float depth = win[c + 1];
                if (depth < 0.02f) continue;
                float vx = win[c + 2], vz = win[c + 3];
                float speed = MathF.Sqrt(vx * vx + vz * vz);
                if (speed < 0.3f || depth * speed < MinFlux) continue;
                var p = new Vector2((x0 + i) * cs, (z0 + j) * cs);
                var dir = new Vector2(vx, vz) / speed;
                // A lip: the ground drops steeply just ahead but not just behind (exactly one lip per flow line).
                float h = G(p);
                if (h - G(p + dir * cs) < threshold || G(p - dir * cs) - h >= threshold) continue;
                // Follow the drop downstream to the foot of the fall, keeping the face's profile for the curtain.
                var q = p;
                profile.Clear();
                profile.Add(h);
                for (int k = 0; k < 64; k++)
                {
                    var next = q + dir * (cs * 0.5f);
                    float hn = G(next);
                    if (G(q) - hn < 0.35f * cs * 0.5f) break;
                    q = next;
                    profile.Add(hn);
                }
                float top = win[c];
                float baseY = Surface(q);
                float height = top - baseY, run = (q - p).Length();
                if (height < MinHeight || height / MathF.Max(run, cs) < MinSteepness) continue;
                var mid = new Vector3(q.X, baseY + height * 0.5f, q.Y);
                float dist = mid.DistanceTo(job.Cam);
                if (dist > job.CurtainDistance || !InView(mid, height * 0.5f + 30f)) continue;

                float strength = Smooth(MinFlux, FullFlux, depth * speed);
                var landing = AddCurtain(r, p, dir, top, height, MathF.Max(speed, 1.5f), profile, cs, strength,
                    dist < 250f ? 10 : 5);
                r.Segments++;

                if (dist > job.MistDistance) continue;
                var key = ((int)MathF.Floor(landing.X / MistBin), (int)MathF.Floor(landing.Z / MistBin));
                if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new MistBinData { BaseY = float.MinValue };
                float wgt = strength * MathF.Sqrt(height) * 0.15f;
                bin.Weight += wgt;
                bin.Sum += new Vector2(landing.X, landing.Z) * wgt;
                bin.SumW += wgt;
                bin.Height = MathF.Max(bin.Height, height);
                bin.BaseY = MathF.Max(bin.BaseY, baseY);
                bin.Dir += dir;
                bin.Fwd = MathF.Max(bin.Fwd, run);
            }

        foreach (var ((bx, bz), bin) in bins)
        {
            if (bin.SumW <= 0f || r.Puffs.Count >= MaxPuffs) continue;
            var at = bin.Sum / bin.SumW;
            float dist = new Vector3(at.X, bin.BaseY, at.Y).DistanceTo(job.Cam);
            // Fewer, bigger puffs far away; none past the mist distance (the shader has faded them by then).
            float lod = dist < job.MistDistance * 0.5f ? 1f : 0.5f;
            // Several faint, overlapping puffs per bin blend into one smoky bank rather than separate clouds.
            int n = Math.Clamp((int)MathF.Ceiling(bin.Weight * job.MistAmount * lod * 1.5f), 2, 6);
            float size = Math.Clamp(5f + bin.Height * 0.3f, 5f, 20f) * (lod < 1f ? 1.3f : 1f);
            float strength = Math.Clamp(0.45f + bin.Weight * 0.4f, 0.45f, 1f);
            var dir = bin.Dir.LengthSquared() > 0f ? bin.Dir.Normalized() : Vector2.Up;
            // +Z downstream (the shader drifts puffs that way).
            var basis = new Basis(Vector3.Up, MathF.Atan2(dir.X, dir.Y));
            for (int k = 0; k < n && r.Puffs.Count < MaxPuffs; k++)
            {
                float s = Hash(bx, bz, k), jx = Hash(bx, bz, k + 17) - 0.5f, jz = Hash(bx, bz, k + 31) - 0.5f;
                // From where the curtain hits the water to a little out over the pool, so it reads as coming off the fall.
                float out_ = size * 0.35f * Hash(bx, bz, k + 47);
                var pos = new Vector3(at.X + jx * MistBin + dir.X * out_, bin.BaseY, at.Y + jz * MistBin + dir.Y * out_);
                r.Puffs.Add((new Transform3D(basis, pos), new Color(s, size, strength, 0f)));
            }
            // Spray hanging over the foot of a tall fall.
            if (bin.Height > 12f && r.Puffs.Count < MaxPuffs)
            {
                var pos = new Vector3(at.X - dir.X * bin.Fwd * 0.3f, bin.BaseY + bin.Height * 0.3f, at.Y - dir.Y * bin.Fwd * 0.3f);
                r.Puffs.Add((new Transform3D(basis, pos), new Color(Hash(bx, bz, 99), size * 0.8f, strength * 0.6f, bin.Height * 0.3f)));
            }
        }
        r.Ms = clock.Elapsed.TotalMilliseconds;
        return r;
    }

    /// <summary>
    /// One curtain segment (a cell wide) from the lip down to the pool: it arcs out on a parabola at the lip's speed, but
    /// never closer to the face than a standoff that grows as it falls, so it hangs in front of the simulated sheet on the
    /// face and always reaches the foot. <paramref name="face"/> is the ground every half cell from the lip. Returns where
    /// it lands.
    /// </summary>
    private static Vector3 AddCurtain(Result r, Vector2 lip, Vector2 dir, float top, float height, float speed,
        List<float> face, float cs, float strength, int rows)
    {
        var perp = new Vector2(-dir.Y, dir.X);
        int first = r.Verts.Count;
        var color = new Color(1f, 1f, 1f, strength);
        float step = cs * 0.5f;
        // Distance from the lip where the face comes down to height y.
        float FaceAt(float y)
        {
            for (int k = 1; k < face.Count; k++)
                if (face[k] <= y) return (k - 1 + (face[k - 1] - y) / MathF.Max(face[k - 1] - face[k], 1e-4f)) * step;
            return (face.Count - 1) * step;
        }
        Vector3 prev = default, at = default;
        for (int row = 0; row <= rows; row++)
        {
            float s = (float)row / rows;
            float drop = s * height;
            float y = top - drop;
            float standoff = MathF.Min(0.3f + drop * 0.08f, 4f);
            float fwd = MathF.Max(speed * MathF.Sqrt(2f * drop / Gravity), FaceAt(y) + standoff);
            var p = lip + dir * fwd;
            at = new Vector3(p.X, y, p.Y);
            float half = cs * 0.6f * (1f + 0.35f * s);
            var tangent = row == 0 ? new Vector3(dir.X, -1f, dir.Y) : at - prev;
            var normal = tangent.Cross(new Vector3(perp.X, 0f, perp.Y)).Normalized();
            prev = at;
            for (int side = 0; side < 2; side++)
            {
                var e = perp * (side == 0 ? -half : half);
                var v = at + new Vector3(e.X, 0f, e.Y);
                r.Verts.Add(v);
                r.Normals.Add(normal);
                r.Colors.Add(color);
                r.Uvs.Add(new Vector2(v.X * perp.X + v.Z * perp.Y, drop));
                r.Uv2s.Add(new Vector2(side, s));
            }
            if (row == 0) continue;
            int b = first + (row - 1) * 2;
            r.Indices.AddRange([b, b + 1, b + 2, b + 2, b + 1, b + 3]);
        }
        return at;
    }

    private void Apply(Result r)
    {
        _curtainMesh.ClearSurfaces();
        if (r.Verts.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = r.Verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = r.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = r.Colors.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = r.Uvs.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = r.Uv2s.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = r.Indices.ToArray();
            _curtainMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        int n = Math.Min(r.Puffs.Count, MaxPuffs);
        var box = new Aabb();
        for (int k = 0; k < n; k++)
        {
            var (at, custom) = r.Puffs[k];
            _mist.SetInstanceTransform(k, at);
            _mist.SetInstanceCustomData(k, custom);
            // The shader grows and moves puffs (size up to 1.4× plus a rise and drift of about their size).
            var reach = Vector3.One * custom.G * 2f;
            var puff = new Aabb(at.Origin - reach, reach * 2f);
            box = k == 0 ? puff : box.Merge(puff);
        }
        _mist.VisibleInstanceCount = n;
        _mistNode.CustomAabb = box;
        Segments = r.Segments;
        Puffs = n;
        ScanMs = r.Ms;
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A stable 0..1 hash per mist bin and puff, so puffs keep their seed across refreshes.</summary>
    private static float Hash(int x, int z, int k)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(k * 83492791);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public override void _ExitTree()
    {
        // A running search only reads the sim's snapshot and writes its own result; let it finish unobserved.
        _job = null;
    }
}
