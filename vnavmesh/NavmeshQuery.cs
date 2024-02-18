using DotRecast.Detour;
using Navmesh.NavVolume;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

public class NavmeshQuery
{
    public DtNavMeshQuery MeshQuery;
    public VoxelPathfind VolumeQuery;
    private IDtQueryFilter _filter = new DtQueryDefaultFilter();

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh);
        VolumeQuery = new(navmesh.Volume);
    }

    public List<Vector3> PathfindMesh(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling)
    {
        var startRef = FindNearestMeshPoly(from);
        var endRef = FindNearestMeshPoly(to);
        Service.Log.Debug($"[pathfind] poly {startRef:X} -> {endRef:X}");
        if (startRef == 0 || endRef == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find polygon on a mesh");
            return new();
        }

        var polysPath = new List<long>();
        var opt = new DtFindPathOption(useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, float.MaxValue);
        MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), _filter, ref polysPath, opt);
        if (polysPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find path on mesh");
            return new();
        }
        Service.Log.Debug($"Pathfind: {string.Join(", ", polysPath.Select(r => r.ToString("X")))}");

        // In case of partial path, make sure the end point is clamped to the last polygon.
        var endPos = to.SystemToRecast();
        if (polysPath.Last() != endRef)
            if (MeshQuery.ClosestPointOnPoly(polysPath.Last(), endPos, out var closest, out _).Succeeded())
                endPos = closest;

        if (useStringPulling)
        {
            var straightPath = new List<DtStraightPath>();
            var success = MeshQuery.FindStraightPath(from.SystemToRecast(), endPos, polysPath, ref straightPath, 1024, 0);
            if (success.Failed())
                Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");
            return straightPath.Select(p => p.pos.RecastToSystem()).ToList();
        }
        else
        {
            var res = polysPath.Select(r => MeshQuery.GetAttachedNavMesh().GetPolyCenter(r).RecastToSystem()).ToList();
            res.Add(endPos.RecastToSystem());
            return res;
        }
    }

    public List<Vector3> PathfindVolume(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling)
    {
        var startVoxel = FindNearestVolumeVoxel(from);
        var endVoxel = FindNearestVolumeVoxel(to);
        Service.Log.Debug($"[pathfind] voxel {startVoxel:X} -> {endVoxel:X}");
        if (startVoxel < 0 || endVoxel < 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find empty voxel");
            return new();
        }

        var voxelPath = VolumeQuery.FindPath(startVoxel, endVoxel, from, to, useRaycast, false); // TODO: do we need intermediate points for string-pulling algo?
        if (voxelPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find path on volume");
            return new();
        }
        Service.Log.Debug($"Pathfind: {string.Join(", ", voxelPath.Select(r => $"{r.p} {VolumeQuery.Volume.IndexToVoxel(r.voxel)}"))}");

        // TODO: string-pulling support
        var res = voxelPath.Select(r => r.p).ToList();
        res.Add(to);
        return res;
    }

    // returns 0 if not found, otherwise polygon ref
    public long FindNearestMeshPoly(Vector3 p, float radius = 2)
    {
        MeshQuery.FindNearestPoly(p.SystemToRecast(), new(radius), _filter, out var nearestRef, out _, out _);
        return nearestRef;
    }

    // returns -1 if not found, otherwise voxel index
    public int FindNearestVolumeVoxel(Vector3 p, float radius = 5) => VoxelSearch.FindNearestEmptyVoxel(VolumeQuery.Volume, p, radius);
}
