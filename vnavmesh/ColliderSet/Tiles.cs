using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;

namespace Navmesh;

public readonly record struct TileObject(SceneExtractor.Mesh Mesh, SceneExtractor.MeshInstance Instance, InstanceType Type);
public readonly record struct TileObjects(int X, int Z, SortedDictionary<ulong, TileObject> Objects, ReadOnlyDictionary<string, SceneExtractor.Mesh> AllMeshes, NavmeshCustomization Customization, uint Zone)
{
    public readonly IEnumerable<TileObject> ObjectsByMesh(Func<SceneExtractor.Mesh, bool> func) => Objects.Values.Where(o => func(o.Mesh));
    public readonly IEnumerable<TileObject> ObjectsByPath(string path) => ObjectsByMesh(m => m.Path == path);

    public readonly void RemoveObjects(Func<TileObject, bool> filter)
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
        public ConcurrentDictionary<ulong, TileObject> Objects = [];
        public bool Changed;
    }

    public IEnumerable<TileObjects> GetTileChanges()
    {
        if (!_anyChanged)
            yield break;

        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (_tiles[i, j]?.Changed == true)
                {
                    _tiles[i, j]!.Changed = false;
                    yield return GetOneTile(i, j)!.Value;
                }
    }

    public IEnumerable<TileObjects> GetAllTiles()
    {
        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (GetOneTile(i, j) is { } t)
                    yield return t;
    }

    public TileObjects? GetOneTile(int i, int j)
    {
        var t = _tiles[i, j];
        if (t == null)
            return null;
        return new(t.X, t.Z, new SortedDictionary<ulong, TileObject>(t.Objects), Meshes.AsReadOnly(), Customization, LastLoadedZone);
    }
}
