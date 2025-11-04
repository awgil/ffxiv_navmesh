using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Recast;
using Navmesh.NavVolume;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public sealed class TileManager : IDisposable
{
    public readonly SceneTracker Scene = new();
    public static readonly Vector3 BoundsMin = new(-1024);
    public static readonly Vector3 BoundsMax = new(1024);

    public double DebounceMs = 500.0;

    public sealed class BuildTask
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;

        private BuildTask(CancellationTokenSource src, Task task)
        {
            _cts = src;
            _task = task;
        }

        public static BuildTask Spawn(Func<CancellationToken, Task> task)
        {
            var cts = new CancellationTokenSource();
            var stored = Task.Run(async () => await task(cts.Token), cts.Token);
            return new(cts, stored);
        }

        public TaskStatus Status => _task.Status;
        public void Cancel() => _cts.Cancel();
    }

    public BuildTask?[,] Tasks { get; private set; } = new BuildTask?[16, 16];

    public volatile int NumTasks;
    private readonly Lock locked = new();
    private readonly SemaphoreSlim _sem = new(0);

    public NavmeshCustomization Customization
    {
        get;
        set
        {
            field = value;

            // update tile grid
            Scene.RowLength = value.Settings.NumTiles[0];
            foreach (var t in Tasks)
                t?.Cancel();
            Tasks = new BuildTask?[Scene.RowLength, Scene.RowLength];

            RecreateMesh();
        }
    } = new();

    private readonly NavmeshManager _manager;

    public DtNavMesh? Mesh;
    public VoxelMap? Volume;

    private bool _initialized;
    private int Concurrency;

    public TileManager(NavmeshManager manager)
    {
        _manager = manager;

        Service.ClientState.ZoneInit += OnZoneInit;

        Customization = NavmeshCustomizationRegistry.ForTerritory(Service.ClientState.TerritoryType);
        Service.Config.Modified += OnConfigModified;
        OnConfigModified();

        Task.Run(async () =>
        {
            await Service.Framework.Run(() => Scene.Initialize());
            foreach (var t in Scene.GetTileChanges())
                QueueTile(t, true, 0);
            _initialized = true;
        });
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= OnZoneInit;
        Scene.Dispose();

        foreach (var t in Tasks)
            t?.Cancel();
    }

    public void Update()
    {
        // wait for first scene load
        if (!_initialized)
            return;

        foreach (var t in Scene.GetTileChanges())
            QueueTile(t, true, DebounceMs);
    }

    private void OnConfigModified()
    {
        var oldThreads = Concurrency;

        var maxThreads = Environment.ProcessorCount;
        var wantedThreads = Service.Config.BuildMaxCores;
        if (wantedThreads <= 0)
            Concurrency = maxThreads + wantedThreads;
        else
            Concurrency = wantedThreads;
        Concurrency = Math.Clamp(Concurrency, 1, maxThreads);

        var deltaThreads = Concurrency - oldThreads;
        if (deltaThreads > 0)
            _sem.Release(deltaThreads);
        else if (deltaThreads < 0)
            Task.Run(async () =>
            {
                for (var i = 0; i < -deltaThreads; i++)
                    await _sem.WaitAsync();
            });
    }

    private void OnZoneInit(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        Customization = NavmeshCustomizationRegistry.ForTerritory(obj.TerritoryType.RowId);
    }

    private void RecreateMesh()
    {
        // recreate mesh/volume
        Mesh = new(new()
        {
            orig = BoundsMin.SystemToRecast(),
            tileWidth = (BoundsMax.X - BoundsMin.X) / Scene.RowLength,
            tileHeight = (BoundsMax.Z - BoundsMin.Z) / Scene.RowLength,
            maxTiles = Scene.NumTiles,
            maxPolys = 1 << DtNavMesh.DT_POLY_BITS
        }, 6);
        Volume = new(BoundsMin, BoundsMax, Customization.Settings.NumTiles);
    }

    public void Rebuild()
    {
        RecreateMesh();

        foreach (var tile in Scene.GetAllTiles())
            QueueTile(tile, false, 0);
    }

    private void QueueTile(SceneTracker.Tile tile, bool allowCache, double debounce)
    {
        Tasks[tile.X, tile.Z]?.Cancel();
        Tasks[tile.X, tile.Z] = BuildTask.Spawn(async tok =>
        {
            if (debounce > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(debounce), tok);
            await BuildTile(tile, allowCache, tok);
        });
    }

    private Exception? _exc;

    private async Task BuildTile(SceneTracker.Tile data, bool allowCache, CancellationToken token)
    {
        Interlocked.Add(ref NumTasks, 1);
        var x = data.X;
        var z = data.Z;

        try
        {
            await _sem.WaitAsync(token);
            try
            {
                var (tile, vox) = LoadOrBuildTile(data, allowCache, token);
                if (Mesh != null)
                {
                    lock (locked)
                    {
                        if (tile != null)
                            Mesh.UpdateTile(tile, 0);
                        if (vox != null && Volume != null)
                            MergeTile(Volume, x, z, vox);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    _exc = ex;
                    Log(ex, $"while building tile {x}x{z}");
                }
                throw;
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

        if (NumTasks == 0 && Mesh != null)
        {
            if (_exc != null)
            {
                Log(_exc, "a task failed, mesh will be incomplete");
                _exc = null;
                return;
            }
            Log("all tiles done, replacing mesh");
            Customization.CustomizeMesh(Mesh, Scene.FestivalLayers);
            _manager.ReplaceMesh(new(Customization.Version, Mesh, Volume));
        }
    }

    private static void MergeTile(VoxelMap parent, int x, int z, VoxelMap child)
    {
        var subdivisionShift = parent.RootTile.Subdivision.Count;

        for (ushort i = 0; i < child.RootTile.Contents.Length; i++)
        {
            var contents = child.RootTile.Contents[i];

            if ((contents & VoxelMap.VoxelOccupiedBit) == 0)
                continue; // empty

            // TODO: almost positive this isn't necessary
            //var v = child.RootTile.LevelDesc.IndexToVoxel(i);
            //if (v.x != x || v.z != z)
            //    continue;

            if ((contents & VoxelMap.VoxelIdMask) != VoxelMap.VoxelIdMask)
                contents += (ushort)subdivisionShift;

            parent.RootTile.Contents[i] = contents;
        }
        parent.RootTile.Subdivision.AddRange(child.RootTile.Subdivision);
    }

    private (DtMeshData?, VoxelMap?) LoadOrBuildTile(SceneTracker.Tile data, bool allowCache, CancellationToken token)
    {
        var customization = NavmeshCustomizationRegistry.ForTerritory(Scene.Territory);

        var cacheKey = SceneTracker.GetHashFromKeys(data.Keys);

        var x = data.X;
        var z = data.Z;
        var dir = new DirectoryInfo($"{_manager.CacheDir}/z{Scene.Territory}");
        var cacheFile = new FileInfo(dir.FullName + $"/{x:d2}-{z:d2}-{cacheKey}");

        try
        {
            if (cacheFile.Exists && allowCache)
            {
                using var stream = cacheFile.OpenRead();
                using var reader = new BinaryReader(stream);
                return Navmesh.DeserializeSingleTile(reader, customization.Version);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "unable to load tile from cache");
        }

        var builder = new TileBuilder(data, customization, customization.IsFlyingSupported(Service.ClientState.TerritoryType));
        var (tile, vox, _) = builder.Build(token);

        VoxelMap? map = null;
        if (vox != null)
        {
            map = new(BoundsMin, BoundsMax, Customization.Settings.NumTiles);
            map.Build(vox, x, z);
        }

        token.ThrowIfCancellationRequested();

        {
            if (!dir.Exists)
                dir.Create();

            using var wstream = cacheFile.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(wstream);
            Navmesh.SerializeSingleTile(writer, tile, map, customization.Version);
        }

        return (tile, map);
    }

    public static void Log(string message) => Service.Log.Debug($"[TileManager] [{Environment.CurrentManagedThreadId,4}] {message}");
    private static void Log(Exception ex, string message) => Service.Log.Warning(ex, $"[TileManager] [{Environment.CurrentManagedThreadId,4}] {message}");
}

public sealed class TileBuilder
{
    public RcContext Telemetry = new();
    public NavmeshSettings Settings;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int NumTilesX;
    public int NumTilesZ;

    private readonly SceneTracker.Tile Tile;
    public int TileX => Tile.X;
    public int TileZ => Tile.Z;

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

    public TileBuilder(SceneTracker.Tile data, NavmeshCustomization customization, bool flyable)
    {
        Tile = data;

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

    public (DtMeshData?, Voxelizer?, RcBuilderResult) Build(CancellationToken token)
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

        rasterizer.RasterizeFlat(Tile.Objects.Select(o => (o.Mesh, o.Instance)), SceneExtractor.MeshType.FileMesh | SceneExtractor.MeshType.CylinderMesh | SceneExtractor.MeshType.AnalyticShape, true, true, token);
        rasterizer.RasterizeFlat(Tile.Objects.Select(o => (o.Mesh, o.Instance)), SceneExtractor.MeshType.Terrain | SceneExtractor.MeshType.AnalyticPlane, false, true, token);

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

        token.ThrowIfCancellationRequested();

        TileManager.Log($"built navmesh tile {TileX}x{TileZ} in {timer.Value().TotalMilliseconds}ms");

        return (meshData, vox, new RcBuilderResult(TileX, TileZ, shf, chf, cset, pmesh, dmesh, Telemetry));
    }
}
