using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;
using CitySim.TerrainSystem;
using CitySim.TerrainSystem.Generation;

namespace CitySim.Debug;

/// <summary>
/// M6 Phase 0 throwaway: can Terrain3D carry a 28 km build area (8192² at 3.5 m) on an 8 GB M1?
/// Generates with <see cref="TerrainGen"/>, imports into Terrain3D, then measures FPS at a few camera spots and the
/// cost of brush-sized edits. Run: Godot --path . res://scenes/Spike.tscn [-- --spike-size=8192 --spike-out=dir]
/// </summary>
public partial class Terrain3DSpike : Node3D
{
    private const float Spacing = 3.5f;
    private const int RegionSize = 1024;

    private int _size = 8192;
    private string _outDir = "";
    private Node3D _t3d = null!;
    private GodotObject _data = null!;
    private Camera3D _camera = null!;
    private HeightMap _map = null!;

    private int _step, _frames;
    private double _frameTime;
    private readonly Stopwatch _sw = new();

    // Camera spots: (label, position, look-at).
    private (string Name, Vector3 Pos, Vector3 Target)[] _views = [];

    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--spike-size=")) _size = int.Parse(arg["--spike-size=".Length..]);
            else if (arg.StartsWith("--spike-out=")) _outDir = arg["--spike-out=".Length..];
        }
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Log($"rss at start {RssMb()} MB");

        // 1. Generate heights (engine-agnostic generator, same as the game).
        _sw.Restart();
        var settings = new GenSettings { Cells = _size, CellSize = Spacing };
        _map = TerrainGen.Create(settings);
        var (min, max) = _map.GetRange();
        Log($"generate {_size + 1}² in {_sw.ElapsedMilliseconds} ms (range {min:0}..{max:0} m), rss {RssMb()} MB");

        // 2. Terrain3D node.
        _sw.Restart();
        _t3d = (Node3D)ClassDB.Instantiate("Terrain3D").AsGodotObject();
        _t3d.Name = "Terrain3D";
        _t3d.Set("collision_mode", 0); // DISABLED: we raycast the heightmap ourselves
        _t3d.Set("vertex_spacing", Spacing);
        AddChild(_t3d);
        // Ignored before the node is in the tree.
        _t3d.Call("change_region_size", RegionSize);
        Log($"region size {_t3d.Call("get_region_size")}");
        var material = _t3d.Get("material").AsGodotObject();
        material.Set("world_background", 0); // NONE
        material.Set("auto_shader", true);
        _data = _t3d.Get("data").AsGodotObject();
        Log($"terrain3d node in {_sw.ElapsedMilliseconds} ms");

        // 3. Import: one R32F image of the first _size² vertices (Terrain3D regions are a power of two).
        _sw.Restart();
        var bytes = new byte[_size * _size * 4];
        var src = _map.Data;
        for (int z = 0; z < _size; z++)
            MemoryMarshal.AsBytes(src.Slice(z * _map.Width, _size)).CopyTo(bytes.AsSpan(z * _size * 4, _size * 4));
        var img = Image.CreateFromData(_size, _size, false, Image.Format.Rf, bytes);
        long tImage = _sw.ElapsedMilliseconds;
        var images = new Godot.Collections.Array { img, default, default };
        _data.Call("import_images", images, Vector3.Zero, 0f, 1f);
        Log($"import: image {tImage} ms, import_images {_sw.ElapsedMilliseconds - tImage} ms, regions {_data.Call("get_region_count")}, rss {RssMb()} MB");
        bytes = [];
        img.Dispose();
        GC.Collect();

        // Spot check: Terrain3D height == our height.
        float wx = 1234.5f, wz = 2345.25f;
        float ours = _map.SampleHeight(wx, wz);
        float theirs = _data.Call("get_height", new Vector3(wx, 0, wz)).AsSingle();
        Log($"height check at ({wx},{wz}): ours {ours:0.000}, terrain3d {theirs:0.000}");

        float world = _size * Spacing, mid = world * 0.5f;
        float hMid = _map.SampleHeight(mid, mid);
        _views =
        [
            ("overview 12 km up", new Vector3(mid, 9000, mid + 9000), new Vector3(mid, 0, mid)),
            ("city 1.5 km", new Vector3(mid, hMid + 1000, mid + 1100), new Vector3(mid, hMid, mid)),
            ("ground 20 m", new Vector3(mid, hMid + 20, mid + 60), new Vector3(mid + 400, hMid + 10, mid - 2000)),
        ];

        _camera = new Camera3D { Near = 0.5f, Far = 80000f, Fov = 50f, Current = true };
        AddChild(_camera);
        _t3d.Call("set_camera", _camera);
        SetView(0);
    }

    private void SetView(int i)
    {
        var (_, pos, target) = _views[i];
        _camera.Position = pos;
        _camera.LookAt(target, Vector3.Up);
        _frames = 0;
        _frameTime = 0;
    }

    public override void _Process(double delta)
    {
        // Each view: 30 warm-up frames, then 120 measured frames, then a screenshot.
        int view = _step;
        if (view < _views.Length)
        {
            _frames++;
            if (_frames > 30) _frameTime += delta;
            if (_frames == 150)
            {
                double ms = _frameTime / 120 * 1000;
                ulong draws = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                ulong prims = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
                ulong vram = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / (1024 * 1024);
                Log($"view '{_views[view].Name}': {ms:0.00} ms/frame ({1000 / ms:0} fps), {draws} draws, {prims / 1000}k tris, vram {vram} MB, rss {RssMb()} MB");
                if (_outDir != "")
                    GetViewport().GetTexture().GetImage().SavePng($"{_outDir}/spike_{view}.png");
                _step++;
                if (_step < _views.Length) SetView(_step);
            }
            return;
        }
        if (_step == _views.Length)
        {
            _step++;
            try { MeasureEdits(); }
            finally { GetTree().Quit(); }
        }
    }

    /// <summary>Two ways to push a 60 m-radius brush (35² px) into Terrain3D.</summary>
    private void MeasureEdits()
    {
        float world = _size * Spacing;
        var centre = new Vector3(world * 0.37f, 0, world * 0.41f);
        int r = (int)(60f / Spacing);

        // A. set_height per pixel through Call(), then update_maps.
        _sw.Restart();
        int calls = 0;
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                var p = centre + new Vector3(dx * Spacing, 0, dz * Spacing);
                _data.Call("set_height", p, _map.SampleHeight(p.X, p.Z) + 5f);
                calls++;
            }
        long tSet = _sw.ElapsedMilliseconds;
        _data.Call("update_maps", 0, false, false);
        Log($"edit A: {calls} set_height calls {tSet} ms, update_maps {_sw.ElapsedMilliseconds - tSet} ms");

        // B. Rewrite the region's whole height image from our data, then update_maps.
        _sw.Restart();
        var region = _data.Call("get_regionp", centre).AsGodotObject();
        var loc = region.Call("get_location").AsVector2I();
        var bytes = new byte[RegionSize * RegionSize * 4];
        var src = _map.Data;
        for (int z = 0; z < RegionSize; z++)
        {
            int gz = loc.Y * RegionSize + z;
            MemoryMarshal.AsBytes(src.Slice(gz * _map.Width + loc.X * RegionSize, RegionSize)).CopyTo(bytes.AsSpan(z * RegionSize * 4, RegionSize * 4));
        }
        var img = Image.CreateFromData(RegionSize, RegionSize, false, Image.Format.Rf, bytes);
        long tBuild = _sw.ElapsedMilliseconds;
        region.Call("set_height_map", img);
        region.Call("set_edited", true);
        region.Call("set_modified", true);
        long tSetMap = _sw.ElapsedMilliseconds;
        _data.Call("update_maps", 0, false, false);
        long tUpd = _sw.ElapsedMilliseconds;
        float back = _data.Call("get_height", centre).AsSingle();
        Log($"edit B: region image {tBuild} ms, set_height_map {tSetMap - tBuild} ms, update_maps {tUpd - tSetMap} ms " +
            $"(restored height {back:0.00} vs ours {_map.SampleHeight(centre.X, centre.Z):0.00})");

        // C. get_height query cost.
        _sw.Restart();
        float sum = 0;
        for (int i = 0; i < 10000; i++)
            sum += _data.Call("get_height", centre + new Vector3(i % 100, 0, i / 100)).AsSingle();
        Log($"get_height x10000 via Call: {_sw.ElapsedMilliseconds} ms; HeightMap.SampleHeight x10000: {TimeOurs(centre)} ms");
    }

    private double TimeOurs(Vector3 c)
    {
        var sw = Stopwatch.StartNew();
        float sum = 0;
        for (int i = 0; i < 10000; i++) sum += _map.SampleHeight(c.X + i % 100, c.Z + i / 100);
        return sw.Elapsed.TotalMilliseconds;
    }

    private static long RssMb() => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

    private static void Log(string s) => GD.Print($"Spike: {s}");
}
