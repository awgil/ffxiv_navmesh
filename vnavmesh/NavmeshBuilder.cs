using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Recast;
using Navmesh.NavVolume;
using System;
using System.Numerics;

namespace Navmesh;

// utility for building a navmesh from scene data
// individual tiles can be built concurrently
public class NavmeshBuilder
{
    public record struct Intermediates(RcHeightfield SolidHeightfield, RcCompactHeightfield CompactHeightfield, RcContourSet ContourSet, RcPolyMesh PolyMesh, RcPolyMeshDetail? DetailMesh);

    public RcContext Telemetry = new();
    public NavmeshSettings Settings;
    public SceneExtractor Scene;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int NumTilesX;
    public int NumTilesZ;
    public Navmesh Navmesh; // should not be accessed while building tiles

    private int _walkableClimbVoxels;
    private int _walkableHeightVoxels;
    private int _walkableRadiusVoxels;
    private float _walkableNormalThreshold;
    private int _borderSizeVoxels;
    private float _borderSizeWorld;
    private int _tileSizeXVoxels;
    private int _tileSizeZVoxels;

    public NavmeshBuilder(SceneDefinition scene, NavmeshSettings settings)
    {
        Settings = settings;

        // load all meshes
        Scene = new(scene);
        BoundsMin = new(-1024);
        BoundsMax = new(1024);
        NumTilesX = NumTilesZ = settings.NumTiles[0];
        Service.Log.Debug($"starting building {NumTilesX}x{NumTilesZ} navmesh");

        // create empty navmesh
        var navmeshParams = new DtNavMeshParams();
        navmeshParams.orig = BoundsMin.SystemToRecast();
        navmeshParams.tileWidth = (BoundsMax.X - BoundsMin.X) / NumTilesX;
        navmeshParams.tileHeight = (BoundsMax.Z - BoundsMin.Z) / NumTilesZ;
        navmeshParams.maxTiles = NumTilesX * NumTilesZ;
        navmeshParams.maxPolys = 1 << DtNavMesh.DT_POLY_BITS;

        var navmesh = new DtNavMesh(navmeshParams, settings.PolyMaxVerts);
        var volume = new VoxelMap(BoundsMin, BoundsMax, settings); // TODO: improve...
        Navmesh = new(navmesh, volume);

        // calculate derived parameters
        _walkableClimbVoxels = (int)MathF.Floor(Settings.AgentMaxClimb / Settings.CellHeight);
        _walkableHeightVoxels = (int)MathF.Ceiling(Settings.AgentHeight / Settings.CellHeight);
        _walkableRadiusVoxels = (int)MathF.Ceiling(Settings.AgentRadius / Settings.CellSize);
        _walkableNormalThreshold = Settings.AgentMaxSlopeDeg.Degrees().Cos();
        _borderSizeVoxels = 3 + _walkableRadiusVoxels;
        _borderSizeWorld = _borderSizeVoxels * Settings.CellSize;
        _tileSizeXVoxels = (int)MathF.Ceiling(navmeshParams.tileWidth / Settings.CellSize) + 2 * _borderSizeVoxels;
        _tileSizeZVoxels = (int)MathF.Ceiling(navmeshParams.tileHeight / Settings.CellSize) + 2 * _borderSizeVoxels;
    }

    // this can be called concurrently; returns intermediate data that can be discarded if not used
    public Intermediates BuildTile(int x, int z)
    {
        var timer = Timer.Create();

        // 0. calculate tile bounds
        // we expand the heighfield bounding box by border size to find the extents of geometry we need to build this tile
        // this is done in order to make sure that the navmesh tiles connect correctly at the borders, and the obstacles close to the border work correctly with the dilation process
        // no polygons (or contours) will be created on the border area
        float widthWorld = Navmesh.Mesh.GetParams().tileWidth;
        float heightWorld = Navmesh.Mesh.GetParams().tileHeight;
        var tileBoundsMin = new Vector3(BoundsMin.X + x * widthWorld, BoundsMin.Y, BoundsMin.Z + z * heightWorld);
        var tileBoundsMax = new Vector3(tileBoundsMin.X + widthWorld, BoundsMax.Y, tileBoundsMin.Z + heightWorld);
        tileBoundsMin.X -= _borderSizeWorld;
        tileBoundsMin.Z -= _borderSizeWorld;
        tileBoundsMax.X += _borderSizeWorld;
        tileBoundsMax.Z += _borderSizeWorld;

        // 1. voxelize raw geometry
        // this creates a 'solid heightfield', which is a grid of sorted linked lists of spans
        // each span contains an 'area id', which is either walkable (if normal is good) or not (otherwise); areas outside spans contains no geometry at all
        var shf = new RcHeightfield(_tileSizeXVoxels, _tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), Settings.CellSize, Settings.CellHeight, _borderSizeVoxels);
        var rasterizer = new NavmeshRasterizer(shf, _walkableNormalThreshold, _walkableClimbVoxels, Telemetry);
        rasterizer.Rasterize(Scene, true, true, true);

