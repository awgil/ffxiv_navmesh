using Dalamud.Memory;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset.Builder;
using DotRecast.Recast.Toolset.Tools;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh;

// async navmesh builder; TODO reconsider all this stuff...
public class NavmeshBuilder : IDisposable
{
    public enum State { NotBuilt, InProgress, Failed, Ready }

    // config
    public RcPartition Partitioning = RcPartition.WATERSHED;
    public float CellSize = 0.3f;
    public float CellHeight = 0.2f;
    public float AgentMaxSlopeDeg = 45f;
    public float AgentHeight = 2.0f;
    public float AgentMaxClimb = 0.9f;
    public float AgentRadius = 0.6f;
    public float EdgeMaxLen = 12f;
    public float EdgeMaxError = 1.3f;
    public int MinRegionSize = 8;
    public int MergedRegionSize = 20;
    public int VertsPerPoly = 6;
    public float DetailSampleDist = 6f;
    public float DetailSampleMaxError = 1f;
    public bool FilterLowHangingObstacles = true;
    public bool FilterLedgeSpans = true;
    public bool FilterWalkableLowHeightSpans = true;

    // result
    private RcBuilderResult? _intermediates; // valid only when task is not running
    private Task<DtNavMesh>? _navmesh;

    public State CurrentState => _navmesh == null ? State.NotBuilt : !_navmesh.IsCompleted ? State.InProgress : _navmesh.IsFaulted ? State.Failed : State.Ready;
    public RcBuilderResult? Intermediates => _intermediates;
    public DtNavMesh? Navmesh => _navmesh != null && _navmesh.IsCompletedSuccessfully ? _navmesh.Result : null;

    public void Dispose()
    {
        // i really don't want to join here...
        //_navmesh?.Dispose();
    }

    public void Rebuild()
    {
        _intermediates = null;
        // TODO: extract minimal collision scene meshes before spawning async task, otherwise we have a race...
        _navmesh = Task.Run(BuildNavmesh);
    }

    public List<Vector3> Pathfind(Vector3 from, Vector3 to)
    {
        var res = new List<Vector3>();
        var navmesh = Navmesh;
        if (navmesh != null)
        {
            var tool = new RcTestNavMeshTool();
            var query = new DtNavMeshQuery(navmesh);
            DtQueryDefaultFilter filter = new DtQueryDefaultFilter();
            var success = query.FindNearestPoly(new(from.X, from.Y, from.Z), new(2, 2, 2), filter, out var startRef, out _, out _);
            Service.Log.Debug($"[pathfind] findsrc={success.Value:X} {startRef}");
            success = query.FindNearestPoly(new(to.X, to.Y, to.Z), new(2, 2, 2), filter, out var endRef, out _, out _);
            Service.Log.Debug($"[pathfind] finddst={success.Value:X} {endRef}");
            List<long> polys = new();
            List<RcVec3f> smooth = new();
            success = tool.FindFollowPath(navmesh, query, startRef, endRef, from.SystemToRecast(), to.SystemToRecast(), filter, true, ref polys, ref smooth);
            Service.Log.Debug($"[pathfind] findpath={success.Value:X}");
            if (success.Succeeded())
                res.AddRange(smooth.Select(v => new Vector3(v.X, v.Y, v.Z)));
        }
        return res;
    }

