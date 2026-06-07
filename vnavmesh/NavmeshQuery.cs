using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Navmesh;

public class NavmeshQuery
{
	public readonly record struct MeshPathfindPhaseResult(
		bool Success,
		int Waypoints,
		int PolyPathCount,
		int StraightPathCount,
		long TotalTicks,
		long NearestTicks,
		long FindPathTicks,
		long StraightPathTicks,
		long WaypointTicks,
		long AllocBytes,
		long NearestAllocBytes,
		long FindPathAllocBytes,
		long StraightPathAllocBytes,
		long WaypointAllocBytes,
		int FindPathPoppedNodes,
		int FindPathTouchedNodes,
		int FindPathScannedEdges,
		int FindPathPassedFilterEdges,
		int FindPathSkippedParentEdges,
		int FindPathSkippedSameParentEdges,
		int FindPathSkippedOpenWorse,
		int FindPathSkippedClosedWorse,
		int FindPathPortalCalls,
		int FindPathCostCalls,
		int FindPathHeuristicCalls,
		int FindPathHeapPushes,
		int FindPathHeapPops,
		int FindPathHeapModifies,
		int FindPathMaxOpenList,
		float FindPathFinalCost,
		string Error);

	private class IntersectQuery : IDtPolyQuery
	{
		public readonly List<long> Result = [];
		public void Process(DtMeshTile tile, DtPoly poly, long refs) => Result.Add(refs);
	}

	public class GoalRadiusHeuristic(float tolerance) : IDtQueryHeuristic
	{
		public float Scale = 1;

		float IDtQueryHeuristic.GetCost(RcVec3f neighbourPos, RcVec3f endPos)
		{
			var dist = RcVec3f.Distance(neighbourPos, endPos);
			return dist * DtDefaultQueryHeuristic.H_SCALE < tolerance ? -1 : dist * DtDefaultQueryHeuristic.H_SCALE * Scale;
		}
	}

	public class ScaledQueryHeuristic(float scale) : IDtQueryHeuristic
	{
		public float Scale = scale;

		float IDtQueryHeuristic.GetCost(RcVec3f neighbourPos, RcVec3f endPos)
		{
			return RcVec3f.Distance(neighbourPos, endPos) * DtDefaultQueryHeuristic.H_SCALE * Scale;
		}
	}

	public class TeleportAwareFilter : IDtQueryFilter
	{
		public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
		{
			var cst = RcVec3f.Distance(pa, pb);

			var costMulti = 10f;

			var curArea = curPoly.GetArea();
			var nextArea = nextPoly?.GetArea() ?? 0;

			if ((curArea ^ nextArea) == (int)Navmesh.AreaId.Endpoint)
				costMulti = (Navmesh.AreaId)curArea switch
				{
					Navmesh.AreaId.Warp => 1,
					Navmesh.AreaId.ClientPath => 3,
					Navmesh.AreaId.Shortcut => 8,
					_ => 10
				};

			return cst * costMulti;
		}


