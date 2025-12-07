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

public class NavmeshDebug
{
    public bool Enabled = false;
    public record class IntermediatesData(Tile Tile, RcBuilderResult Data);
    public IntermediatesData?[,] Intermediates { get; private set; } = new IntermediatesData?[16, 16];
    public Task?[,] BuildTasks = new Task?[16, 16];

    public void Reset()
    {
        for (var i = 0; i < Intermediates.GetLength(0); i++)
            for (var j = 0; j < Intermediates.GetLength(1); j++)
            {
                Intermediates[i, j] = null;
                BuildTasks[i, j] = null;
            }
    }
}

struct BuildProgress()
{
    private int _numStarted;
    private int _numFinished;
    public bool Pending { get; private set; }
    private DateTime[,] _mostRecentTaskStart = new DateTime[16, 16];
    private readonly Lock _lock = new();

    public (int NumStarted, DateTime SpawnTime) Start(IList<Tile> tiles)
    {
        lock (_lock)
        {
            var dt = DateTime.Now;
            foreach (var t in tiles)
                _mostRecentTaskStart[t.X, t.Z] = dt;

            Pending = false;
            _numStarted += tiles.Count;
            return (_numStarted, dt);
        }
    }

    public int Finish(int count)
    {
        lock (_lock)
        {
            Pending = false;
            _numFinished += count;
            return _numFinished;
        }
    }

    public readonly DateTime MostRecentTaskStart(int x, int z)
    {
        lock (_lock)
        {
            return _mostRecentTaskStart[x, z];
        }
    }

    public void SetPending()
    {
        if (_numStarted == 0)
            Pending = true;
    }
    public void Clear() { _numStarted = _numFinished = 0; Pending = false; _mostRecentTaskStart = new DateTime[16, 16]; }
    public readonly bool IsFinished => _numFinished >= _numStarted;
    public readonly float Progress => Pending ? 0 : _numStarted > 0 ? (float)_numFinished / _numStarted : -1;
}

// manager that loads navmesh matching current zone and performs async pathfinding queries
public sealed partial class NavmeshManager : IDisposable
{
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;
    public bool SeedMode = false;

    public static readonly Vector3 BoundsMin = new(-1024);
    public static readonly Vector3 BoundsMax = new(1024);

    public Navmesh? Navmesh { get; private set; }
    public NavmeshQuery? Query { get; private set; }
    public event Action<Navmesh?, NavmeshQuery?> NavmeshChanged = delegate { };
    public event Action<uint> TerritoryChanged = delegate { };

    public Exception? LastError;
    private bool _shouldRestart;

    public DtNavMesh? Mesh;
    public VoxelMap? Volume;

    public float LoadTaskProgress => _buildProgress[Service.ClientState.TerritoryType].Progress; // negative if load task is not running, otherwise in [0, 1] range

    private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

    private int _numActivePathfinds;
    public bool PathfindInProgress => _numActivePathfinds > 0;
    public int NumQueuedPathfindRequests => _numActivePathfinds > 0 ? _numActivePathfinds - 1 : 0;

    private readonly BuildProgress[] _buildProgress;

    public DirectoryInfo CacheDir { get; private set; }

    public readonly LayoutObjectSet Scene = new();
    internal TileSet? Grid;
    private IDisposable? _tileSubscription;

    private readonly CancellationTokenSource _taskSrc = new();
    private CancellationTokenSource? _queryToken = new();

    public NavmeshCustomization Customization => Scene.Customization;
    public uint[] ActiveFestivals { get; private set; } = new uint[4];

    private int Concurrency;
    private readonly Lock meshLock = new();
    private readonly SemaphoreSlim _sem = new(0);

    private bool _enableCache = true;

    public NavmeshDebug DebugData = new();

    public NavmeshManager(DirectoryInfo cacheDir)
    {
        CacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists

        Service.Config.Modified += OnConfigModified;
        OnConfigModified();

        Scene.ZoneChanged += OnZoneChanged;
        //Scene.PauseActions = true;

        _lastLoadQueryTask = Service.Framework.Run(InitGrid);
        _buildProgress = new BuildProgress[2048];
        Array.Fill(_buildProgress, new());
    }

    public void Dispose()
    {
        Log("Disposing");
        _taskSrc.Cancel();
        _tileSubscription?.Dispose();
        Grid?.Dispose();
        Scene.Dispose();
        ClearState();
    }

