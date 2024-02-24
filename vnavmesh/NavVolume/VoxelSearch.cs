using System.Numerics;

namespace Navmesh.NavVolume;

public static class VoxelSearch
{
    public static int FindNearestEmptyVoxel(VoxelMap volume, Vector3 center, int halfSize)
    {
        var cv = volume.WorldToVoxel(center);
        //Service.Log.Debug($"Searching {cv}");
        if (volume.InBounds(cv) && !volume[cv.x, cv.y, cv.z])
            return volume.VoxelToIndex(cv); // fast path: the cell is empty already

        var min = volume.Clamp((cv.x - halfSize, cv.y - halfSize, cv.z - halfSize));
        var max = volume.Clamp((cv.x + halfSize, cv.y + halfSize, cv.z + halfSize));
        float minDist = float.MaxValue;
        var res = (0, 0, 0);
        for (int z = min.z; z <= max.z; ++z)
        {
            for (int x = min.x; x <= max.x; ++x)
            {
                for (int y = min.y; y <= max.y; ++y)
                {
                    if (!volume[x, y, z])
                    {
                        var dist = (center - volume.VoxelToWorld(x, y, z)).Length();
                        if (x != cv.x || z != cv.z)
                            dist += volume.CellSize.Y; // penalty for moving sideways vs up - TODO reconsider...
                        if (y < cv.y)
                            dist += 2 * volume.CellSize.Y; // penalty for lower voxels to reduce chance of it being underground - TODO reconsider...
                        //Service.Log.Debug($"Considering {x}x{y}x{z}: {dist}, min so far {minDist}");
                        if (dist < minDist)
                        {
                            minDist = dist;
                            res = (x, y, z);
                        }
                    }
                }
            }
        }
        return minDist < float.MaxValue ? volume.VoxelToIndex(res) : -1;
    }
}