		public virtual bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) => true;
	}

	public class FloodFillAwareFilter : TeleportAwareFilter
	{
		public override bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
		{
			return (poly.flags & Navmesh.FLAG_UNREACHABLE) == 0;
		}
	}

	private readonly record struct MeshPathCacheKey(long StartRef, long EndRef, int HeuristicScaleBits);
	private readonly record struct NearestMeshPolyCacheKey(int X, int Y, int Z, int HalfExtentXz, int HalfExtentY, bool AllowUnreachable);

	private sealed class MeshPathCacheEntry(MeshPathCacheKey key, List<long> path)
	{
		public readonly MeshPathCacheKey Key = key;
		public readonly List<long> Path = path;
	}

	private sealed class NearestMeshPolyCacheEntry(NearestMeshPolyCacheKey key, long poly)
	{
		public readonly NearestMeshPolyCacheKey Key = key;
		public long Poly = poly;
	}

	public DtNavMeshQuery MeshQuery;
	public VoxelPathfind? VolumeQuery;
	private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
	private readonly IDtQueryFilter _pathFilter = new TeleportAwareFilter();
	private readonly IDtQueryFilter _reachableFilter = new FloodFillAwareFilter();
	private readonly ScaledQueryHeuristic _pathHeuristic = new(10);
	private readonly Dictionary<MeshPathCacheKey, LinkedListNode<MeshPathCacheEntry>> _meshPathCache = [];
	private readonly LinkedList<MeshPathCacheEntry> _meshPathCacheLru = [];
	private readonly Dictionary<NearestMeshPolyCacheKey, LinkedListNode<NearestMeshPolyCacheEntry>> _nearestMeshPolyCache = [];
	private readonly LinkedList<NearestMeshPolyCacheEntry> _nearestMeshPolyCacheLru = [];

	public List<long> LastPath => _lastPath;
	private List<long> _lastPath = [];
	private List<DtStraightPath> _straightPath = [];
	private float _meshHeuristicScale = 10;
	private int _meshPathCacheSize = 2048;
	private int _nearestMeshPolyCacheSize = 4096;

	public float MeshHeuristicScale
	{
		get => _meshHeuristicScale;
		set
		{
			_meshHeuristicScale = Math.Max(0.01f, value);
			_pathHeuristic.Scale = _meshHeuristicScale;
		}
	}

	public int MeshPathCacheSize
	{
		get => _meshPathCacheSize;
		set
		{
			_meshPathCacheSize = Math.Max(0, value);
			TrimMeshPathCache();
		}
	}

	public int NearestMeshPolyCacheSize
	{
		get => _nearestMeshPolyCacheSize;
		set
		{
			_nearestMeshPolyCacheSize = Math.Max(0, value);
			TrimNearestMeshPolyCache();
		}
	}

	public bool LastMeshPathCacheHit { get; private set; }
	public long MeshPathCacheHits { get; private set; }
	public long MeshPathCacheMisses { get; private set; }
	public long NearestMeshPolyCacheHits { get; private set; }
	public long NearestMeshPolyCacheMisses { get; private set; }

	public NavmeshQuery(Navmesh navmesh)
	{
		MeshQuery = new(navmesh.Mesh/*, s => Service.Log.Debug(s)*/);
		if (navmesh.Volume != null)
			VolumeQuery = new(navmesh.Volume);
	}

	public List<Waypoint> PathfindMesh(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, float range, CancellationToken cancel)
	{
		var startPos = from.SystemToRecast();
		var targetPos = to.SystemToRecast();
		var startRef = FindNearestMeshPoly(startPos);
		var endRef = FindNearestMeshPoly(targetPos);
		if (NavmeshDiagnostics.IsDebugEnabled)
			NavmeshDiagnostics.Debug($"[pathfind] poly {startRef:X} -> {endRef:X}");
		if (startRef == 0 || endRef == 0)
		{
			NavmeshDiagnostics.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find polygon on a mesh");
			return [];
		}

		var debugEnabled = NavmeshDiagnostics.IsDebugEnabled;
		var timer = debugEnabled ? Timer.Create() : default;
		_lastPath.Clear();
		LastMeshPathCacheHit = false;
		var canUsePathCache = !useRaycast && range <= 0 && MeshPathCacheSize > 0;
		if (canUsePathCache && TryGetCachedMeshPath(startRef, endRef, _lastPath))
		{
			LastMeshPathCacheHit = true;
			MeshPathCacheHits++;
		}
		else
		{
			if (canUsePathCache)
				MeshPathCacheMisses++;
			IDtQueryHeuristic heuristic = range > 0 ? new GoalRadiusHeuristic(range) { Scale = MeshHeuristicScale } : _pathHeuristic;
			var opt = new DtFindPathOption(heuristic, useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? float.MaxValue : 0);
			MeshQuery.FindPath(startRef, endRef, startPos, targetPos, _pathFilter, ref _lastPath, opt);
			if (canUsePathCache && _lastPath.Count > 0)
				StoreCachedMeshPath(startRef, endRef, _lastPath);
		}
		if (_lastPath.Count == 0)
		{
			NavmeshDiagnostics.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find path on mesh");
			return [];
		}
		if (debugEnabled)
			NavmeshDiagnostics.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", _lastPath.Select(r => r.ToString("X")))}");

		// In case of partial path, make sure the end point is clamped to the last polygon.
		var endPos = targetPos;
		//if (polysPath.Last() != endRef)
		//    if (MeshQuery.ClosestPointOnPoly(polysPath.Last(), endPos, out var closest, out _).Succeeded())
		//        endPos = closest;

		cancel.ThrowIfCancellationRequested();

		if (useStringPulling)
		{
			var success = MeshQuery.FindStraightPath(startPos, endPos, _lastPath, ref _straightPath, 1024, 0);
			if (success.Failed())
				NavmeshDiagnostics.Error($"Failed to find a path from {from} ({startRef:X}) to {to} ({endRef:X}): failed to find straight path ({success.Value:X})");

			var navmesh = MeshQuery.GetAttachedNavMesh();
			var res = new List<Waypoint>(_straightPath.Count + 1);
			foreach (var p in _straightPath)
				res.Add(new(p.pos.RecastToSystem(), GetAreaId(navmesh, p.refs)));

			res.Add(new(endPos.RecastToSystem()));
			return res;
		}
		else
		{
			var navmesh = MeshQuery.GetAttachedNavMesh();
			var res = new List<Waypoint>(_lastPath.Count + 1);
			foreach (var r in _lastPath)
				res.Add(new(navmesh.GetPolyCenter(r).RecastToSystem(), GetAreaId(navmesh, r)));

			res.Add(new(endPos.RecastToSystem()));
			return res;
		}
	}

	public MeshPathfindPhaseResult BenchmarkPathfindMeshPhases(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, float range, CancellationToken cancel)
	{
		var allocStart = GC.GetAllocatedBytesForCurrentThread();
		var totalStart = Stopwatch.GetTimestamp();

		var nearestAllocStart = GC.GetAllocatedBytesForCurrentThread();
		var nearestStart = Stopwatch.GetTimestamp();
		var startPos = from.SystemToRecast();
		var targetPos = to.SystemToRecast();
		var startRef = FindNearestMeshPoly(startPos);
		var endRef = FindNearestMeshPoly(targetPos);
		var nearestTicks = Stopwatch.GetTimestamp() - nearestStart;
		var nearestAlloc = GC.GetAllocatedBytesForCurrentThread() - nearestAllocStart;

		if (startRef == 0 || endRef == 0)
			return phaseResult(false, 0, 0, 0, nearestTicks, 0, 0, 0, nearestAlloc, 0, 0, 0, null, $"failed to find polygon on a mesh ({startRef:X} -> {endRef:X})");

		_lastPath.Clear();
		IDtQueryHeuristic heuristic = range > 0 ? new GoalRadiusHeuristic(range) { Scale = MeshHeuristicScale } : _pathHeuristic;
		var opt = new DtFindPathOption(heuristic, useRaycast ? DtFindPathOptions.DT_FINDPATH_ANY_ANGLE : 0, useRaycast ? float.MaxValue : 0);

		var findPathAllocStart = GC.GetAllocatedBytesForCurrentThread();
		var findPathStart = Stopwatch.GetTimestamp();
		MeshQuery.CollectFindPathStats = true;
		MeshQuery.FindPath(startRef, endRef, startPos, targetPos, _pathFilter, ref _lastPath, opt);
		MeshQuery.CollectFindPathStats = false;
		var findPathStats = MeshQuery.LastFindPathStats;
		var findPathTicks = Stopwatch.GetTimestamp() - findPathStart;
		var findPathAlloc = GC.GetAllocatedBytesForCurrentThread() - findPathAllocStart;

		if (_lastPath.Count == 0)
			return phaseResult(false, 0, 0, 0, nearestTicks, findPathTicks, 0, 0, nearestAlloc, findPathAlloc, 0, 0, findPathStats, "failed to find path on mesh");

		cancel.ThrowIfCancellationRequested();

		var endPos = targetPos;
		long straightTicks = 0;
		long waypointTicks;
		long straightAlloc = 0;
		long waypointAlloc;
		int waypoints;
		int straightPoints = 0;

		if (useStringPulling)
		{
			var straightAllocStart = GC.GetAllocatedBytesForCurrentThread();
			var straightStart = Stopwatch.GetTimestamp();
			var success = MeshQuery.FindStraightPath(startPos, endPos, _lastPath, ref _straightPath, 1024, 0);
			straightTicks = Stopwatch.GetTimestamp() - straightStart;
			straightAlloc = GC.GetAllocatedBytesForCurrentThread() - straightAllocStart;
			straightPoints = _straightPath.Count;

			if (success.Failed())
				return phaseResult(false, 0, _lastPath.Count, straightPoints, nearestTicks, findPathTicks, straightTicks, 0, nearestAlloc, findPathAlloc, straightAlloc, 0, findPathStats, $"failed to find straight path ({success.Value:X})");

			var waypointAllocStart = GC.GetAllocatedBytesForCurrentThread();
			var waypointStart = Stopwatch.GetTimestamp();
			var navmesh = MeshQuery.GetAttachedNavMesh();
			var res = new List<Waypoint>(_straightPath.Count + 1);
			foreach (var p in _straightPath)
				res.Add(new(p.pos.RecastToSystem(), GetAreaId(navmesh, p.refs)));
			res.Add(new(endPos.RecastToSystem()));
			waypoints = res.Count;
			waypointTicks = Stopwatch.GetTimestamp() - waypointStart;
			waypointAlloc = GC.GetAllocatedBytesForCurrentThread() - waypointAllocStart;
		}
		else
		{
			var waypointAllocStart = GC.GetAllocatedBytesForCurrentThread();
			var waypointStart = Stopwatch.GetTimestamp();
			var navmesh = MeshQuery.GetAttachedNavMesh();
			var res = new List<Waypoint>(_lastPath.Count + 1);
			foreach (var r in _lastPath)
				res.Add(new(navmesh.GetPolyCenter(r).RecastToSystem(), GetAreaId(navmesh, r)));
			res.Add(new(endPos.RecastToSystem()));
			waypoints = res.Count;
			waypointTicks = Stopwatch.GetTimestamp() - waypointStart;
			waypointAlloc = GC.GetAllocatedBytesForCurrentThread() - waypointAllocStart;
		}

		return phaseResult(true, waypoints, _lastPath.Count, straightPoints, nearestTicks, findPathTicks, straightTicks, waypointTicks, nearestAlloc, findPathAlloc, straightAlloc, waypointAlloc, findPathStats, "");

		MeshPathfindPhaseResult phaseResult(bool success, int resultWaypoints, int polyPathCount, int straightPathCount, long nearest, long findPath, long straight, long waypoint, long nearestAllocBytes, long findPathAllocBytes, long straightAllocBytes, long waypointAllocBytes, DtFindPathStats? pathStats, string error)
		{
			var totalTicks = Stopwatch.GetTimestamp() - totalStart;
			var allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocStart;
			return new(
				success,
				resultWaypoints,
				polyPathCount,
				straightPathCount,
				totalTicks,
				nearest,
				findPath,
				straight,
				waypoint,
				allocBytes,
				nearestAllocBytes,
				findPathAllocBytes,
				straightAllocBytes,
				waypointAllocBytes,
				pathStats?.PoppedNodes ?? 0,
				pathStats?.TouchedNodes ?? 0,
				pathStats?.ScannedEdges ?? 0,
				pathStats?.PassedFilterEdges ?? 0,
				pathStats?.SkippedParentEdges ?? 0,
				pathStats?.SkippedSameParentEdges ?? 0,
				pathStats?.SkippedOpenWorse ?? 0,
				pathStats?.SkippedClosedWorse ?? 0,
				pathStats?.PortalCalls ?? 0,
				pathStats?.CostCalls ?? 0,
				pathStats?.HeuristicCalls ?? 0,
				pathStats?.HeapPushes ?? 0,
				pathStats?.HeapPops ?? 0,
				pathStats?.HeapModifies ?? 0,
				pathStats?.MaxOpenList ?? 0,
				pathStats?.FinalCost ?? 0,
				error);
		}
	}

	private MeshPathCacheKey MakeMeshPathCacheKey(long startRef, long endRef)
	{
		return new(startRef, endRef, BitConverter.SingleToInt32Bits(MeshHeuristicScale));
	}

	private bool TryGetCachedMeshPath(long startRef, long endRef, List<long> path)
	{
		var key = MakeMeshPathCacheKey(startRef, endRef);
		if (!_meshPathCache.TryGetValue(key, out var node))
			return false;

		_meshPathCacheLru.Remove(node);
		_meshPathCacheLru.AddFirst(node);
		path.AddRange(node.Value.Path);
		return true;
	}

	private void StoreCachedMeshPath(long startRef, long endRef, List<long> path)
	{
		var key = MakeMeshPathCacheKey(startRef, endRef);
		if (_meshPathCache.TryGetValue(key, out var existing))
		{
			existing.Value.Path.Clear();
			existing.Value.Path.AddRange(path);
			_meshPathCacheLru.Remove(existing);
			_meshPathCacheLru.AddFirst(existing);
			return;
		}

		var entry = new MeshPathCacheEntry(key, [.. path]);
		var node = new LinkedListNode<MeshPathCacheEntry>(entry);
		_meshPathCache.Add(key, node);
		_meshPathCacheLru.AddFirst(node);
		TrimMeshPathCache();
	}

	private void TrimMeshPathCache()
	{
		while (_meshPathCache.Count > MeshPathCacheSize)
		{
			var node = _meshPathCacheLru.Last;
			if (node == null)
				break;

			_meshPathCacheLru.RemoveLast();
			_meshPathCache.Remove(node.Value.Key);
		}
	}

	private static NearestMeshPolyCacheKey MakeNearestMeshPolyCacheKey(RcVec3f p, float halfExtentXZ, float halfExtentY, bool allowUnreachable)
	{
		return new(
			BitConverter.SingleToInt32Bits(p.X),
			BitConverter.SingleToInt32Bits(p.Y),
			BitConverter.SingleToInt32Bits(p.Z),
			BitConverter.SingleToInt32Bits(halfExtentXZ),
			BitConverter.SingleToInt32Bits(halfExtentY),
			allowUnreachable);
	}

	private bool TryGetCachedNearestMeshPoly(RcVec3f p, float halfExtentXZ, float halfExtentY, bool allowUnreachable, out long poly)
	{
		var key = MakeNearestMeshPolyCacheKey(p, halfExtentXZ, halfExtentY, allowUnreachable);
		if (!_nearestMeshPolyCache.TryGetValue(key, out var node))
		{
			poly = 0;
			return false;
		}

		_nearestMeshPolyCacheLru.Remove(node);
		_nearestMeshPolyCacheLru.AddFirst(node);
		poly = node.Value.Poly;
		return true;
	}

	private void StoreCachedNearestMeshPoly(RcVec3f p, float halfExtentXZ, float halfExtentY, bool allowUnreachable, long poly)
	{
		var key = MakeNearestMeshPolyCacheKey(p, halfExtentXZ, halfExtentY, allowUnreachable);
		if (_nearestMeshPolyCache.TryGetValue(key, out var existing))
		{
			existing.Value.Poly = poly;
			_nearestMeshPolyCacheLru.Remove(existing);
			_nearestMeshPolyCacheLru.AddFirst(existing);
			return;
		}

		var entry = new NearestMeshPolyCacheEntry(key, poly);
		var node = new LinkedListNode<NearestMeshPolyCacheEntry>(entry);
		_nearestMeshPolyCache.Add(key, node);
		_nearestMeshPolyCacheLru.AddFirst(node);
		TrimNearestMeshPolyCache();
	}

	private void TrimNearestMeshPolyCache()
	{
		while (_nearestMeshPolyCache.Count > NearestMeshPolyCacheSize)
		{
			var node = _nearestMeshPolyCacheLru.Last;
			if (node == null)
				break;

			_nearestMeshPolyCacheLru.RemoveLast();
			_nearestMeshPolyCache.Remove(node.Value.Key);
		}
	}

	private static Navmesh.AreaId GetAreaId(DtNavMesh navmesh, long refs)
	{
		navmesh.GetPolyArea(refs, out var area);
		return (Navmesh.AreaId)area;
	}

	public List<Waypoint> PathfindVolume(Vector3 from, Vector3 to, bool useRaycast, bool useStringPulling, CancellationToken cancel)
	{
		if (VolumeQuery == null)
		{
			NavmeshDiagnostics.Error($"Nav volume was not built");
			return [];
		}

		var startVoxel = FindNearestVolumeVoxel(from);
		var endVoxel = FindNearestVolumeVoxel(to);
		if (NavmeshDiagnostics.IsDebugEnabled)
			NavmeshDiagnostics.Debug($"[pathfind] voxel {startVoxel:X} -> {endVoxel:X}");
		if (startVoxel == VoxelMap.InvalidVoxel || endVoxel == VoxelMap.InvalidVoxel)
		{
			NavmeshDiagnostics.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find empty voxel");
			return [];
		}

		var debugEnabled = NavmeshDiagnostics.IsDebugEnabled;
		var timer = debugEnabled ? Timer.Create() : default;
		var voxelPath = VolumeQuery.FindPath(startVoxel, endVoxel, from, to, useRaycast, false, cancel); // TODO: do we need intermediate points for string-pulling algo?
		if (voxelPath.Count == 0)
		{
			NavmeshDiagnostics.Error($"Failed to find a path from {from} ({startVoxel:X}) to {to} ({endVoxel:X}): failed to find path on volume");
			return [];
		}
		if (debugEnabled)
			NavmeshDiagnostics.Debug($"Pathfind took {timer.Value().TotalSeconds:f3}s: {string.Join(", ", voxelPath.Select(r => $"{r.p} {r.voxel:X}"))}");

		// TODO: string-pulling support
		var res = new List<Waypoint>(voxelPath.Count + 1);
		foreach (var r in voxelPath)
			res.Add(new(r.p));

		res.Add(new(to));
		return res;
	}

	// returns 0 if not found, otherwise polygon ref
	public long FindNearestMeshPoly(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5, bool allowUnreachable = true)
	{
		return FindNearestMeshPoly(p.SystemToRecast(), halfExtentXZ, halfExtentY, allowUnreachable);
	}

	private long FindNearestMeshPoly(RcVec3f p, float halfExtentXZ = 5, float halfExtentY = 5, bool allowUnreachable = true)
	{
		if (NearestMeshPolyCacheSize > 0 && TryGetCachedNearestMeshPoly(p, halfExtentXZ, halfExtentY, allowUnreachable, out var cachedRef))
		{
			NearestMeshPolyCacheHits++;
			return cachedRef;
		}

		if (NearestMeshPolyCacheSize > 0)
			NearestMeshPolyCacheMisses++;

		MeshQuery.FindNearestPoly(p, new(halfExtentXZ, halfExtentY, halfExtentXZ), allowUnreachable ? _filter : _reachableFilter, out var nearestRef, out _, out _);
		if (NearestMeshPolyCacheSize > 0 && nearestRef != 0)
			StoreCachedNearestMeshPoly(p, halfExtentXZ, halfExtentY, allowUnreachable, nearestRef);
		return nearestRef;
	}

	public List<long> FindIntersectingMeshPolys(Vector3 p, Vector3 halfExtent, bool allowUnreachable = true)
	{
		IntersectQuery query = new();
		MeshQuery.QueryPolygons(p.SystemToRecast(), halfExtent.SystemToRecast(), allowUnreachable ? _filter : _reachableFilter, query);
		return query.Result;
	}

	public Vector3? FindNearestPointOnMeshPoly(Vector3 p, long poly) => MeshQuery.ClosestPointOnPoly(poly, p.SystemToRecast(), out var closest, out _).Succeeded() ? closest.RecastToSystem() : null;

	public Vector3? FindNearestPointOnMesh(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5, bool allowUnreachable = true) => FindNearestPointOnMeshPoly(p, FindNearestMeshPoly(p, halfExtentXZ, halfExtentY, allowUnreachable));

	// finds the point on the mesh within specified x/z tolerance and with largest Y that is still smaller than p.Y
	public Vector3? FindPointOnFloor(Vector3 p, float halfExtentXZ = 5, bool allowUnreachable = true)
	{
		IEnumerable<long> polys = FindIntersectingMeshPolys(p, new(halfExtentXZ, 2048, halfExtentXZ), allowUnreachable);
		return polys.Select(poly => FindNearestPointOnMeshPoly(p, poly)).Where(pt => pt != null && pt.Value.Y <= p.Y).MaxBy(pt => pt!.Value.Y);
	}

	// returns VoxelMap.InvalidVoxel if not found, otherwise voxel index
	public ulong FindNearestVolumeVoxel(Vector3 p, float halfExtentXZ = 5, float halfExtentY = 5) => VolumeQuery != null ? VoxelSearch.FindNearestEmptyVoxel(VolumeQuery.Volume, p, new(halfExtentXZ, halfExtentY, halfExtentXZ)) : VoxelMap.InvalidVoxel;

	// collect all mesh polygons reachable from specified polygon
	public HashSet<long> FindReachableMeshPolys(params long[] starting)
	{
		HashSet<long> result = [];

		List<long> queue = [.. starting];
		queue.RemoveAll(s => s == 0);

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
