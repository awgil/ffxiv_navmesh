using System.Numerics;

namespace Navmesh.NavVolume;

public static class VoxelSearch
{
    public static int FindNearestEmptyVoxel(VoxelMap volume, Vector3 center, float radius)
    {
        var (cx, cy, cz) = volume.WorldToVoxel(center);
        if (volume.InBounds(cx, cy, cz) && !volume[cx, cy, cz])
            return volume.VoxelToIndex(cx, cy, cz); // fast path: the cell is empty already

        var maxDistSq = radius * radius;

        // search plane at z = cz
        var res = cz >= 0 && cz < volume.NumCellsZ ? FindNearestEmptyVoxelXY(volume, center, ref maxDistSq, 0, cx, cy, cz) : -1;
        //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {cz} -> {res}");

        // search nearby planes
        var distFromNeg = center.Z - (volume.BoundsMin.Z + cz * volume.CellSize.Z);
        var distFromPos = volume.CellSize.Z - distFromNeg;
        for (int dz = 1; ; ++dz)
        {
            bool outOfBounds = true;

            // search plane at -dz
            var d = distFromNeg * distFromNeg;
            var z = cz - dz;
            if (d <= maxDistSq && z >= 0)
            {
                var alt = z < volume.NumCellsZ ? FindNearestEmptyVoxelXY(volume, center, ref maxDistSq, d, cx, cy, z) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {z} -> {res}");
                }
                outOfBounds = false;
                distFromNeg += volume.CellSize.Z;
            }

            // search plane at +dz
            d = distFromPos * distFromPos;
            z = cz + dz;
            if (d <= maxDistSq && z < volume.NumCellsZ)
            {
                var alt = z >= 0 ? FindNearestEmptyVoxelXY(volume, center, ref maxDistSq, d, cx, cy, z) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {z} -> {res}");
                }
                outOfBounds = false;
                distFromPos += volume.CellSize.Z;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }

    private static int FindNearestEmptyVoxelXY(VoxelMap volume, Vector3 center, ref float maxDistSq, float planeDistSq, int cx, int cy, int cz)
    {
        // search column at (cx,cz)
        var res = cx >= 0 && cx < volume.NumCellsX ? FindNearestEmptyVoxelY(volume, center, ref maxDistSq, planeDistSq, cx, cy, cz) : -1;
        //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {cx}x{cz} -> {res}");

        // search nearby columns
        var distFromNeg = center.X - (volume.BoundsMin.X + cx * volume.CellSize.X);
        var distFromPos = volume.CellSize.X - distFromNeg;
        for (int dx = 1; ; ++dx)
        {
            bool outOfBounds = true;

            // search column at -dx
            var d = distFromNeg * distFromNeg + planeDistSq;
            var x = cx - dx;
            if (d <= maxDistSq && x >= 0)
            {
                var alt = x < volume.NumCellsX ? FindNearestEmptyVoxelY(volume, center, ref maxDistSq, d, x, cy, cz) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {x}x{cz} -> {res}");
                }
                outOfBounds = false;
                distFromNeg += volume.CellSize.X;
            }

            // search column at +dx
            d = distFromPos * distFromPos + planeDistSq;
            x = cx + dx;
            if (d <= maxDistSq && x < volume.NumCellsX)
            {
                var alt = x >= 0 ? FindNearestEmptyVoxelY(volume, center, ref maxDistSq, d, x, cy, cz) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {x}x{cz} -> {res}");
                }
                outOfBounds = false;
                distFromPos += volume.CellSize.X;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }

    private static int FindNearestEmptyVoxelY(VoxelMap volume, Vector3 center, ref float maxDistSq, float columnDistSq, int cx, int cy, int cz)
    {
        // check voxel at cy
        if (cy >= 0 && cy < volume.NumCellsY && !volume[cx, cy, cz])
        {
            maxDistSq = columnDistSq;
            //Service.Log.Debug($"Found: {cx}x{cy}x{cz} -> {maxDistSq}");
            return volume.VoxelToIndex(cx, cy, cz);
        }

        // search up and down
        var distFromNeg = center.Y - (volume.BoundsMin.Y + cy * volume.CellSize.Y);
        var distFromPos = volume.CellSize.Y - distFromNeg;
        distFromNeg += 5 * volume.CellSize.Y; // TODO: this is an arbitrary penalty for 'underground unblocked' voxels; improve this...
        int res = -1;
        for (int dy = 1; ; ++dy)
        {
            bool outOfBounds = true;

            // search voxel at -dy
            var d = distFromNeg * distFromNeg + columnDistSq;
            var y = cy - dy;
            if (d <= maxDistSq && y >= 0)
            {
                if (y < volume.NumCellsY && !volume[cx, y, cz])
                {
                    maxDistSq = d;
                    //Service.Log.Debug($"Found: {cx}x{y}x{cz} -> {maxDistSq}");
                    res = volume.VoxelToIndex(cx, y, cz);
                }
                outOfBounds = false;
                distFromNeg += volume.CellSize.Y;
            }

            // search voxel at +dy
            d = distFromPos * distFromPos + columnDistSq;
            y = cy + dy;
            if (d <= maxDistSq && y < volume.NumCellsY)
            {
                if (y >= 0 && !volume[cx, y, cz])
                {
                    maxDistSq = d;
                    //Service.Log.Debug($"Found: {cx}x{y}x{cz} -> {maxDistSq}");
                    res = volume.VoxelToIndex(cx, y, cz);
                }
                outOfBounds = false;
                distFromPos += volume.CellSize.Y;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }
}
