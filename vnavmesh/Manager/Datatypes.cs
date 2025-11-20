using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;

namespace Navmesh;

//public readonly record struct TileObject(SceneExtractor.Mesh Mesh, SceneExtractor.MeshInstance Instance, InstanceType Type);
public readonly record struct Tile(int X, int Z, SortedDictionary<ulong, InstanceWithMesh> Objects, ReadOnlyDictionary<string, SceneExtractor.Mesh> AllMeshes, NavmeshCustomization Customization, uint Zone)
{
    public readonly IEnumerable<InstanceWithMesh> ObjectsByMesh(Func<SceneExtractor.Mesh, bool> func) => Objects.Values.Where(o => func(o.Mesh));
    public readonly IEnumerable<InstanceWithMesh> ObjectsByPath(string path) => ObjectsByMesh(m => m.Path == path);

    public readonly void RemoveObjects(Func<InstanceWithMesh, bool> filter)
    {
        foreach (var k in Objects.Keys.ToList())
            if (filter(Objects[k]))
                Objects.Remove(k);
    }

    public readonly string GetCacheKey()
    {
        var span = Objects.Keys.ToArray();
        var bytes = new byte[span.Length * sizeof(ulong)];
        Buffer.BlockCopy(span, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(MD5.HashData(bytes));
    }
}

public partial class ColliderSet
{
    internal TileInternal?[,] _tiles = new TileInternal?[0, 0];
    internal class TileInternal(int x, int z)
    {
        public int X = x;
        public int Z = z;
        public ConcurrentDictionary<ulong, InstanceWithMesh> Objects = [];
        public bool Changed;
    }
}
