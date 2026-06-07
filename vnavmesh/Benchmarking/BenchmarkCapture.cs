using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Navmesh.Benchmarking;

internal static class BenchmarkCapture
{
    private const int DefaultSeed = 0x6E61766D; // "navm"

    public static string Capture(NavmeshManager manager, string configDirectory, string name, int pairsPerMode)
    {
        if (manager.Navmesh == null)
            throw new InvalidOperationException("Navmesh is not loaded");

        pairsPerMode = Math.Clamp(pairsPerMode, 1, 10_000);

        var random = new Random(DefaultSeed);
        var pairs = new List<BenchmarkPathPair>();
        AddMeshPairs(pairs, manager.Navmesh, random, pairsPerMode);
        AddVolumePairs(pairs, manager.Navmesh, random, pairsPerMode);

        var safeName = SanitizeName(string.IsNullOrWhiteSpace(name) ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") : name);
        var directory = Path.Combine(configDirectory, "benchmarks", safeName);
        var metadata = new BenchmarkSnapshotMetadata
        {
            SourceKey = manager.CurrentKey,
            TerritoryId = Service.ClientState.TerritoryType,
            CustomizationVersion = manager.Navmesh.CustomizationVersion,
            PairSeed = DefaultSeed,
            RequestedPairsPerMode = pairsPerMode,
            Pairs = pairs,
        };

        BenchmarkSnapshotFiles.Save(directory, metadata, manager.Navmesh);
        return directory;
    }

    private static void AddMeshPairs(List<BenchmarkPathPair> pairs, Navmesh navmesh, Random random, int count)
    {
        var points = CollectMeshPoints(navmesh.Mesh);
        AddPairs(pairs, points, random, count, false);
    }

    private static List<Vector3> CollectMeshPoints(DtNavMesh mesh)
    {
        var points = new List<Vector3>();
        for (var i = 0; i < mesh.GetMaxTiles(); i++)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;

            var polyBase = mesh.GetPolyRefBase(tile);
            for (var j = 0; j < tile.data.header.polyCount; j++)
            {
                var poly = tile.data.polys[j];
                if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND)
                    continue;
                if ((poly.flags & Navmesh.FLAG_UNREACHABLE) != 0)
                    continue;

                points.Add(mesh.GetPolyCenter(polyBase | (uint)j).RecastToSystem());
            }
        }

        return points;
    }

    private static void AddVolumePairs(List<BenchmarkPathPair> pairs, Navmesh navmesh, Random random, int count)
    {
        if (navmesh.Volume == null)
            return;

        var volume = navmesh.Volume;
        var points = new List<Vector3>();
        var seen = new HashSet<ulong>();
        var min = volume.RootTile.BoundsMin;
        var max = volume.RootTile.BoundsMax;
        var attempts = Math.Max(10_000, count * 300);
        while (points.Count < count * 2 && attempts-- > 0)
        {
            var p = new Vector3(
                Lerp(random, min.X, max.X),
                Lerp(random, min.Y, max.Y),
                Lerp(random, min.Z, max.Z));
            var (voxel, empty) = volume.FindLeafVoxel(p);
            if (!empty || !seen.Add(voxel))
                continue;

            var (vmin, vmax) = volume.VoxelBounds(voxel, 0.1f);
            points.Add((vmin + vmax) * 0.5f);
        }

        AddPairs(pairs, points, random, count, true);
    }

    private static void AddPairs(List<BenchmarkPathPair> pairs, IReadOnlyList<Vector3> points, Random random, int count, bool fly)
    {
        if (points.Count < 2)
            return;

        for (var i = 0; i < count; i++)
        {
            var a = random.Next(points.Count);
            var b = random.Next(points.Count - 1);
            if (b >= a)
                b++;

            pairs.Add(new(
                BenchmarkPoint.FromVector3(points[a]),
                BenchmarkPoint.FromVector3(points[b]),
                fly));
        }
    }

    private static float Lerp(Random random, float min, float max) => min + (max - min) * (float)random.NextDouble();

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