        // 2. perform a bunch of postprocessing on a heightfield
        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.LowHangingObstacles))
        {
            // mark non-walkable spans as walkable if their maximum is within climb distance of the span below
            // this allows climbing stairs, walking over curbs, etc
            RcFilters.FilterLowHangingWalkableObstacles(Telemetry, _walkableClimbVoxels, shf);
        }

        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.LedgeSpans))
        {
            // mark 'ledge' spans as non-walkable - spans that have too large height distance to the neighbour
            // this reduces the impact of voxelization error
            RcFilters.FilterLedgeSpans(Telemetry, _walkableHeightVoxels, _walkableClimbVoxels, shf);
        }

        if (Settings.Filtering.HasFlag(NavmeshSettings.Filter.WalkableLowHeightSpans))
        {
            // mark walkable spans of very low height (smaller than agent height) as non-walkable
            RcFilters.FilterWalkableLowHeightSpans(Telemetry, _walkableHeightVoxels, shf);
        }

        // 3. create a 'compact heightfield' structure
        // this is very similar to a normal heightfield, except that spans are now stored in a single array, and grid cells just contain consecutive ranges
        // this also contains connectivity data (links to neighbouring cells)
        // note that spans from null areas are not added to the compact heightfield
        // also note that for each span, y is equal to the solid span's smax (makes sense - in solid, walkable voxel is one containing walkable geometry, so free area is 'above')
        // h is not really used beyond connectivity calculations (it's a distance to the next span - potentially of null area - or to maxheight)
        var chf = RcCompacts.BuildCompactHeightfield(Telemetry, _walkableHeightVoxels, _walkableClimbVoxels, shf);

        // 4. mark spans that are too close to unwalkable as unwalkable, to account for actor's non-zero radius
        // this changes area of some spans from walkable to non-walkable
        // note that before this step, compact heightfield has no non-walkable spans
        RcAreas.ErodeWalkableArea(Telemetry, _walkableRadiusVoxels, chf);
        // note: this is the good time to mark convex poly areas with custom area ids

        // 5. build connected regions; this assigns region ids to spans in the compact heightfield
        // there are different algorithms with different tradeoffs
        var regionMinArea = (int)(Settings.RegionMinSize * Settings.RegionMinSize);
        var regionMergeArea = (int)(Settings.RegionMergeSize * Settings.RegionMergeSize);
        if (Settings.Partitioning == RcPartition.WATERSHED)
        {
            RcRegions.BuildDistanceField(Telemetry, chf);
            RcRegions.BuildRegions(Telemetry, chf, regionMinArea, regionMergeArea);
        }
        else if (Settings.Partitioning == RcPartition.MONOTONE)
        {
            RcRegions.BuildRegionsMonotone(Telemetry, chf, regionMinArea, regionMergeArea);
        }
        else
        {
            RcRegions.BuildLayerRegions(Telemetry, chf, regionMinArea);
        }

        // 6. build contours around regions, then simplify them to reduce vertex count
        // contour set is just a list of contours, each of which is (when projected to XZ plane) a simple non-convex polygon that belong to a single region with a single area id
        var polyMaxEdgeLenVoxels = (int)(Settings.PolyMaxEdgeLen / Settings.CellSize);
        var cset = RcContours.BuildContours(Telemetry, chf, Settings.PolyMaxSimplificationError, polyMaxEdgeLenVoxels, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);

        // 7. triangulate contours to build a mesh of convex polygons with adjacency information
        var pmesh = RcMeshs.BuildPolyMesh(Telemetry, cset, Settings.PolyMaxVerts);
        for (int i = 0; i < pmesh.npolys; ++i)
            pmesh.flags[i] = 1;

        // 8. split polygonal mesh into triangular mesh with correct height
        // this step is optional
        var detailSampleDist = Settings.DetailSampleDist < 0.9f ? 0 : Settings.CellSize * Settings.DetailSampleDist;
        var detailSampleMaxError = Settings.CellHeight * Settings.DetailMaxSampleError;
        RcPolyMeshDetail? dmesh = RcMeshDetails.BuildPolyMeshDetail(Telemetry, pmesh, chf, detailSampleDist, detailSampleMaxError);

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

        // 10. add tile to the navmesh
        if (navmeshData != null)
        {
            lock (Navmesh.Mesh)
            {
                Navmesh.Mesh.AddTile(navmeshData, 0, 0);
            }
        }

        // 11. build nav volume data
        // TODO: keep local 1x1x16 voxel map, and just merge under lock
        lock (Navmesh.Volume)
        {
            Navmesh.Volume.AddFromHeightfield(shf);
        }

        Service.Log.Debug($"built navmesh tile {x}x{z} in {timer.Value().TotalMilliseconds}ms");
        return new(shf, chf, cset, pmesh, dmesh);
    }
}
