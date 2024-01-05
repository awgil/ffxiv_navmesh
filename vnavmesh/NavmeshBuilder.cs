using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Toolset.Tools;
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

    // results - should not be accessed while task is running
    private CollisionGeometryExtractor _extractor = new();
    private RcBuilderResult? _intermediates;
    private DtNavMesh? _navmesh;
    private DtNavMeshQuery? _query;
    private Task? _task;

    public State CurrentState => _task == null ? State.NotBuilt : !_task.IsCompleted ? State.InProgress : _task.IsFaulted ? State.Failed : State.Ready;
    public CollisionGeometryExtractor CollisionGeometry => _extractor;
    public RcBuilderResult? Intermediates => _task != null && _task.IsCompletedSuccessfully ? _intermediates : null;
    public DtNavMesh? Navmesh => _task != null && _task.IsCompletedSuccessfully ? _navmesh : null;
    public DtNavMeshQuery? Query => _task != null && _task.IsCompletedSuccessfully ? _query : null;

    public void Dispose()
    {
        // i really don't want to join here...
        //_task?.Dispose();
    }

    public void Rebuild()
    {
        if (_task != null && !_task.IsCompleted)
            _task.Wait();
        _intermediates = null;
        _navmesh = null;
        _query = null;
        _extractor.Clear();
        Service.Log.Debug("[navmesh] extract from scene");
        _extractor.FillFromGame(1); // layer 0 is where collision geometry is
        Service.Log.Debug("[navmesh] schedule async build");
        _task = Task.Run(BuildNavmesh);
    }

    public List<Vector3> Pathfind(Vector3 from, Vector3 to)
    {
        var res = new List<Vector3>();
        var navmesh = Navmesh;
        var query = Query;
        if (navmesh != null && query != null)
        {
            var tool = new RcTestNavMeshTool();
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

    private void BuildNavmesh()
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

            // 0. load all meshes
            Service.Log.Debug("[navmesh] load meshes");
            _extractor.Extract();

            // 1. voxelize raw geometry
            // this creates a 'solid heightfield', which is a grid of sorted linked lists of spans
            // each span contains an 'area id', which is either walkable (if normal is good) or not (otherwise); areas outside spans contains no geometry at all
            Service.Log.Debug("[navmesh] rasterize input polygon soup to heightfield");
            var solid = new NavmeshRasterizer(cfg, _extractor.BoundsMin, _extractor.BoundsMax, telemetry);
            solid.Rasterize(_extractor, true, true, false); // TODO: some analytic meshes are used for things like doors - their raycast flag is removed when they are opened ... :(

            // 2. perform a bunch of postprocessing on a heightfield
            if (cfg.FilterLowHangingObstacles)
            {
                // mark non-walkable spans as walkable if their maximum is within climb distance of the span below
                // this allows climbing stairs, walking over curbs, etc
                Service.Log.Debug("[navmesh] filter low-hanging obstacles");
                RcFilters.FilterLowHangingWalkableObstacles(telemetry, cfg.WalkableClimb, solid.Heightfield);
            }

            if (cfg.FilterLedgeSpans)
            {
                // mark 'ledge' spans as non-walkable - spans that have too large height distance to the neighbour
                // this reduces the impact of voxelization error
                Service.Log.Debug("[navmesh] filter ledge spans");
                RcFilters.FilterLedgeSpans(telemetry, cfg.WalkableHeight, cfg.WalkableClimb, solid.Heightfield);
            }

            if (cfg.FilterWalkableLowHeightSpans)
            {
                // mark walkable spans of very low height (smaller than agent height) as non-walkable
                Service.Log.Debug("[navmesh] filter walkable low-height spans");
                RcFilters.FilterWalkableLowHeightSpans(telemetry, cfg.WalkableHeight, solid.Heightfield);
            }

            // 3. create a 'compact heightfield' structure
            // this is very similar to a normal heightfield, except that spans are now stored in a single array, and grid cells just contain consecutive ranges
            // this also contains connectivity data (links to neighbouring cells)
            // note that spans from null areas are not added to the compact heightfield
            // also note that for each span, y is equal to the solid span's smax (makes sense - in solid, walkable voxel is one containing walkable geometry, so free area is 'above')
            // h is not really used beyond connectivity calculations (it's a distance to the next span - potentially of null area - or to maxheight)
            Service.Log.Debug("[navmesh] build compact heightfield with connectivity data");
            RcCompactHeightfield chf = RcCompacts.BuildCompactHeightfield(telemetry, cfg.WalkableHeight, cfg.WalkableClimb, solid.Heightfield);

            // 4. mark spans that are too close to unwalkable as unwalkable, to account for actor's non-zero radius
            // this changes area of some spans from walkable to non-walkable
            // note that before this step, compact heightfield has no non-walkable spans
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
            // contour set is just a list of contours, each of which is (when projected to XZ plane) a simple non-convex polygon that belong to a single region with a single area id
            Service.Log.Debug("[navmesh] tracing and simplyfing contours");
            RcContourSet cset = RcContours.BuildContours(telemetry, chf, cfg.MaxSimplificationError, cfg.MaxEdgeLen, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);

            // 7. triangulate contours to build a mesh of convex polygons with adjacency information
            Service.Log.Debug("[navmesh] building polygon mesh from contours");
            RcPolyMesh pmesh = RcMeshs.BuildPolyMesh(telemetry, cset, cfg.MaxVertsPerPoly);
            for (int i = 0; i < pmesh.npolys; ++i)
                pmesh.flags[i] = 1;

            // 8. split polygonal mesh into triangular mesh with correct height
            // this step is optional?..
            Service.Log.Debug("[navmesh] creating detail triangle mesh");
            RcPolyMeshDetail dmesh = RcMeshDetails.BuildPolyMeshDetail(telemetry, pmesh, chf, cfg.DetailSampleDist, cfg.DetailSampleMaxError);
            var rcResult = new RcBuilderResult(0, 0, solid.Heightfield, chf, cset, pmesh, dmesh, telemetry);

            // 9. create detour navmesh
            Service.Log.Debug("[navmesh] detour navmesh build");
            var navmeshConfig = new DtNavMeshCreateParams()
            {
                verts = pmesh.verts,
                vertCount = pmesh.nverts,
                polys = pmesh.polys,
                polyFlags = pmesh.flags,
                polyAreas = pmesh.areas,
                polyCount = pmesh.npolys,
                nvp = pmesh.nvp,

                detailMeshes = dmesh.meshes,
                detailVerts = dmesh.verts,
                detailVertsCount = dmesh.nverts,
                detailTris = dmesh.tris,
                detailTriCount = dmesh.ntris,

                bmin = pmesh.bmin,
                bmax = pmesh.bmax,
                walkableHeight = AgentHeight,
                walkableRadius = AgentRadius,
                walkableClimb = AgentMaxClimb,
                cs = CellSize,
                ch = CellHeight,
                buildBvTree = true,
            };
            var navmeshData = DtNavMeshBuilder.CreateNavMeshData(navmeshConfig);
            var navmesh = new DtNavMesh(navmeshData, VertsPerPoly, 0);

            Service.Log.Debug("[navmesh] end");
            _intermediates = rcResult;
            _navmesh = navmesh;
            _query = new DtNavMeshQuery(navmesh);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error building navmesh: {ex}");
            throw;
        }
    }
}
