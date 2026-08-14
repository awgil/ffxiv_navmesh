using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.NavVolume;

// Greedy line-of-sight simplification of a path returned by VoxelPathfind.
//
// The search returns one point per voxel it entered, so a route through open air comes back as a
// stair-stepped chain rather than a line, and following it exactly is what makes flight look
// mechanical. Mesh paths get Detour's FindStraightPath; the volume has no equivalent, and the exact
// funnel this would need is hard on an octree (see the abandoned attempt in VoxelStraighten.cs).
// So this does the cheap version: from an anchor, walk forward while the straight line to the
// candidate stays inside empty voxels, and keep the last candidate that held.
//
// The line test is VoxelSearch.LineOfSight, which is the same primitive the search already uses to
// collapse grandparent hops when raycasts are enabled. So this removes points the pathfind would
// have been willing to skip anyway; it does not cut anything new.
public static class VoxelStringPull
{
    public static List<(ulong voxel, Vector3 p)> Simplify(VoxelMap volume, List<(ulong voxel, Vector3 p)> path)
    {
        if (path.Count < 3)
            return path;

        var res = new List<(ulong voxel, Vector3 p)>(path.Count) { path[0] };
        var anchor = 0;
        while (anchor < path.Count - 1)
        {
            // the immediate next point never needs a test: reaching it is how the search got here
            var best = anchor + 1;
            for (var j = anchor + 2; j < path.Count; ++j)
            {
                if (!Visible(volume, path[anchor], path[j]))
                    break; // stop at the first blocked candidate rather than looking past it, so a
                           // gap that happens to reopen further along cannot pull the path through
                best = j;
            }
            res.Add(path[best]);
            anchor = best;
        }
        return res;
    }

    private static bool Visible(VoxelMap volume, (ulong voxel, Vector3 p) from, (ulong voxel, Vector3 p) to)
    {
        try
        {
            return VoxelSearch.LineOfSight(volume, from.voxel, to.voxel, from.p, to.p);
        }
        catch (PathfindLoopException)
        {
            // the walk along the line failed to advance, so nothing can be claimed about it; keeping
            // the point is always safe, and a throw here would take down a pathfind that succeeded
            return false;
        }
    }
}
