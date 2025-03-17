using DotRecast.Detour;
using Navmesh.NavVolume;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Navmesh;

public class NavmeshQuery
{
    private class IntersectQuery : IDtPolyQuery
    {
        public List<long> Result = new();
        public void Process(DtMeshTile tile, DtPoly poly, long refs) => Result.Add(refs);
    }

    public DtNavMeshQuery MeshQuery;
    public VoxelPathfind? VolumeQuery;
    private IDtQueryFilter _filter = new DtQueryDefaultFilter();

    public List<long> LastPath => _lastPath;
    private List<long> _lastPath = [];

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh);
        if (navmesh.Volume != null)
            VolumeQuery = new(navmesh.Volume);
    }

    public List<Vector3> PathfindMesh(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel)
    {
        var startRef = FindNearestMeshPoly(from);
        var endRef = FindNearestMeshPoly(to);
        Service.Log.Debug($"[pathfind] poly {startRef:X} -> {endRef:X}");
        if (startRef == 0 || endRef == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find polygon on a mesh");
            return new();
        }

        var timer = Timer.Create();
        _lastPath.Clear();
        var opt = new DtFindPathOption(useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? 5 : 0);
        MeshQuery.FindPath(startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), _filter, ref _lastPath, opt);
        if (_lastPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find path on mesh");
            return new();
        }
        Service.Log.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", _lastPath.Select(r => r.ToString("X")))}");

        // In case of partial path, make sure the end point is clamped to the last polygon.
        var endPos = to.SystemToRecast();
        //if (polysPath.Last() != endRef)
        //    if (MeshQuery.ClosestPointOnPoly(polysPath.Last(), endPos, out var closest, out _).Succeeded())
        //        endPos = closest;

        if (useStringPulling)
        {
            var straightPath = new List<DtStraightPath>();
            var success = MeshQuery.FindStraightPath(from.SystemToRecast(), endPos, _lastPath, ref straightPath, 1024, 0);
            if (success.Failed())
                Service.Log.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");
            var res = straightPath.Select(p => p.pos.RecastToSystem()).ToList();
            res.Add(endPos.RecastToSystem());
            return res;
        }
        else
        {
            var res = _lastPath.Select(r => MeshQuery.GetAttachedNavMesh().GetPolyCenter(r).RecastToSystem()).ToList();
            res.Add(endPos.RecastToSystem());
            return res;
        }
    }

    public List<Vector3> PathfindVolume(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel)
    {
        if (VolumeQuery == null)
        {
            Service.Log.Error($"Nav volume was not built");
            return new();
        }

        var startVoxel = FindNearestVolumeVoxel(from);
        var endVoxel = FindNearestVolumeVoxel(to);
        Service.Log.Debug($"[pathfind] voxel {startVoxel:X} -> {endVoxel:X}");
        if (startVoxel == VoxelMap.InvalidVoxel || endVoxel == VoxelMap.InvalidVoxel)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find empty voxel");
            return new();
        }

        var timer = Timer.Create();
        var voxelPath = VolumeQuery.FindPath(startVoxel, endVoxel, from, to, useRaycast, false, cancel); // TODO: do we need intermediate points for string-pulling algo?
        if (voxelPath.Count == 0)
        {
            Service.Log.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find path on volume");
            return new();
        }
        Service.Log.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", voxelPath.Select(r => $"{r.p} {r.voxel:X}"))}");

        // TODO: string-pulling support
        var res = voxelPath.Select(r => r.p).ToList();
        res.Add(to);
        return res;
    }

    // returns 0 if not found, otherwise polygon ref
    public long FindNearestMeshPoly(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5)
    {
        MeshQuery.FindNearestPoly(p.SystemToRecast(), new(halfExtentXZ, halfExtentY, halfExtentXZ), _filter, out var nearestRef, out _, out _);
        return nearestRef;
    }

    public List<long> FindIntersectingMeshPolys(Vector3 p, Vector3 halfExtent)
    {
        IntersectQuery query = new();
        MeshQuery.QueryPolygons(p.SystemToRecast(), halfExtent.SystemToRecast(), _filter, query);
        return query.Result;
    }

    public Vector3? FindNearestPointOnMeshPoly(Vector3 p, long poly) => MeshQuery.ClosestPointOnPoly(poly, p.SystemToRecast(), out var closest, out _).Succeeded() ? closest.RecastToSystem() : null;

    public Vector3? FindNearestPointOnMesh(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5) => FindNearestPointOnMeshPoly(p, FindNearestMeshPoly(p, halfExtentXZ, halfExtentY));

    // finds the point on the mesh within specified x/z tolerance and with largest Y that is still smaller than p.Y
    public Vector3? FindPointOnFloor(Vector3 p, float halfExtentXZ = 5)
    {
        IEnumerable<long> polys = FindIntersectingMeshPolys(p, new(halfExtentXZ, 2048, halfExtentXZ));
        return polys.Select(poly => FindNearestPointOnMeshPoly(p, poly)).Where(pt => pt != null && pt.Value.Y <= p.Y).MaxBy(pt => pt!.Value.Y);
    }

    // returns VoxelMap.InvalidVoxel if not found, otherwise voxel index
    public ulong FindNearestVolumeVoxel(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5) => VolumeQuery != null ? VoxelSearch.FindNearestEmptyVoxel(VolumeQuery.Volume, p, new(halfExtentXZ, halfExtentY, halfExtentXZ)) : VoxelMap.InvalidVoxel;

    // collect all mesh polygons reachable from specified polygon
    public HashSet<long> FindReachableMeshPolys(long starting)
    {
        HashSet<long> result = [];
        if (starting == 0)
            return result;

        List<long> queue = [starting];
        while (queue.Count > 0)
        {
            var next = queue[^1];
            queue.RemoveAt(queue.Count - 1);

            if (!result.Add(next))
                continue; // already visited

            MeshQuery.GetAttachedNavMesh().GetTileAndPolyByRefUnsafe(next, out var nextTile, out var nextPoly);
            for (int i = nextTile.polyLinks[nextPoly.index]; i != DtNavMesh.DT_NULL_LINK; i = nextTile.links[i].next)
            {
                long neighbourRef = nextTile.links[i].refs;
                if (neighbourRef != 0)
                    queue.Add(neighbourRef);
            }
        }

        return result;
    }
}
