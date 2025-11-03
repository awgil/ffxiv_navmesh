using Dalamud.Plugin.Services;
using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Recast;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public sealed class TileManager : IDisposable
{
    public readonly SceneTracker Scene;
    public static readonly Vector3 BoundsMin = new(-1024);
    public static readonly Vector3 BoundsMax = new(1024);

    private readonly CancellationTokenSource?[,] _cancel = new CancellationTokenSource[16, 16];
    private double[,] _timers = new double[16, 16];
    public readonly Task?[,] Tasks = new Task?[16, 16];

    public volatile int NumTasks;
    private readonly Lock locked = new();
    private readonly SemaphoreSlim _sem = new(12);

    public NavmeshCustomization Customization = new();

    private readonly NavmeshManager _manager;

    public DtNavMesh? Mesh;

    internal double GetTimer(int x, int z) => _timers[x, z];

    private bool _debounce;

    public TileManager(NavmeshManager manager)
    {
        _manager = manager;

        Customization = NavmeshCustomizationRegistry.ForTerritory(Service.ClientState.TerritoryType);
        Scene = new(Customization.Settings.NumTiles[0]);
        Scene.TileChanged += (x, z) =>
        {
            _cancel[x, z]?.Cancel();
            if (_debounce)
                _timers[x, z] = Scene.DebounceMS;
            else
                // currently initialized layout (on plugin load) should be loaded without delay
                BuildTile(x, z);
        };

        Scene.Init();
        _debounce = true;

        Service.ClientState.ZoneInit += OnZoneInit;
        Mesh = new(MakeParams(), 6);
    }

    private DtNavMeshParams MakeParams() => new()
    {
        orig = BoundsMin.SystemToRecast(),
        tileWidth = (BoundsMax.X - BoundsMin.X) / Scene.NumTilesInRow,
        tileHeight = (BoundsMax.Z - BoundsMin.Z) / Scene.NumTilesInRow,
        maxTiles = Scene.NumTiles,
        maxPolys = 1 << DtNavMesh.DT_POLY_BITS
    };

    private void OnZoneInit(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        CancelAll();

        Customization = NavmeshCustomizationRegistry.ForTerritory(obj.TerritoryType.RowId);
        Scene.NumTilesInRow = Customization.Settings.NumTiles[0];
        Scene.Clear();
        _timers = new double[Scene.NumTilesInRow, Scene.NumTilesInRow];
        Mesh = new(MakeParams(), 6);
    }

    public void Dispose()
    {
        CancelAll();
        Scene.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CancelAll()
    {
        foreach (var c in _cancel)
            c?.Cancel();
    }

    public void Update(IFramework framework)
    {
        var dt = framework.UpdateDelta.TotalMilliseconds;

        for (var i = 0; i < _timers.GetLength(0); i++)
        {
            for (var j = 0; j < _timers.GetLength(1); j++)
            {
                var wasPositive = _timers[i, j] > 0;
                _timers[i, j] -= dt;
                if (wasPositive && _timers[i, j] <= 0)
                    BuildTile(i, j);
            }
        }
    }

    private void BuildTile(int x, int z)
    {
        var cancel = _cancel[x, z] = new CancellationTokenSource();
        Tasks[x, z] = Task.Run(() =>
        {
            Interlocked.Add(ref NumTasks, 1);

            try
            {
                _sem.Wait(cancel.Token);
                try
                {
                    RebuildTile(x, z, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                }
                finally
                {
                    _sem.Release();
                }
            }
            finally
            {
                Interlocked.Add(ref NumTasks, -1);
            }
        }, cancel.Token).ContinueWith(t =>
        {
            if (NumTasks == 0 && Mesh != null)
            {
                Service.Log.Debug($"all tiles done, replacing mesh");
                _manager.ReplaceMesh(new(Customization.Version, Mesh, null));
            }
        });
    }

    private void RebuildTile(int x, int z, CancellationToken token)
    {
        var data = LoadOrBuildTile(x, z, token);

        if (data == null || Mesh == null)
            return;

        lock (locked)
        {
            Mesh.UpdateTile(data, 0);
            Service.Log.Debug($"added tile {x}x{z}");
        }
    }

    private DtMeshData? LoadOrBuildTile(int x, int z, CancellationToken token)
    {
        var customization = NavmeshCustomizationRegistry.ForTerritory(Scene.Territory);

        var contents = Scene.CloneTile(x, z);
        var cacheKey = SceneTracker.GetHashFromKeys([.. contents.Objects.Keys]);

        DtMeshData? data = null;

        Service.Log.Debug($"kicking off build for tile {x}x{z} - key is {cacheKey}");

        var dir = new DirectoryInfo($"{_manager.CacheDir}/{Service.ClientState.TerritoryType}");
        var cacheFile = new FileInfo(dir.FullName + $"/{x:X2}-{z:X2}-{cacheKey}");

        if (!dir.Exists) dir.Create();

        if (cacheFile.Exists)
        {
            Service.Log.Debug($"loading tile {x}x{z} from cache");
            using var stream = cacheFile.OpenRead();
            using var reader = new BinaryReader(stream);
            data = Navmesh.DeserializeSingleTile(reader, customization.Version);
            return data;
        }

        var builder = new TileBuilder(x, z, [.. contents.Objects.Values.Select(v => (v.Mesh, v.Instance))], customization, customization.IsFlyingSupported(Service.ClientState.TerritoryType));
        data = builder.Build(token).Item1;

        Service.Log.Debug($"writing tile {x}x{z} to cache");
        using var wstream = cacheFile.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(wstream);
        Navmesh.SerializeSingleTile(writer, data, customization.Version);
        return data;
    }
}

public sealed class TileBuilder
{
    public RcContext Telemetry = new();
    public NavmeshSettings Settings;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int NumTilesX;
    public int NumTilesZ;

    public List<(SceneExtractor.Mesh, SceneExtractor.MeshInstance)> Tile;
    public int TileX;
    public int TileZ;

    private readonly int _walkableClimbVoxels;
    private readonly int _walkableHeightVoxels;
    private readonly int _walkableRadiusVoxels;
    private readonly float _walkableNormalThreshold;
    private readonly int _borderSizeVoxels;
    private readonly float _borderSizeWorld;
    private readonly int _tileSizeXVoxels;
    private readonly int _tileSizeZVoxels;
    private readonly int _voxelizerNumX = 1;
    private readonly int _voxelizerNumY = 1;
    private readonly int _voxelizerNumZ = 1;

    private readonly DtNavMesh navmesh;
    private readonly VoxelMap? volume;

    public TileBuilder(int x, int z, List<(SceneExtractor.Mesh, SceneExtractor.MeshInstance)> tile, NavmeshCustomization customization, bool flyable)
    {
        Tile = tile;
        TileX = x;
        TileZ = z;

        Settings = customization.Settings;

        var NumTilesX = Settings.NumTiles[0];
        var NumTilesZ = NumTilesX;

        BoundsMin = new(-1024);
        BoundsMax = new(1024);

        var navmeshParams = new DtNavMeshParams
        {
            orig = BoundsMin.SystemToRecast(),
            tileWidth = (BoundsMax.X - BoundsMin.X) / NumTilesX,
            tileHeight = (BoundsMax.Z - BoundsMin.Z) / NumTilesZ,
            maxTiles = NumTilesX * NumTilesZ,
            maxPolys = 1 << DtNavMesh.DT_POLY_BITS
        };

        navmesh = new DtNavMesh(navmeshParams, Settings.PolyMaxVerts);
        volume = flyable ? new VoxelMap(BoundsMin, BoundsMax, Settings.NumTiles) : null;

        // calculate derived parameters
        _walkableClimbVoxels = (int)MathF.Floor(Settings.AgentMaxClimb / Settings.CellHeight);
        _walkableHeightVoxels = (int)MathF.Ceiling(Settings.AgentHeight / Settings.CellHeight);
        _walkableRadiusVoxels = (int)MathF.Ceiling(Settings.AgentRadius / Settings.CellSize);
        _walkableNormalThreshold = Settings.AgentMaxSlopeDeg.Degrees().Cos();
        _borderSizeVoxels = 3 + _walkableRadiusVoxels;
        _borderSizeWorld = _borderSizeVoxels * Settings.CellSize;
        _tileSizeXVoxels = (int)MathF.Ceiling(navmeshParams.tileWidth / Settings.CellSize) + 2 * _borderSizeVoxels;
        _tileSizeZVoxels = (int)MathF.Ceiling(navmeshParams.tileHeight / Settings.CellSize) + 2 * _borderSizeVoxels;
        if (volume != null)
        {
            _voxelizerNumY = Settings.NumTiles[0];
            for (int i = 1; i < Settings.NumTiles.Length; ++i)
            {
                var n = Settings.NumTiles[i];
                _voxelizerNumX *= n;
                _voxelizerNumY *= n;
                _voxelizerNumZ *= n;
            }
        }
    }

    public (DtMeshData?, RcBuilderResult) Build(CancellationToken token)
    {
        var timer = Timer.Create();

        var widthWorld = navmesh.GetParams().tileWidth;
        var heightWorld = navmesh.GetParams().tileHeight;
        var tileBoundsMin = new Vector3(BoundsMin.X + TileX * widthWorld, BoundsMin.Y, BoundsMin.Z + TileZ * heightWorld);
        var tileBoundsMax = new Vector3(tileBoundsMin.X + widthWorld, BoundsMax.Y, tileBoundsMin.Z + heightWorld);
        tileBoundsMin.X -= _borderSizeWorld;
        tileBoundsMin.Z -= _borderSizeWorld;
        tileBoundsMax.X += _borderSizeWorld;
        tileBoundsMax.Z += _borderSizeWorld;

        var shf = new RcHeightfield(_tileSizeXVoxels, _tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), Settings.CellSize, Settings.CellHeight, _borderSizeVoxels);
        var vox = volume != null ? new Voxelizer(_voxelizerNumX, _voxelizerNumY, _voxelizerNumZ) : null;
        var rasterizer = new NavmeshRasterizer(shf, _walkableNormalThreshold, _walkableClimbVoxels, _walkableHeightVoxels, Settings.Filtering.HasFlag(NavmeshSettings.Filter.Interiors), vox, Telemetry);

        rasterizer.RasterizeFlat(Tile, SceneExtractor.MeshType.FileMesh | SceneExtractor.MeshType.CylinderMesh | SceneExtractor.MeshType.AnalyticShape, true, true, token);
        rasterizer.RasterizeFlat(Tile, SceneExtractor.MeshType.Terrain | SceneExtractor.MeshType.AnalyticPlane, false, true, token);

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
            // mark walkable spans of very low height (smaller than agent height) as non-walkable (TODO: do we still need it?)
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

            tileX = TileX,
            tileZ = TileZ,
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

        var meshData = DtNavMeshBuilder.CreateNavMeshData(navmeshConfig);

        Service.Log.Debug($"built navmesh tile {TileX}x{TileZ} in {timer.Value().TotalMilliseconds}ms");

        return (meshData, new RcBuilderResult(TileX, TileZ, shf, chf, cset, pmesh, dmesh, Telemetry));
    }
}
