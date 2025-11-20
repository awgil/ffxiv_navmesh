using Dalamud.Game.ClientState.Conditions;
using DotRecast.Detour;
using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public sealed partial class NavmeshManager : IDisposable
{
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;
    public bool SeedMode = Service.PluginInterface.IsDev;

    public static readonly Vector3 BoundsMin = new(-1024);
    public static readonly Vector3 BoundsMax = new(1024);

    public Navmesh? Navmesh { get; private set; }
    public NavmeshQuery? Query { get; private set; }
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;

    public DtNavMesh? Mesh;
    public VoxelMap? Volume;

    private volatile float _loadTaskProgress = -1;
    public float LoadTaskProgress => _loadTaskProgress; // negative if load task is not running, otherwise in [0, 1] range

    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    private int _numActivePathfinds;
    public bool PathfindInProgress => _numActivePathfinds > 0;
    public int NumQueuedPathfindRequests => _numActivePathfinds > 0 ? _numActivePathfinds - 1 : 0;

    private volatile int _numStarted;
    private volatile int _numFinished;

    public DirectoryInfo CacheDir { get; private set; }

    public readonly ColliderSet Scene = new();
    private readonly Grid Grid = new();

    private readonly CancellationTokenSource _taskSrc = new();
    private CancellationTokenSource? _queryToken = new();

    public NavmeshCustomization Customization => Scene.Customization;
    public uint[] ActiveFestivals { get; private set; } = new uint[4];

    public bool TrackIntermediates;
    public RcBuilderResult?[,] Intermediates { get; private set; } = new RcBuilderResult?[0, 0];

    private int Concurrency;
    private readonly Lock meshLock = new();
    private readonly SemaphoreSlim _sem = new(0);

    private bool _initialized;
    private bool _enableCache = true;

    public NavmeshManager(DirectoryInfo cacheDir)
    {
        CacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists

        Service.Config.Modified += OnConfigModified;
        OnConfigModified();

        Scene.ZoneChanged += OnZoneChange;

        // update object list whenever scene changes
        Grid.Watch(Scene, _taskSrc.Token);

        // trigger rebuilds when updates stop
        Grid.Debounced(TimeSpan.FromMilliseconds(500))
            .ForEachAsync(async changes =>
            {
                Interlocked.Add(ref _numStarted, changes.Count);
                List<Task> tiles = [];
                var enableCache = _enableCache;
                foreach (var tile in changes)
                {
                    var t = new Tile(tile.X, tile.Z, new SortedDictionary<ulong, InstanceWithMesh>(tile.Objects.ToDictionary(k => k.Key, k => k.Value with { Instance = k.Value.Instance.Clone() })), Customization, Scene.LastLoadedZone);
                    tiles.Add(
                        Task.Run(async () => await BuildTile(t, enableCache, _taskSrc.Token), _taskSrc.Token)
                            .ContinueWith(_ =>
                            {
                                Interlocked.Add(ref _numFinished, 1);
                                _loadTaskProgress = (float)_numFinished / _numStarted;
                            })
                    );
                }

                await Task.WhenAll(tiles);
                if (_numFinished < _numStarted)
                    return;

                if (Mesh != null)
                {
                    _queryToken?.Cancel();
                    _queryToken = new();
                    Customization.CustomizeMesh(Mesh, [.. ActiveFestivals]);
                    Navmesh = new(Customization.Version, Mesh, Volume);
                    Query = new(Navmesh);

                    var ff = await FloodFill.GetAsync();
                    if (ff.Seeds.TryGetValue(Scene.LastLoadedZone, out var ss))
                        Prune(ss.Select(s => (Vector3)s));

                    OnNavmeshChanged?.Invoke(Navmesh, Query);
                }

                _numFinished = _numStarted = 0;
                _loadTaskProgress = -1;
                _enableCache = true;
            }, _taskSrc.Token);

        // prepare a task with correct task scheduler that other tasks can be chained off
        _lastLoadQueryTask = Task.Run(async () =>
        {
            // fire change events for all existing layout objects when the plugin loads
            await Service.Framework.Run(() => Scene.Initialize());

            _initialized = true;

            Log("Tasks kicked off");
        });
    }

    public void Dispose()
    {
        Log("Disposing");
        _taskSrc.Cancel();
        Grid.Dispose();
        Scene.Dispose();
        ClearState();
    }

    public void Update()
    {
        if (!_initialized || Service.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return;

        Scene.Poll();

        unsafe
        {
            var w = LayoutWorld.Instance()->ActiveLayout;
            if (w == null)
                return;
            if (w->FestivalStatus is > 0 and < 5)
                return;

            MemoryMarshal.Cast<GameMain.Festival, uint>(w->ActiveFestivals).CopyTo(ActiveFestivals);
        }

        if (SeedMode)
            RunSeedMode();
    }

    public void OnZoneChange(ColliderSet scene)
    {
        ClearState();
        Array.Fill<uint>(ActiveFestivals, 0);
        Intermediates = new RcBuilderResult?[scene.RowLength, scene.RowLength];
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

    public bool Reload(bool allowLoadFromCache)
    {
        _enableCache = allowLoadFromCache;
        ClearState();
        Scene.Initialize();
        return true;
    }

    private bool _seeding;

    private void RunSeedMode()
    {
        if (_seeding)
            return;

        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;

        if (Service.Condition.Any(ConditionFlag.InFlight, ConditionFlag.Diving, ConditionFlag.BetweenAreas51, ConditionFlag.Jumping, ConditionFlag.Mounted) || Service.ClientState.TerritoryType == 0)
            return;

        if (Navmesh == null || Query == null || _loadTaskProgress >= 0)
            return;

        var pos = player.Position;
        var playerPoly = Query.FindNearestMeshPoly(pos);
        if (playerPoly == 0)
        {
            _seeding = true;
            ExecuteWhenIdle(async () =>
            {
                var ff = await FloodFill.GetAsync();
                ff.AddPoint(Service.ClientState.TerritoryType, pos);
                await ff.Serialize();
                Reload(true);
            }, default);
        }
    }

    private async Task BuildTile(Tile data, bool allowCache, CancellationToken token)
    {
        var x = data.X;
        var z = data.Z;

        await _sem.WaitAsync(token);
        try
        {
            var (tile, vox) = LoadOrBuildTile(data, allowCache, token);

            // if tile zone doesn't match current zone, the player changed areas while we were building, so we don't modify the loaded mesh (tile will still be saved to cache)
            if (Mesh != null && data.Zone == Scene.LastLoadedZone)
            {
                lock (meshLock)
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
            if (ex is not (OperationCanceledException or CustomizationVersionMismatch))
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

    private Exception? _exc;

    private (DtMeshData?, VoxelMap?) LoadOrBuildTile(Tile data, bool allowCache, CancellationToken token)
    {
        var customization = data.Customization;
        customization.CustomizeTile(data);
        Log($"building tile {data.X}x{data.Z}");

        var cacheKey = data.GetCacheKey();

        var x = data.X;
        var z = data.Z;
        var dir = new DirectoryInfo($"{CacheDir}/z{data.Zone}");
        var cacheFile = new FileInfo(dir.FullName + $"/{x:d2}-{z:d2}-{cacheKey}.tile");

        try
        {
            if (cacheFile.Exists && allowCache)
            {
                using var stream = cacheFile.OpenRead();
                using var reader = new BinaryReader(stream);
                var tile2 = Navmesh.DeserializeSingleTile(reader, customization.Version);
                Log($"loaded tile {data.X}x{data.Z} from cache");
                return tile2;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "unable to load tile from cache");
        }

        var builder = new NavmeshBuilder(data, customization, customization.IsFlyingSupported(data.Zone));
        var (tile, vox, intermediates) = builder.Build(token);

        if (TrackIntermediates)
            Intermediates[x, z] = intermediates;

        VoxelMap? map = null;
        if (vox != null)
        {
            map = new(BoundsMin, BoundsMax, Customization.Settings.NumTiles);
            map.Build(vox, x, z, token);
        }

        if (!dir.Exists)
            dir.Create();

        SerializeTile(cacheFile, tile, map, customization.Version, data);

        token.ThrowIfCancellationRequested();

        return (tile, map);
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

    public Task<List<Vector3>> QueryPath(Vector3 from, Vector3 to, bool flying, CancellationToken externalCancel = default, float range = 0)
    {
        if (_queryToken == null)
            throw new Exception($"Can't initiate query - navmesh is not loaded");

        // task can be cancelled either by internal request (i.e. when navmesh is reloaded) or external
        var combined = CancellationTokenSource.CreateLinkedTokenSource(_queryToken.Token, externalCancel);
        ++_numActivePathfinds;
        return ExecuteWhenIdle(async cancel =>
        {
            using var autoDisposeCombined = combined;
            using var autoDecrementCounter = new OnDispose(() => --_numActivePathfinds);
            Log($"Kicking off pathfind from {from} to {to}");
            var path = await Task.Run(() =>
            {
                combined.Token.ThrowIfCancellationRequested();
                if (Query == null)
                    throw new Exception($"Can't pathfind, navmesh did not build successfully");
                Log($"Executing pathfind from {from} to {to}");
                return flying ? Query.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token) : Query.PathfindMesh(from, to, UseRaycasts, UseStringPulling, combined.Token, range);
            }, combined.Token);
            Log($"Pathfinding done: {path.Count} waypoints");
            return path;
        }, combined.Token);
    }

    // note: pixelSize should be power-of-2
    public (Vector3 min, Vector3 max) BuildBitmap(Vector3 startingPos, string filename, float pixelSize, AABB? mapBounds = null)
    {
        if (Navmesh == null || Query == null)
            throw new InvalidOperationException($"Can't build bitmap - navmesh creation is in progress");

        bool inBounds(Vector3 vert) => mapBounds is not AABB aabb || vert.X >= aabb.Min.X && vert.Y >= aabb.Min.Y && vert.Z >= aabb.Min.Z && vert.X <= aabb.Max.X && vert.Y <= aabb.Max.Y && vert.Z <= aabb.Max.Z;

        var startPoly = Query.FindNearestMeshPoly(startingPos);
        var reachablePolys = Query.FindReachableMeshPolys(startPoly);

        HashSet<long> polysInbounds = [];

        Vector3 min = new(1024), max = new(-1024);
        foreach (var p in reachablePolys)
        {
            Navmesh.Mesh.GetTileAndPolyByRefUnsafe(p, out var tile, out var poly);
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var v = NavmeshBitmap.GetVertex(tile, poly.verts[i]);
                if (!inBounds(v))
                    goto cont;

                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                //Service.Log.Debug($"{p:X}.{i}= {v}");
            }

            polysInbounds.Add(p);

        cont:;
        }
        //Service.Log.Debug($"bounds: {min}-{max}");

        var bitmap = new NavmeshBitmap(min, max, pixelSize);
        foreach (var p in polysInbounds)
        {
            bitmap.RasterizePolygon(Navmesh.Mesh, p);
        }
        bitmap.Save(filename);
        Service.Log.Debug($"Generated nav bitmap '{filename}' @ {startingPos}: {bitmap.MinBounds}-{bitmap.MaxBounds}");
        return (bitmap.MinBounds, bitmap.MaxBounds);
    }

    public void Prune(IEnumerable<Vector3> points)
    {
        if (Navmesh == null || Query == null)
            throw new InvalidOperationException("can't prune, mesh is missing");

        var startPolys = points.Select(pt => Query.FindNearestMeshPoly(pt));
        var reachablePolys = Query.FindReachableMeshPolys([.. startPolys]);

        var pruneCount = 0;
        for (var i = 0; i < Navmesh.Mesh.GetMaxTiles(); i++)
        {
            var t = Navmesh.Mesh.GetTile(i);
            if (t.data?.header == null)
                continue;

            var prBase = Navmesh.Mesh.GetPolyRefBase(t);
            for (var j = 0; j < t.data.header.polyCount; j++)
            {
                var pref = prBase | (uint)j;
                if (Navmesh.Mesh.GetPolyFlags(pref, out var fl).Failed())
                {
                    Log($"failed to fetch flags for {pref:X}");
                    continue;
                }
                if (reachablePolys.Contains(pref))
                {
                    if (Navmesh.Mesh.SetPolyFlags(pref, fl & ~Navmesh.FLAGS_DISABLED).Failed())
                        Log($"failed to set flags for {pref:X}");
                }
                else
                {
                    pruneCount++;
                    if (Navmesh.Mesh.SetPolyFlags(pref, fl | Navmesh.FLAGS_DISABLED).Failed())
                        Log($"failed to set flags for {pref:X}");
                }
            }
        }

        Log($"pruned {pruneCount} unreachable polygons");
    }

    private void ClearState()
    {
        if (_queryToken == null)
            return; // already cleared

        var cts = _queryToken;
        _queryToken = null;
        cts.Cancel();
        Log("Queueing state clear");
        ExecuteWhenIdle(() =>
        {
            Log("Clearing state");
            FloodFill.Clear();
            _numActivePathfinds = 0;
            _seeding = false;
            cts.Dispose();
            OnNavmeshChanged?.Invoke(null, null);
            Query = null;
            Navmesh = null;
        }, default);
    }

    private void ExecuteWhenIdle(Action task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        _lastLoadQueryTask = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            task();
        }, token);
    }

    private void ExecuteWhenIdle(Func<CancellationToken, Task> task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        _lastLoadQueryTask = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            var t = task(token);
            await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            LogTaskError(t);
        }, token);
    }

    private Task<T> ExecuteWhenIdle<T>(Func<CancellationToken, Task<T>> task, CancellationToken token)
    {
        var prev = _lastLoadQueryTask;
        var res = Service.Framework.Run(async () =>
        {
            await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            _ = prev.Exception;
            var t = task(token);
            await ((Task)t).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            LogTaskError(t);
            return t.Result;
        }, token);
        _lastLoadQueryTask = res;
        return res;
    }

    public static void Log(string message) => Service.Log.Debug($"[NavmeshManager] [{Environment.CurrentManagedThreadId,4}] {message}");
    private static void Log(Exception ex, string message) => Service.Log.Warning(ex, $"[TileManager] [{Environment.CurrentManagedThreadId,4}] {message}");
    private static void LogTaskError(Task task)
    {
        if (task.IsFaulted)
            Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
    }

    private static void SerializeTile(FileInfo cacheFile, DtMeshData? tile, VoxelMap? map, int version, Tile data)
    {
        using (var wstream = cacheFile.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new BinaryWriter(wstream);
            Navmesh.SerializeSingleTile(writer, tile, map, version);
        }

        var objfile = new FileInfo(Path.ChangeExtension(cacheFile.FullName, ".txt"));
        using (var wstream = objfile.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(wstream);
            foreach (var (key, tileobj) in data.Objects)
                writer.WriteLine($"{key:X16} {tileobj.Mesh.Path}");
        }
    }
}
