using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.NavVolume;

public class PathfindLoopException(ulong from, ulong to, Vector3 fromP, Vector3 toP) : Exception {
    public readonly ulong FromVoxel = from;
    public readonly ulong ToVoxel = to;
    public readonly Vector3 FromPos = fromP;
    public readonly Vector3 ToPos = toP;

    public override string Message => $"An infinite loop occurred during the pathfind operation. (from={FromVoxel:X} / {FromPos}, to={ToVoxel:X} / {ToPos})";
}

public static class VoxelSearch
{
    public static IEnumerable<(ulong index, bool empty)> EnumerateLeafVoxels(VoxelMap volume, Vector3 center, Vector3 halfExtent)
        => volume.RootTile.EnumerateLeafVoxels(center - halfExtent, center + halfExtent);

    public static Vector3 FindClosestVoxelPoint(VoxelMap volume, ulong index, Vector3 p)
    {
        var (min, max) = volume.VoxelBounds(index, 0.1f);
        return Vector3.Clamp(p, min, max);
    }

    public static ulong FindNearestEmptyVoxel(VoxelMap volume, Vector3 center, Vector3 halfExtent)
    {
        var cv = volume.FindLeafVoxel(center);
        //Service.Log.Debug($"Searching {cv}");
        if (cv.empty)
            return cv.voxel; // fast path: the cell is empty already

        float minDist = float.MaxValue;
        ulong res = VoxelMap.InvalidVoxel;
        foreach (var v in volume.RootTile.EnumerateLeafVoxels(center - halfExtent, center + halfExtent))
        {
            if (!v.empty)
                continue;

            var p = FindClosestVoxelPoint(volume, v.index, center);
            var d = p - center;
            var dist = d.LengthSquared();
            if (d.X != 0 || d.Z != 0)
                dist += 100; // penalty for moving sideways vs up - TODO reconsider...
            if (d.Y < 0)
                dist += 400; // penalty for lower voxels to reduce chance of it being underground - TODO reconsider...
            //Service.Log.Debug($"Considering {x}x{y}x{z}: {dist}, min so far {minDist}");

            if (dist < minDist)
            {
                minDist = dist;
                res = v.index;
            }
        }
        return res;
    }

    // enumerate entered voxels along line; starting voxel is not returned, ending voxel is
    public static IEnumerable<(ulong voxel, float t, bool empty)> EnumerateVoxelsInLine(VoxelMap volume, ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        var origFrom = fromVoxel;
        var ab = toPos - fromPos;
        var eps = 0.1f / ab.Length();
        while (fromVoxel != toVoxel)
        {
            var (vmin, vmax) = volume.VoxelBounds(fromVoxel, 0);
            // find closest intersection among three (out of six) neighbours
            // line-plane intersection: Q = A + AB*t, PQ*n=0 => (PA + tAB)*n = 0 => t = AP*n / AB*n
            var tx = ab.X == 0 ? float.MaxValue : ((ab.X > 0 ? vmax.X : vmin.X) - fromPos.X) / ab.X;
            var ty = ab.Y == 0 ? float.MaxValue : ((ab.Y > 0 ? vmax.Y : vmin.Y) - fromPos.Y) / ab.Y;
            var tz = ab.Z == 0 ? float.MaxValue : ((ab.Z > 0 ? vmax.Z : vmin.Z) - fromPos.Z) / ab.Z;
            var t = Math.Min(Math.Min(tx, ty), Math.Min(tz, 1));
            // Service.Log.Debug($"{fromVoxel:X} -> {toVoxel:X}: t={tx:f3}x{ty:f3}x{tz:f3}={t:f3}");
            var tAdj = Math.Min(t + eps, 1);
            var proj = fromPos + tAdj * ab;
            var (nextVoxel, nextEmpty) = volume.FindLeafVoxel(proj.Floor());
            if (nextVoxel == fromVoxel)
                throw new PathfindLoopException(origFrom, toVoxel, fromPos, toPos);
            yield return (nextVoxel, t, nextEmpty);
            fromVoxel = nextVoxel;
        }
    }

    public static bool LineOfSight(VoxelMap volume, ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos)
        => EnumerateVoxelsInLine(volume, fromVoxel, toVoxel, fromPos, toPos).All(v => v.empty);
}
