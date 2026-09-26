using Godot;

namespace CitySim.TerrainSystem;

/// <summary>One rectangular tile of the terrain mesh, built directly from the shared heightmap.</summary>
public partial class TerrainChunk : MeshInstance3D
{
    private HeightMap? _map;
    private int _startX, _startZ, _cellsX, _cellsZ;

    public Vector2I ChunkCoord { get; private set; }

    public void Init(HeightMap map, Vector2I chunkCoord, int chunkCells)
    {
        _map = map;
        ChunkCoord = chunkCoord;
        _startX = chunkCoord.X * chunkCells;
        _startZ = chunkCoord.Y * chunkCells;
        _cellsX = Mathf.Min(chunkCells, map.Width - 1 - _startX);
        _cellsZ = Mathf.Min(chunkCells, map.Depth - 1 - _startZ);
        Name = $"Chunk_{chunkCoord.X}_{chunkCoord.Y}";
        Position = new Vector3(_startX * map.CellSize, 0f, _startZ * map.CellSize);
    }

    public void Rebuild()
    {
        if (_map is null) return;

        int vx = _cellsX + 1;
        int vz = _cellsZ + 1;
        float cell = _map.CellSize;
        float invW = 1f / (_map.Width - 1);
        float invD = 1f / (_map.Depth - 1);

        var vertices = new Vector3[vx * vz];
        var normals = new Vector3[vx * vz];
        var uvs = new Vector2[vx * vz];
        var indices = new int[_cellsX * _cellsZ * 6];

        for (int j = 0; j < vz; j++)
        {
            int gz = _startZ + j;
            for (int i = 0; i < vx; i++)
            {
                int gx = _startX + i;
                int v = j * vx + i;
                vertices[v] = new Vector3(i * cell, _map[gx, gz], j * cell);
                var n = _map.GetNormal(gx, gz);
                normals[v] = new Vector3(n.X, n.Y, n.Z);
                uvs[v] = new Vector2(gx * invW, gz * invD);
            }
        }

        // Godot treats clockwise triangles (seen from the front) as front faces.
        int k = 0;
        for (int j = 0; j < _cellsZ; j++)
        {
            for (int i = 0; i < _cellsX; i++)
            {
                int a = j * vx + i;
                int b = a + 1;
                int c = a + vx;
                int d = c + 1;
                indices[k++] = a; indices[k++] = b; indices[k++] = d;
                indices[k++] = a; indices[k++] = d; indices[k++] = c;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = mesh;
    }
}
