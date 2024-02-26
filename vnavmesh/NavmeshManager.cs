using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone
public class NavmeshManager : IDisposable
{
    public bool AutoLoad = true; // whether we load/build mesh automatically when changing zone
    public bool ShowDtrBar = true;
    public event Action<Navmesh?>? OnNavmeshChanged;
    public Navmesh? Navmesh => _navmesh;
    public float TaskProgress => _task != null ? _taskProgress : -1; // returns negative value if task is not running
    public string CurrentKey => _lastKey;

    private DirectoryInfo _cacheDir;
    private NavmeshSettings _settings = new();
    private string _lastKey = "";
    private Task<Navmesh>? _task;
    private volatile float _taskProgress;
    private Navmesh? _navmesh;
    private DtrBarEntry _dtrBarEntry;

    public NavmeshManager(DirectoryInfo cacheDir)
    {
        _cacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    public void Dispose()
    {
        if (_task != null)
        {
            if (!_task.IsCompleted)
                _task.Wait();
            _task.Dispose();
            _task = null;
        }
        ClearState();
    }

    public void Update()
    {
        if (_dtrBarEntry.Shown = ShowDtrBar)
        {
            if (TaskProgress >= 0)
                _dtrBarEntry.Text = new SeString(new TextPayload($"Mesh: {(int)(TaskProgress*100)}%"));
            else if (Navmesh != null)
                _dtrBarEntry.Text = new SeString(new TextPayload($"Mesh: Ready"));
            else
                _dtrBarEntry.Text = new SeString(new TextPayload($"Mesh: Not Ready"));
        }
        if (_task != null)
        {
            if (!_task.IsCompleted)
                return; // async task is still in progress, do nothing; note that we don't want to start multiple concurrent tasks on rapid transitions

            Service.Log.Information($"Finishing transition to '{_lastKey}'");
            try
            {
                _navmesh = _task.Result;
                OnNavmeshChanged?.Invoke(_navmesh);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"Failed to build navmesh: {ex}");
            }
            _task.Dispose();
            _task = null;
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
        if (_task != null)
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
            _taskProgress = 0;
            _task = Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache));
        }
        return true;
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
        OnNavmeshChanged?.Invoke(null);
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
                return Navmesh.Deserialize(reader);
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
                _taskProgress += deltaProgress;
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
