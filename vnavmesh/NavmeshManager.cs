using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public class NavmeshManager : IDisposable
{
    public bool AutoLoad = true; // whether we load/build mesh automatically when changing zone
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;
    public Navmesh? Navmesh => _navmesh;
    public NavmeshQuery? Query => _query;
    public float LoadTaskProgress => _loadTask != null ? _loadTaskProgress : -1; // returns negative value if task is not running
    public string CurrentKey => _lastKey;

    private DirectoryInfo _cacheDir;
    private NavmeshSettings _settings = new();
    private string _lastKey = "";
    private Task<Navmesh>? _loadTask;
    private volatile float _loadTaskProgress;
    private Navmesh? _navmesh;
    private NavmeshQuery? _query;
    private CancellationTokenSource? _queryCancelSource;

    public NavmeshManager(DirectoryInfo cacheDir)
    {
        _cacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists
    }

    public void Dispose()
    {
        if (_loadTask != null)
        {
            if (!_loadTask.IsCompleted)
                _loadTask.Wait();
            _loadTask.Dispose();
            _loadTask = null;
        }
        ClearState();
    }

    public void Update()
    {
        if (_loadTask != null)
        {
            if (!_loadTask.IsCompleted)
                return; // async task is still in progress, do nothing; note that we don't want to start multiple concurrent tasks on rapid transitions

            Service.Log.Information($"Finishing transition to '{_lastKey}'");
            try
            {
                _navmesh = _loadTask.Result;
                _query = new(_navmesh);
                _queryCancelSource = new();
                OnNavmeshChanged?.Invoke(_navmesh, _query);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"Failed to build navmesh: {ex}");
            }
            _loadTask.Dispose();
            _loadTask = null;
        }

        var curKey = GetCurrentKey();
        if (curKey == _lastKey)
            return; // everything up-to-date

        if (!AutoLoad)
        {
            if (_lastKey.Length == 0)
                return; // nothing is loaded, and auto-load is forbidden
            curKey = ""; // just unload existing mesh
        }

        Service.Log.Info($"Starting transition from '{_lastKey}' to '{curKey}'");
        _lastKey = curKey;
        Reload(true);
    }

    public bool Reload(bool allowLoadFromCache)
    {
        if (_loadTask != null)
        {
            Service.Log.Error($"Can't initiate reload - another task is already in progress");
            return false; // some task is already in progress...
        }

        ClearState();
        if (_lastKey.Length > 0)
        {
            var scene = new SceneDefinition();
            scene.FillFromActiveLayout();
            var cacheKey = GetCacheKey(scene);
            _loadTaskProgress = 0;
            _loadTask = Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache));
        }
        return true;
    }

    public Task<List<Vector3>>? QueryPath(Vector3 from, Vector3 to, bool flying)
    {
        var query = _query;
        if (_queryCancelSource == null || query == null)
        {
            Service.Log.Error($"Can't initiate query - navmesh is not loaded");
            return null;
        }

        var token = _queryCancelSource.Token;
        var task = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return flying ? query.PathfindVolume(from, to, UseRaycasts, UseStringPulling, token) : query.PathfindMesh(from, to, UseRaycasts, UseStringPulling, token);
        });
        return task;
    }

    // if non-empty string is returned, active layout is ready
    private unsafe string GetCurrentKey()
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return ""; // layout not ready

        var terrRow = Service.LuminaRow<Lumina.Excel.GeneratedSheets.TerritoryType>(layout->TerritoryTypeId);
        if (terrRow == null)
            return ""; // layout doesn't belong to a valid zone

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        return $"{terrRow.Bg}//{filterKey:X}//{layout->ActiveFestivals[0]:X}.{layout->ActiveFestivals[1]:X}.{layout->ActiveFestivals[2]:X}.{layout->ActiveFestivals[3]:X}";
    }

    private unsafe string GetCacheKey(SceneDefinition scene)
    {
        // note: festivals are active globally, but majority of zones don't have festival-specific layers, so we only want real ones in the cache key
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var terrRow = Service.LuminaRow<Lumina.Excel.GeneratedSheets.TerritoryType>(layout->TerritoryTypeId)!;
        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        return $"{terrRow.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{string.Join('.', scene.FestivalLayers.Select(id => id.ToString("X")))}";
    }

    private void ClearState()
    {
        _queryCancelSource?.Cancel();
        //_queryCancelSource?.Dispose(); - i don't think it's safe to call dispose at this point...
        _queryCancelSource = null;

        OnNavmeshChanged?.Invoke(null, null);
        _query = null;
        _navmesh = null;
    }

    private Navmesh BuildNavmesh(SceneDefinition scene, string cacheKey, bool allowLoadFromCache)
    {
        // try reading from cache
        var cache = new FileInfo($"{_cacheDir.FullName}/{cacheKey}.navmesh");
        if (allowLoadFromCache && cache.Exists)
        {
            try
            {
                Service.Log.Debug($"Loading cache: {cache.FullName}");
                using var stream = cache.OpenRead();
                using var reader = new BinaryReader(stream);
                return Navmesh.Deserialize(reader, _settings);
            }
            catch (Exception ex)
            {
                Service.Log.Debug($"Failed to load cache: {ex}");
            }
        }

        // cache doesn't exist or can't be used for whatever reason - build navmesh from scratch
        // TODO: we can build multiple tiles concurrently
        var builder = new NavmeshBuilder(scene, _settings);
        var deltaProgress = 1.0f / (builder.NumTilesX * builder.NumTilesZ);
        for (int z = 0; z < builder.NumTilesZ; ++z)
        {
            for (int x = 0; x < builder.NumTilesX; ++x)
            {
                builder.BuildTile(x, z);
                _loadTaskProgress += deltaProgress;
            }
        }

        // write results to cache
        {
            Service.Log.Debug($"Writing cache: {cache.FullName}");
            using var stream = cache.OpenWrite();
            using var writer = new BinaryWriter(stream);
            builder.Navmesh.Serialize(writer);
        }
        return builder.Navmesh;
    }
}