    private class DummyProvider : IInputGeomProvider
    {
        public void AddConvexVolume(RcConvexVolume convexVolume) { }
        public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) { }
        public IList<RcConvexVolume> ConvexVolumes() => [];
        public RcTriMesh GetMesh() => new([], []);
        public RcVec3f GetMeshBoundsMax() => default;
        public RcVec3f GetMeshBoundsMin() => default;
        public List<RcOffMeshConnection> GetOffMeshConnections() => [];
        public IEnumerable<RcTriMesh> Meshes() => Enumerable.Empty<RcTriMesh>();
        public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) { }
    }

    private DtNavMesh BuildNavmesh()
    {
        try
        {
            Service.Log.Debug("[navmesh] start");
            var telemetry = new RcTelemetry();

            // init config
            var cfg = new RcConfig(
                Partitioning,
                CellSize, CellHeight,
                AgentMaxSlopeDeg, AgentHeight, AgentRadius, AgentMaxClimb,
                MinRegionSize, MergedRegionSize,
                EdgeMaxLen, EdgeMaxError,
                VertsPerPoly,
                DetailSampleDist, DetailSampleMaxError,
                FilterLowHangingObstacles, FilterLedgeSpans, FilterWalkableLowHeightSpans,
                new(RcConstants.RC_WALKABLE_AREA), true);

            Service.Log.Debug("[navmesh] find set of colliders");
            var colliders = new Colliders(true, true);

            // 1. voxelize raw geometry
            // this creates a 'solid heightfield', which is a grid of sorted linked lists of spans
            // each span contains an 'area id', which is either walkable (if normal is good) or not (otherwise); areas outside spans contains no geometry at all
            Service.Log.Debug("[navmesh] rasterize input polygon soup to heightfield");
            var solid = new NavmeshRasterizer(cfg, colliders.BoundsMin, colliders.BoundsMax, telemetry);
            colliders.Rasterize(solid);

            // 2. perform a bunch of postprocessing on a heightfield
            if (cfg.FilterLowHangingObstacles)
            {
                Service.Log.Debug("[navmesh] filter low-hanging obstacles");
                RcFilters.FilterLowHangingWalkableObstacles(telemetry, cfg.WalkableClimb, solid.Heightfield);
            }

            if (cfg.FilterLedgeSpans)
            {
                Service.Log.Debug("[navmesh] filter ledge spans");
                RcFilters.FilterLedgeSpans(telemetry, cfg.WalkableHeight, cfg.WalkableClimb, solid.Heightfield);
            }

            if (cfg.FilterWalkableLowHeightSpans)
            {
                Service.Log.Debug("[navmesh] filter walkable low-height spans");
                RcFilters.FilterWalkableLowHeightSpans(telemetry, cfg.WalkableHeight, solid.Heightfield);
            }

            // 3. create a 'compact heightfield' structure
            // this is very similar to a normal heightfield, except that spans are now stored in a single array, and grid cells just contain consecutive ranges
            // this also contains connectivity data (links to neighbouring cells)
            Service.Log.Debug("[navmesh] build compact heightfield with connectivity data");
            RcCompactHeightfield chf = RcCompacts.BuildCompactHeightfield(telemetry, cfg.WalkableHeight, cfg.WalkableClimb, solid.Heightfield);

            // 4. mark spans that are too close to unwalkable as unwalkable, to account for actor's non-zero radius
            // this changes area of some spans from walkable to non-walkable
            Service.Log.Debug("[navmesh] erode the walkable area by agent radius");
            RcAreas.ErodeWalkableArea(telemetry, cfg.WalkableRadius, chf);
            // note: this is the good time to mark convex poly areas with custom area ids

            // 5. build connected regions; this assigns region ids to spans in the compact heightfield
            // there are different algorithms with different tradeoffs
            Service.Log.Debug("[navmesh] partitioning heightfield");
            if (cfg.Partition == RcPartitionType.WATERSHED.Value)
            {
                RcRegions.BuildDistanceField(telemetry, chf);
                RcRegions.BuildRegions(telemetry, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
            }
            else if (cfg.Partition == RcPartitionType.MONOTONE.Value)
            {
                RcRegions.BuildRegionsMonotone(telemetry, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
            }
            else
            {
                RcRegions.BuildLayerRegions(telemetry, chf, cfg.MinRegionArea);
            }

            // 6. build contours around regions, then simplify them to reduce vertex count
            // contour set is just a list of contours, each of which is a simple non-convex polygon that belong to a single region with a single area id
            Service.Log.Debug("[navmesh] tracing and simplyfing contours");
            RcContourSet cset = RcContours.BuildContours(telemetry, chf, cfg.MaxSimplificationError, cfg.MaxEdgeLen, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);

            // 7. triangulate contours to build a mesh of convex polygons with adjacency information
            Service.Log.Debug("[navmesh] building polygon mesh from contours");
            RcPolyMesh pmesh = RcMeshs.BuildPolyMesh(telemetry, cset, cfg.MaxVertsPerPoly);

            // 8. not sure about this, apparently this is needed to provide height information?..
            Service.Log.Debug("[navmesh] creating detail mesh");
            RcPolyMeshDetail? dmesh = cfg.BuildMeshDetail ? RcMeshDetails.BuildPolyMeshDetail(telemetry, pmesh, chf, cfg.DetailSampleDist, cfg.DetailSampleMaxError) : null;
            var rcResult = new RcBuilderResult(0, 0, solid.Heightfield, chf, cset, pmesh, dmesh, telemetry);

            Service.Log.Debug("[navmesh] detour navmesh build");
            DtNavMeshCreateParams navmeshConfig = DemoNavMeshBuilder.GetNavMeshCreateParams(new DummyProvider(), CellSize, CellHeight, AgentHeight, AgentRadius, AgentMaxClimb, rcResult);
            var navmeshData = DtNavMeshBuilder.CreateNavMeshData(navmeshConfig);
            if (navmeshData == null)
                throw new Exception("Failed to create DtMeshData");
            DemoNavMeshBuilder.UpdateAreaAndFlags(navmeshData);

            var navmesh = new DtNavMesh(navmeshData, VertsPerPoly, 0);

            Service.Log.Debug("[navmesh] end");
            _intermediates = rcResult;
            return navmesh;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error building navmesh: {ex}");
            throw;
        }
    }

    private unsafe class Colliders
    {
        public List<Pointer<ColliderStreamed>> Streamed = new();
        public List<Pointer<ColliderMesh>> Meshes = new();
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;

        public Colliders(bool includeStreamedMeshes, bool includeStandaloneMeshes)
        {
            BoundsMin = new(float.MaxValue);
            BoundsMax = new(float.MinValue);

            // first pass - mark streamed meshes (so that we can ignore them on standalone mesh pass) and add streamable
            HashSet<nint> streamedMeshes = new();
            foreach (var s in FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
            {
                foreach (var coll in s->Scene->Colliders)
                {
                    if (coll->GetColliderType() != ColliderType.Streamed)
                        continue;
                    var cast = (ColliderStreamed*)coll;
                    if (cast->Header == null)
                        continue;
                    if (includeStreamedMeshes)
                    {
                        Streamed.Add(cast);
                        AddBounds(ref cast->Header->Bounds);
                    }
                    if (includeStandaloneMeshes && cast->Elements != null)
                        foreach (ref var e in new Span<ColliderStreamed.Element>(cast->Elements, cast->Header->NumMeshes))
                            if (e.Mesh != null)
                                streamedMeshes.Add((nint)e.Mesh);
                }
            }

            // second pass - add standalone meshes
            if (includeStandaloneMeshes)
            {
                foreach (var s in FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
                {
                    foreach (var coll in s->Scene->Colliders)
                    {
                        if (coll->GetColliderType() == ColliderType.Mesh && !streamedMeshes.Contains((nint)coll))
                        {
                            var cast = (ColliderMesh*)coll;
                            Meshes.Add(cast);
                            AddBounds(ref cast->WorldBoundingBox);
                        }
                    }
                }
            }
        }

        public void Rasterize(NavmeshRasterizer rasterizer)
        {
            foreach (var coll in Streamed)
            {
                if (coll.Value->Header == null || coll.Value->Elements == null)
                    continue;
                var basePath = MemoryHelper.ReadStringNullTerminated((nint)coll.Value->PathBase);
                foreach (ref var e in new Span<ColliderStreamed.Element>(coll.Value->Elements, coll.Value->Header->NumMeshes))
                {
                    var f = Service.DataManager.GetFile($"{basePath}/tr{e.MeshId:d4}.pcb");
                    if (f != null)
                    {
                        fixed (byte* rawData = &f.Data[0])
                        {
                            var data = (MeshPCB.FileHeader*)rawData;
                            if (data->Version is 1 or 4)
                            {
                                rasterizer.RasterizePCB((MeshPCB.FileNode*)(data + 1), null);
                            }
                        }
                    }
                }
            }

            foreach (var coll in Meshes)
            {
                if (!coll.Value->MeshIsSimple && coll.Value->Mesh != null)
                {
                    var mesh = (MeshPCB*)coll.Value->Mesh;
                    rasterizer.RasterizePCB(mesh->RootNode, &coll.Value->World);
                }
            }
        }

        private void AddBounds(ref FFXIVClientStructs.FFXIV.Common.Math.AABB aabb)
        {
            BoundsMin = Vector3.Min(BoundsMin, aabb.Min);
            BoundsMax = Vector3.Max(BoundsMax, aabb.Max);
        }
    }
}
