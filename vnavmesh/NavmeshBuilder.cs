using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Toolset.Tools;
using Navmesh.NavVolume;
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

    public class IntermediateData
    {
        public int NumTilesX;
        public int NumTilesZ;
        public RcHeightfield[,] SolidHeightfields;
        public RcCompactHeightfield[,] CompactHeightfields;
        public RcContourSet[,] ContourSets;
        public RcPolyMesh[,] PolyMeshes;
        public RcPolyMeshDetail?[,] DetailMeshes;
        public RcTelemetry Telemetry;

        public IntermediateData(int numTilesX, int numTilesZ)
        {
            NumTilesX = numTilesX;
            NumTilesZ = numTilesZ;
            SolidHeightfields = new RcHeightfield[numTilesX, numTilesZ];
            CompactHeightfields = new RcCompactHeightfield[numTilesX, numTilesZ];
            ContourSets = new RcContourSet[numTilesX, numTilesZ];
            PolyMeshes = new RcPolyMesh[numTilesX, numTilesZ];
            DetailMeshes = new RcPolyMeshDetail?[numTilesX, numTilesZ];
            Telemetry = new();
        }
    }

    // config
    public NavmeshSettings Settings = new();

    // results - should not be accessed while task is running
    private SceneDefinition? _scene;
    private SceneExtractor? _extractor;
    private IntermediateData? _intermediates;
    private DtNavMesh? _navmesh;
    private DtNavMeshQuery? _query;
    private VoxelMap? _volume;
    private PathfindQuery? _volumeQuery;
    private Task? _task;

    public State CurrentState => _task == null ? State.NotBuilt : !_task.IsCompleted ? State.InProgress : _task.IsFaulted ? State.Failed : State.Ready;
    public SceneDefinition? Scene => _task != null && _task.IsCompletedSuccessfully ? _scene : null;
    public SceneExtractor? Extractor => _task != null && _task.IsCompletedSuccessfully ? _extractor : null;
    public IntermediateData? Intermediates => _task != null && _task.IsCompletedSuccessfully ? _intermediates : null;
    public DtNavMesh? Navmesh => _task != null && _task.IsCompletedSuccessfully ? _navmesh : null;
    public DtNavMeshQuery? Query => _task != null && _task.IsCompletedSuccessfully ? _query : null;
    public VoxelMap? Volume => _task != null && _task.IsCompletedSuccessfully ? _volume : null;
    public PathfindQuery? VolumeQuery => _task != null && _task.IsCompletedSuccessfully ? _volumeQuery : null;

    public void Dispose()
    {
        Clear();
    }

    public void Rebuild()
    {
        Clear();
        Service.Log.Debug("[navmesh] extract from scene");
        _scene = new();
        _scene.FillFromActiveLayout();
        Service.Log.Debug("[navmesh] schedule async build");
        _task = Task.Run(() => BuildNavmesh(_scene));
    }

    public void Clear()
    {
        if (_task != null)
        {
            if (!_task.IsCompleted)
                _task.Wait();
            _task.Dispose();
            _task = null;
        }
        _scene = null;
        _extractor = null;
        _intermediates = null;
        _navmesh = null;
        _query = null;
        _volume = null;
        _volumeQuery = null;
        //GC.Collect();
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

    public List<Vector3> PathfindVolume(Vector3 from, Vector3 to)
    {
        return VolumeQuery?.FindPath(from, to) ?? new();
    }

    private void BuildNavmesh(SceneDefinition scene)
    {
        try
        {
            var timer = Timer.Create();
            var telemetry = new RcTelemetry();

            // load all meshes
            _extractor = new(scene);

            int ntilesX = 1, ntilesZ = 1;
            if (Settings.TileSize > 0)
                RcCommons.CalcTileCount(_extractor.BoundsMin.SystemToRecast(), _extractor.BoundsMax.SystemToRecast(), Settings.CellSize, Settings.TileSize, Settings.TileSize, out ntilesX, out ntilesZ);
            Service.Log.Debug($"starting building {ntilesX}x{ntilesZ} navmesh");

            // create empty navmesh
            var navmeshParams = new DtNavMeshParams();
            navmeshParams.orig = _extractor.BoundsMin.SystemToRecast();
            if (Settings.TileSize > 0)
            {
                navmeshParams.tileWidth = navmeshParams.tileHeight = Settings.TileSize * Settings.CellSize;
            }
            else
            {
                navmeshParams.tileWidth = _extractor.BoundsMax.X - _extractor.BoundsMin.X;
                navmeshParams.tileHeight = _extractor.BoundsMax.Z - _extractor.BoundsMin.Z;
            }
            navmeshParams.maxTiles = ntilesX * ntilesZ;
            navmeshParams.maxPolys = 1 << DtNavMesh.DT_POLY_BITS;
            var navmesh = new DtNavMesh(navmeshParams, Settings.PolyMaxVerts);
            var volume = new VoxelMap(_extractor.BoundsMin, _extractor.BoundsMax, 512, 128, 512); // TODO: improve...

            // create tile data and add to navmesh
            _intermediates = new(ntilesX, ntilesZ);
            for (int z = 0; z < ntilesZ; ++z)
            {
                for (int x = 0; x < ntilesX; ++x)
                {
                    var tile = BuildNavmeshTile(x, z, _extractor, _intermediates, _intermediates.Telemetry, volume);
                    if (tile != null)
                        navmesh.AddTile(tile, 0, 0);
                }
            }

            Service.Log.Debug($"navmesh build time: {timer.Value().TotalMilliseconds}ms");
            _navmesh = navmesh;
            _query = new DtNavMeshQuery(navmesh);
            _volume = volume;
            _volumeQuery = new(volume);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error building navmesh: {ex}");
            throw;
        }
    }

    private DtMeshData? BuildNavmeshTile(int x, int z, SceneExtractor extractor, IntermediateData? intermediates, RcTelemetry telemetry, VoxelMap volume)
    {
        var timer = Timer.Create();

        var walkableClimbVoxels = (int)MathF.Floor(Settings.AgentMaxClimb / Settings.CellHeight);
        var walkableHeightVoxels = (int)MathF.Ceiling(Settings.AgentHeight / Settings.CellHeight);
        var walkableRadiusVoxels = (int)MathF.Ceiling(Settings.AgentRadius / Settings.CellSize);
        int borderSizeVoxels = 0;
        var boundsMin = extractor.BoundsMin;
        var boundsMax = extractor.BoundsMax;
        int widthVoxels, heightVoxels;
        if (Settings.TileSize > 0)
        {
            borderSizeVoxels = 3 + walkableRadiusVoxels;
            var tileSizeWorld = Settings.TileSize * Settings.CellSize;
            boundsMin.X += x * tileSizeWorld;
            boundsMin.Z += z * tileSizeWorld;
            boundsMax.X = boundsMin.X + tileSizeWorld;
            boundsMax.Z = boundsMin.Z + tileSizeWorld;

            // Expand the heighfield bounding box by border size to find the extents of geometry we need to build this tile.
            // This is done in order to make sure that the navmesh tiles connect correctly at the borders,
            // and the obstacles close to the border work correctly with the dilation process.
            // No polygons (or contours) will be created on the border area.
            var borderSizeWorld = borderSizeVoxels * Settings.CellSize;
            boundsMin.X -= borderSizeWorld;
            boundsMin.Z -= borderSizeWorld;
            boundsMax.X += borderSizeWorld;
            boundsMax.Z += borderSizeWorld;

            widthVoxels = heightVoxels = Settings.TileSize + borderSizeVoxels * 2;
        }
        else
        {
            RcCommons.CalcGridSize(boundsMin.SystemToRecast(), boundsMax.SystemToRecast(), Settings.CellSize, out widthVoxels, out heightVoxels);
        }

        // 1. voxelize raw geometry
        // this creates a 'solid heightfield', which is a grid of sorted linked lists of spans
        // each span contains an 'area id', which is either walkable (if normal is good) or not (otherwise); areas outside spans contains no geometry at all
        var shf = new RcHeightfield(widthVoxels, heightVoxels, boundsMin.SystemToRecast(), boundsMax.SystemToRecast(), Settings.CellSize, Settings.CellHeight, borderSizeVoxels);
        var rasterizer = new NavmeshRasterizer(shf, Settings.AgentMaxSlopeDeg.Degrees(), walkableClimbVoxels, telemetry);
        rasterizer.Rasterize(extractor, true, true, false); // TODO: some analytic meshes are used for things like doors - their raycast flag is removed when they are opened ... :(

        // 2. perform a bunch of postprocessing on a heightfield
        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.LowHangingObstacles))
        {
            // mark non-walkable spans as walkable if their maximum is within climb distance of the span below
            // this allows climbing stairs, walking over curbs, etc
            RcFilters.FilterLowHangingWalkableObstacles(telemetry, walkableClimbVoxels, shf);
        }

        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.LedgeSpans))
        {
            // mark 'ledge' spans as non-walkable - spans that have too large height distance to the neighbour
            // this reduces the impact of voxelization error
            RcFilters.FilterLedgeSpans(telemetry, walkableHeightVoxels, walkableClimbVoxels, shf);
        }

        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.WalkableLowHeightSpans))
        {
            // mark walkable spans of very low height (smaller than agent height) as non-walkable
            RcFilters.FilterWalkableLowHeightSpans(telemetry, walkableHeightVoxels, shf);
        }

        // 3. create a 'compact heightfield' structure
        // this is very similar to a normal heightfield, except that spans are now stored in a single array, and grid cells just contain consecutive ranges
        // this also contains connectivity data (links to neighbouring cells)
        // note that spans from null areas are not added to the compact heightfield
        // also note that for each span, y is equal to the solid span's smax (makes sense - in solid, walkable voxel is one containing walkable geometry, so free area is 'above')
        // h is not really used beyond connectivity calculations (it's a distance to the next span - potentially of null area - or to maxheight)
        var chf = RcCompacts.BuildCompactHeightfield(telemetry, walkableHeightVoxels, walkableClimbVoxels, shf);

        // 4. mark spans that are too close to unwalkable as unwalkable, to account for actor's non-zero radius
        // this changes area of some spans from walkable to non-walkable
        // note that before this step, compact heightfield has no non-walkable spans
        RcAreas.ErodeWalkableArea(telemetry, walkableRadiusVoxels, chf);
        // note: this is the good time to mark convex poly areas with custom area ids

        // 5. build connected regions; this assigns region ids to spans in the compact heightfield
        // there are different algorithms with different tradeoffs
        var regionMinArea = (int)(Settings.RegionMinSize * Settings.RegionMinSize);
        var regionMergeArea = (int)(Settings.RegionMergeSize * Settings.RegionMergeSize);
        if (Settings.Partitioning == RcPartition.WATERSHED)
        {
            RcRegions.BuildDistanceField(telemetry, chf);
            RcRegions.BuildRegions(telemetry, chf, regionMinArea, regionMergeArea);
        }
        else if (Settings.Partitioning == RcPartition.MONOTONE)
        {
            RcRegions.BuildRegionsMonotone(telemetry, chf, regionMinArea, regionMergeArea);
        }
        else
        {
            RcRegions.BuildLayerRegions(telemetry, chf, regionMinArea);
        }

        // 6. build contours around regions, then simplify them to reduce vertex count
        // contour set is just a list of contours, each of which is (when projected to XZ plane) a simple non-convex polygon that belong to a single region with a single area id
        var polyMaxEdgeLenVoxels = (int)(Settings.PolyMaxEdgeLen / Settings.CellSize);
        var cset = RcContours.BuildContours(telemetry, chf, Settings.PolyMaxSimplificationError, polyMaxEdgeLenVoxels, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);

        // 7. triangulate contours to build a mesh of convex polygons with adjacency information
        var pmesh = RcMeshs.BuildPolyMesh(telemetry, cset, Settings.PolyMaxVerts);
        for (int i = 0; i < pmesh.npolys; ++i)
            pmesh.flags[i] = 1;

        // 8. split polygonal mesh into triangular mesh with correct height
        // this step is optional
        var detailSampleDist = Settings.DetailSampleDist < 0.9f ? 0 : Settings.CellSize * Settings.DetailSampleDist;
        var detailSampleMaxError = Settings.CellHeight * Settings.DetailMaxSampleError;
        RcPolyMeshDetail? dmesh = RcMeshDetails.BuildPolyMeshDetail(telemetry, pmesh, chf, detailSampleDist, detailSampleMaxError);

        // 9. create detour navmesh data
        var navmeshConfig = new DtNavMeshCreateParams()
        {
            verts = pmesh.verts,
            vertCount = pmesh.nverts,
            polys = pmesh.polys,
            polyFlags = pmesh.flags,
            polyAreas = pmesh.areas,
            polyCount = pmesh.npolys,
            nvp = pmesh.nvp,

            detailMeshes = dmesh?.meshes,
            detailVerts = dmesh?.verts,
            detailVertsCount = dmesh?.nverts ?? 0,
            detailTris = dmesh?.tris,
            detailTriCount = dmesh?.ntris ?? 0,

            tileX = x,
            tileZ = z,
            tileLayer = 0, // TODO: do we care to use layers?..
            bmin = pmesh.bmin,
            bmax = pmesh.bmax,

            walkableHeight = Settings.AgentHeight,
            walkableRadius = Settings.AgentRadius,
            walkableClimb = Settings.AgentMaxClimb,
            cs = Settings.CellSize,
            ch = Settings.CellHeight,

            buildBvTree = true, // TODO: false if using layers?
        };
        var navmeshData = DtNavMeshBuilder.CreateNavMeshData(navmeshConfig);

        // 10. build nav volume data
        volume.AddFromHeightfield(shf);

        // if we want to keep intermediates, store them
        if (intermediates != null)
        {
            intermediates.SolidHeightfields[x, z] = shf;
            intermediates.CompactHeightfields[x, z] = chf;
            intermediates.ContourSets[x, z] = cset;
            intermediates.PolyMeshes[x, z] = pmesh;
            intermediates.DetailMeshes[x, z] = dmesh;
        }

        Service.Log.Debug($"built navmesh tile {x}x{z} in {timer.Value().TotalMilliseconds}ms");
        return navmeshData;
    }
}
