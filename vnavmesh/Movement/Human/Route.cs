using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Navmesh.Movement.Human;

// a recording assignment: get from A to B in a specific zone; see ADR 0003
public class Route
{
    public string Id = "";
    public uint Territory;
    public Vector3 A;
    public Vector3 B;
    public bool Fly;
    public string Notes = "";

    [JsonIgnore] public float Length => Vector3.Distance(A, B);
}

// persistent set of routes, authored in-game
public class RouteBook
{
    public List<Route> Routes = [];

    private readonly FileInfo _file;

    public RouteBook(string configDir)
    {
        _file = new FileInfo(Path.Combine(configDir, "human-routes.json"));
        Load();
    }

    public IEnumerable<Route> ForTerritory(uint territory) => Routes.Where(r => r.Territory == territory);

    // ids only need to be unique within a territory, since captures are filed per territory
    public string SuggestId(uint territory)
    {
        var used = ForTerritory(territory).Select(r => r.Id).ToHashSet();
        for (int i = 1; ; ++i)
        {
            var candidate = $"route{i:d3}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    public void Add(Route route)
    {
        Routes.Add(route);
        Save();
    }

    public void Remove(Route route)
    {
        Routes.Remove(route);
        Save();
    }

    public void Save()
    {
        try
        {
            _file.Directory?.Create();
            File.WriteAllText(_file.FullName, JsonConvert.SerializeObject(Routes, Formatting.Indented));
        }
        catch (Exception e)
        {
            Service.Log.Error($"[recorder] failed to save routes to {_file.FullName}: {e}");
        }
    }

    private void Load()
    {
        if (!_file.Exists)
            return;

        try
        {
            var parsed = JArray.Parse(File.ReadAllText(_file.FullName)).ToObject<List<Route>>();
            if (parsed != null)
                Routes = parsed;
            Service.Log.Debug($"[recorder] loaded {Routes.Count} routes");
        }
        catch (Exception e)
        {
            Service.Log.Error($"[recorder] failed to load routes from {_file.FullName}: {e}");
        }
    }
}
