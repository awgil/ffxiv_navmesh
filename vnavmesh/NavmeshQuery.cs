using DotRecast.Detour;
using Navmesh.NavVolume;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

public class NavmeshQuery
{
    public DtNavMeshQuery MeshQuery;
    public PathfindQuery VolumeQuery;
    private IDtQueryFilter _filter = new DtQueryDefaultFilter();

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh);
        VolumeQuery = new(navmesh.Volume);
    }

    public List<Vector3> PathfindMesh(Vector3 from, Vector3 to)
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
        var opt = new DtFindPathOption(DtFindPathOptions.DT_FINDPATH_ANY_ANGLE, float.MaxValue);
        MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), _filter, ref polysPath, opt);
        if (polysPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find path on mesh");
            return new();
        }

        // In case of partial path, make sure the end point is clamped to the last polygon.
        var endPos = to.SystemToRecast();
        if (polysPath.Last() != endRef)
            if (MeshQuery.ClosestPointOnPoly(polysPath.Last(), endPos, out var closest, out _).Succeeded())
                endPos = closest;

        var straightPath = new List<DtStraightPath>();
        var success = MeshQuery.FindStraightPath(from.SystemToRecast(), endPos, polysPath, ref straightPath, 256, 0);
        if (success.Failed())
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");
            return new();
        }

        return straightPath.Select(p => p.pos.RecastToSystem()).ToList();
    }

    public List<Vector3> PathfindVolume(Vector3 from, Vector3 to)
    {
        return VolumeQuery.FindPath(from, to);
    }

    // returns 0 if not found, otherwise polygon ref
    public long FindNearestMeshPoly(Vector3 p, float radius = 2)
    {
        MeshQuery.FindNearestPoly(p.SystemToRecast(), new(radius), _filter, out var nearestRef, out _, out _);
        return nearestRef;
    }
}
