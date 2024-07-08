using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.NavVolume;

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
        var ab = toPos - fromPos;
        var eps = 0.1f / ab.Length();
        var iters = 0;
        while (fromVoxel != toVoxel)
        {
            iters += 1;
            if (iters > 10000)
                throw new Exception("Too many iterations in EnumerateVoxelsInLine (> 10000)");
            var bounds = volume.VoxelBounds(fromVoxel, 0);
            // find closest intersection among three (out of six) neighbours
            // line-plane intersection: Q = A + AB*t, PQ*n=0 => (PA + tAB)*n = 0 => t = AP*n / AB*n
            // TODO: think more about path right along the border
            var tx = ab.X == 0 ? float.MaxValue : ((ab.X > 0 ? bounds.max.X : bounds.min.X) - fromPos.X) / ab.X;
            var ty = ab.Y == 0 ? float.MaxValue : ((ab.Y > 0 ? bounds.max.Y : bounds.min.Y) - fromPos.Y) / ab.Y;
            var tz = ab.Z == 0 ? float.MaxValue : ((ab.Z > 0 ? bounds.max.Z : bounds.min.Z) - fromPos.Z) / ab.Z;
            //Service.Log.Debug($"{fromVoxel:X} -> {toVoxel:X}: t={tx:f3}x{ty:f3}x{tz:f3}");
            var t = Math.Min(Math.Min(tx, ty), Math.Min(tz, 1));
            var tAdj = Math.Min(t + eps, 1);
            var (nextVoxel, nextEmpty) = volume.FindLeafVoxel(fromPos + tAdj * ab);
            yield return (nextVoxel, t, nextEmpty);
            fromVoxel = nextVoxel;
            if (tAdj >= 1)
                yield break;
        }
    }

    public static bool LineOfSight(VoxelMap volume, ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        foreach (var v in EnumerateVoxelsInLine(volume, fromVoxel, toVoxel, fromPos, toPos))
            if (!v.empty)
                return false;
        return true;
    }
}
