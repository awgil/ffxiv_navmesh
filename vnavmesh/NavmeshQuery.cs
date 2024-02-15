using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast.Toolset.Tools;
using Navmesh.NavVolume;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

public class NavmeshQuery
{
    public DtNavMeshQuery MeshQuery;
    public PathfindQuery VolumeQuery;

    public NavmeshQuery(Navmesh navmesh)
    {
        MeshQuery = new(navmesh.Mesh);
        VolumeQuery = new(navmesh.Volume);
    }

    public List<Vector3> PathfindMesh(Vector3 from, Vector3 to)
    {
        var res = new List<Vector3>();
        var tool = new RcTestNavMeshTool();
        DtQueryDefaultFilter filter = new DtQueryDefaultFilter();
        var success = MeshQuery.FindNearestPoly(new(from.X, from.Y, from.Z), new(2, 2, 2), filter, out var startRef, out _, out _);
        Service.Log.Debug($"[pathfind] findsrc={success.Value:X} {startRef}");
        success = MeshQuery.FindNearestPoly(new(to.X, to.Y, to.Z), new(2, 2, 2), filter, out var endRef, out _, out _);
        Service.Log.Debug($"[pathfind] finddst={success.Value:X} {endRef}");
        List<long> polys = new();
        List<RcVec3f> smooth = new();
        success = tool.FindFollowPath(MeshQuery.GetAttachedNavMesh(), MeshQuery, startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), filter, true, ref polys, ref smooth);
        Service.Log.Debug($"[pathfind] findpath={success.Value:X}");
        if (success.Succeeded())
            res.AddRange(smooth.Select(v => new Vector3(v.X, v.Y, v.Z)));
        return res;
    }

    public List<Vector3> PathfindVolume(Vector3 from, Vector3 to)
    {
        return VolumeQuery.FindPath(from, to);
    }
}