    public unsafe void Update()
    {
        if (Service.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return;

        if (SeedMode)
            RunSeedMode();
    }

    public List<uint> GetActiveFestivals()
    {
        unsafe
        {
            var w = LayoutWorld.Instance()->ActiveLayout;
            if (w == null)
                return [0, 0, 0, 0];
            if (w->FestivalStatus is > 0 and < 5)
                return [0, 0, 0, 0];

            return MemoryMarshal.Cast<GameMain.Festival, uint>(w->ActiveFestivals).ToArray().ToList();
        }
    }

    public void OnZoneChanged(LayoutObjectSet scene)
    {
        ClearState();
        Array.Fill<uint>(ActiveFestivals, 0);
        DebugData.Reset();
        _buildProgress[scene.LastLoadedZone].SetPending();
        Mesh = new(new()
        {
            orig = BoundsMin.SystemToRecast(),
            tileWidth = (BoundsMax.X - BoundsMin.X) / Scene.RowLength,
            tileHeight = (BoundsMax.Z - BoundsMin.Z) / Scene.RowLength,
            maxTiles = Scene.NumTiles,
            maxPolys = 1 << DtNavMesh.DT_POLY_BITS
        }, 6);
        Volume = new(BoundsMin, BoundsMax, Customization.Settings.NumTiles);
        TerritoryChanged.Invoke(scene.LastLoadedZone);

        if (_shouldRestart)
        {
            _shouldRestart = false;
            InitGrid();
        }
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

    public void InitGrid()
    {
        Log("Initializing tile watch");
        _tileSubscription?.Dispose();
        Grid?.Dispose();
        Grid = new();

        // update object list whenever scene changes
        Grid.Watch(Scene);

        // trigger rebuilds when updates stop
        _tileSubscription = Grid.BatchedWithTimeout(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30))
            .SelectMany(changes => Observable.FromAsync(async () =>
            {
                var fest = GetActiveFestivals();
                var terr = changes[0].Territory;

                // ensure that the most recently STARTED task is the one whose output is added to the mesh
                // consider the case where a zone has already been built, and the player triggers a cutscene that loads the same zone with a lot of added or removed objects, then ends the cutscene quickly and returns to the normal zone; the normal zone tiles will be loaded from cache before the useless cutscene tiles are built from scratch
                // we do try to pause the object watcher in cutscenes, but the condition flags are not always reliable
                var (_, spawnTime) = _buildProgress[terr].Start(changes);

                List<Task> tiles = [];
                var enableCache = _enableCache;
                foreach (var t in changes)
                {
                    Slog.LogGeneric("tile-start", (int)terr, t.X, t.Z, string.Join(" ", t.Objects.Keys.Select(k => k.ToString("X16"))));
                    var spawned =
                        Task.Run(async () => await BuildTile(t, enableCache, spawnTime, _taskSrc.Token), _taskSrc.Token)
                            .ContinueWith(_ =>
                            {
                                Slog.LogGeneric("tile-end", (int)terr, t.X, t.Z);
                                _buildProgress[terr].Finish(1);
                            });
                    DebugData.BuildTasks[t.X, t.Z] = spawned;
                    tiles.Add(spawned);
                }

                _enableCache = true;

                await Task.WhenAll(tiles);
                if (!_buildProgress[terr].IsFinished)
                    return;

                if (Mesh != null && terr == Service.ClientState.TerritoryType)
                {
                    _queryToken?.Cancel();
                    _queryToken = new();
                    Customization.CustomizeMesh(Mesh, fest);
                    Navmesh = new(Customization.Version, Mesh, Volume);
                    Query = new(Navmesh);

                    var ff = await FloodFill.GetAsync();
                    if (ff?.Seeds.TryGetValue(terr, out var ss) == true)
                        Prune(ss.Select(s => (Vector3)s));

                    NavmeshChanged.Invoke(Navmesh, Query);
                }

                _buildProgress[terr].Clear();
            }))
            .Subscribe(_ =>
            {
                Log("tile batch completed");
            }, ex =>
            {
                LastError = ex;
                _shouldRestart = true;
                Log(ex, $"tile batch failed; last change event was {Grid.LastEvent}");
            });
    }

    public bool Reload(bool allowLoadFromCache)
    {
        _enableCache = allowLoadFromCache;
        ClearState();
        InitGrid();
        return true;
    }

    internal void RebuildTile(int x, int z)
    {
        _enableCache = false;
        Grid?.Reload(Scene.LastLoadedZone, x, z);
    }

    private bool _seeding;

    private void RunSeedMode()
    {
        if (_seeding)
            return;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        if (Service.Condition.Any(ConditionFlag.InFlight, ConditionFlag.Diving, ConditionFlag.BetweenAreas51, ConditionFlag.Jumping, ConditionFlag.Mounted) || Service.ClientState.TerritoryType == 0)
            return;

        if (Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(Service.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId != 1)
            return;

        if (Navmesh == null || Query == null || LoadTaskProgress >= 0)
            return;

        var pos = player.Position;
        var playerPoly = Query.FindNearestMeshPoly(pos);
        if (playerPoly == 0)
        {
            _seeding = true;
            ExecuteWhenIdle(async () =>
            {
                var ff = await FloodFill.GetAsync();
                if (ff != null)
                {
                    ff.AddPoint(Service.ClientState.TerritoryType, pos);
                    await ff.Serialize();
                    Reload(true);
                }
            }, default);
        }
    }

    private async Task BuildTile(Tile data, bool allowCache, DateTime spawnTime, CancellationToken token)
    {
        var x = data.X;
        var z = data.Z;

        await _sem.WaitAsync(token);
        try
        {
            var (tile, vox) = LoadOrBuildTile(data, allowCache, token);

            // another task has been spawned, but it may have already finished, i.e. if it was entirely loaded from cache
            if (_buildProgress[data.Territory].MostRecentTaskStart(data.X, data.Z) > spawnTime)
                return;

            // can't add tile to current mesh
            if (Mesh == null || data.Territory != Scene.LastLoadedZone)
                return;

            lock (meshLock)
            {
                if (tile != null)
                    Mesh.UpdateTile(tile, 0);
                if (vox != null && Volume != null)
                    MergeTile(Volume, x, z, vox);
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

        var bgStr = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(data.Territory)?.Bg.ToString().Replace('/', '_') ?? "__none__";

        var x = data.X;
        var z = data.Z;
        var dir = new DirectoryInfo($"{CacheDir}/{bgStr}");
        var cacheFile = new FileInfo(dir.FullName + $"/{x:d2}-{z:d2}-{cacheKey}.tile");

        if (allowCache)
        {
            try
            {
                if (cacheFile.Exists)
                {
                    using var stream = cacheFile.OpenRead();
                    using var reader = new BinaryReader(stream);
                    var tile2 = Navmesh.DeserializeSingleTile(reader, customization.Version);
                    Log($"loaded tile {data.X}x{data.Z} from cache");
                    return tile2;
                }
                else if (dir.Exists)
                    ShowDiff(data, dir);
            }
            catch (Exception ex)
            {
                Log(ex, "unable to load tile from cache");
            }
        }

        var builder = new NavmeshBuilder(data, customization, customization.IsFlyingSupported(data.Territory));
        var (tile, vox, intermediates) = builder.Build(token);

        if (DebugData.Enabled)
            DebugData.Intermediates[x, z] = new(data, intermediates);

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

    public Task<List<Vector3>> QueryPath(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default)
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

    public void CancelAll()
    {
        if (_queryToken == null)
            return;

        var cts = _queryToken;
        _queryToken = null;
        cts.Cancel();
        ExecuteWhenIdle(() =>
        {
            _numActivePathfinds = 0;
            cts.Dispose();
            TerritoryChanged.Invoke(Scene.LastLoadedZone);
            _queryToken = new();
        }, default);
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
            if (Service.PluginInterface.IsDev)
                FloodFill.Clear();
            _numActivePathfinds = 0;
            _seeding = false;
            cts.Dispose();
            NavmeshChanged.Invoke(null, null);
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
    private static void LogV(string message) => Service.Log.Verbose($"[NavmeshManager] [{Environment.CurrentManagedThreadId,4}] {message}");
    private static void Log(Exception? ex, string message) => Service.Log.Warning(ex, $"[TileManager] [{Environment.CurrentManagedThreadId,4}] {message}");
    private static void LogTaskError(Task task)
    {
        if (task.IsFaulted)
            Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
    }

    private static void SerializeTile(FileInfo cacheFile, DtMeshData? tile, VoxelMap? map, int version, Tile data)
    {
        try
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
                    writer.WriteLine($"{key:X16} {tileobj.Mesh.Path} {(int)tileobj.Instance.WorldBounds.Min.X} {(int)tileobj.Instance.WorldBounds.Min.Y} {(int)tileobj.Instance.WorldBounds.Min.Z} {(int)tileobj.Instance.WorldBounds.Max.X} {(int)tileobj.Instance.WorldBounds.Max.Y} {(int)tileobj.Instance.WorldBounds.Max.Z}");
            }
        }
        catch (IOException ex)
        {
            Log(ex, "Unable to save cache");
        }
    }

    private static void ShowDiff(Tile t, DirectoryInfo cacheDir)
    {
        var tileKeys = t.Objects.Select(kv => (kv.Key, kv.Value.Mesh.Path)).ToList();

        foreach (var listfile in cacheDir.GetFiles($"{t.X:d2}-{t.Z:d2}-*.txt"))
        {
            List<(ulong, string)> keys = [];

            using var stream = listfile.OpenRead();
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(' ', 2);
                keys.Add((ulong.Parse(parts[0], System.Globalization.NumberStyles.HexNumber), parts[1]));
            }
            LogV($"testing tile contents against {listfile.Name}");
            CompareLists(tileKeys, keys);
        }
    }

    private static void CompareLists(List<(ulong, string)> sourceKeys, List<(ulong, string)> cacheKeys)
    {
        int i = 0, j = 0;

        while (true)
        {
            if (i >= sourceKeys.Count)
            {
                for (; j < cacheKeys.Count; j++)
                {
                    LogV($"trailing item in cache: {cacheKeys[j].Item1:X16} {cacheKeys[j].Item2}");
                }
                return;
            }
            if (j >= cacheKeys.Count)
            {
                for (; i < sourceKeys.Count; i++)
                {
                    LogV($"trailing item in tile: {sourceKeys[i].Item1:X16} {sourceKeys[i].Item2}");
                }
                return;
            }

            var (k1, path1) = sourceKeys[i];
            var (k2, path2) = cacheKeys[j];
            if (k1 == k2)
            {
                i++;
                j++;
            }
            else if (k1 < k2)
            {
                LogV($"missing from cache: {k1:X16} {path1}");
                i++;
            }
            else if (k1 > k2)
            {
                LogV($"missing from tile: {k2:X16} {path2}");
                j++;
            }
        }
    }
}
