using System.Collections.Generic;
using Godot;
using CitySim.TerrainSystem.Erosion;

namespace CitySim.TerrainSystem;

/// <summary>
/// Draws the lakes of a <see cref="LakeMap"/>: flat quads at each lake's level over every cell that touches a wet
/// vertex, one mesh per <see cref="Tile"/>² block, only where there's water. Quads reach one cell into the bank, so the
/// shoreline is where the terrain cuts through the water. Runs of cells in a row with the same level become one quad.
/// </summary>
public partial class LakeWater : Node3D
{
    /// <summary>Cells per side of one mesh.</summary>
    public const int Tile = 256;

    /// <summary>Vertices and indices for the wet tiles. Pure data, so it can be built on a worker thread.</summary>
    public sealed record Arrays(List<(Vector3[] Vertices, int[] Indices)> Tiles);

    public Material? Material { get; set; }

    public static Arrays Build(LakeMap lakes)
    {
        var tiles = new List<(Vector3[], int[])>();
        int cellsX = lakes.Width - 1, cellsZ = lakes.Depth - 1;
        float cs = lakes.CellSize;
        var verts = new List<Vector3>();
        var idx = new List<int>();
        for (int tz = 0; tz < cellsZ; tz += Tile)
            for (int tx = 0; tx < cellsX; tx += Tile)
            {
                verts.Clear();
                idx.Clear();
                int x1 = System.Math.Min(tx + Tile, cellsX), z1 = System.Math.Min(tz + Tile, cellsZ);
                for (int z = tz; z < z1; z++)
                {
                    int runStart = -1;
                    float runLevel = float.NaN;
                    for (int x = tx; x <= x1; x++)
                    {
                        float level = x < x1 ? CellLevel(lakes, x, z) : float.NaN;
                        if (runStart >= 0 && level != runLevel)
                        {
                            Quad(runStart, x, z, runLevel);
                            runStart = -1;
                        }
                        if (runStart < 0 && !float.IsNaN(level))
                        {
                            runStart = x;
                            runLevel = level;
                        }
                    }
                }
                if (idx.Count > 0) tiles.Add((verts.ToArray(), idx.ToArray()));
            }
        return new Arrays(tiles);

        void Quad(int xa, int xb, int z, float y)
        {
            int b = verts.Count;
            verts.Add(new Vector3(xa * cs, y, z * cs));
            verts.Add(new Vector3(xb * cs, y, z * cs));
            verts.Add(new Vector3(xa * cs, y, (z + 1) * cs));
            verts.Add(new Vector3(xb * cs, y, (z + 1) * cs));
            // Clockwise seen from above (Godot's front face).
            idx.AddRange([b, b + 1, b + 2, b + 2, b + 1, b + 3]);
        }
    }

    /// <summary>Water level over cell (x, z): the highest lake level at its four corners, or NaN if all are dry.</summary>
    private static float CellLevel(LakeMap lakes, int x, int z)
    {
        float a = lakes.LevelAt(x, z), b = lakes.LevelAt(x + 1, z), c = lakes.LevelAt(x, z + 1), d = lakes.LevelAt(x + 1, z + 1);
        float m = float.NaN;
        if (!float.IsNaN(a)) m = a;
        if (!float.IsNaN(b) && !(b <= m)) m = b;
        if (!float.IsNaN(c) && !(c <= m)) m = c;
        if (!float.IsNaN(d) && !(d <= m)) m = d;
        return m;
    }

    /// <summary>Replaces the meshes (main thread).</summary>
    public void Apply(Arrays arrays)
    {
        foreach (var child in GetChildren()) child.QueueFree();
        foreach (var (vertices, indices) in arrays.Tiles)
        {
            var normals = new Vector3[vertices.Length];
            System.Array.Fill(normals, Vector3.Up);
            var data = new Godot.Collections.Array();
            data.Resize((int)Mesh.ArrayType.Max);
            data[(int)Mesh.ArrayType.Vertex] = vertices;
            data[(int)Mesh.ArrayType.Normal] = normals;
            data[(int)Mesh.ArrayType.Index] = indices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, data);
            AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = Material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }
}
