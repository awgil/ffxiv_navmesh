using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;

using Seeds = System.Collections.Generic.Dictionary<uint, System.Collections.Generic.List<Navmesh.JsonVec>>;

namespace Navmesh;

public record struct JsonVec(float X, float Y, float Z)
{
    public static implicit operator Vector3(JsonVec v) => new(v.X, v.Y, v.Z);
    public static implicit operator JsonVec(Vector3 v) => new(v.X, v.Y, v.Z);
}

public class FloodFill
{
    private static FloodFill? _instance;

    public Action<uint, Vector3> PointAdded = delegate { };

    public static readonly string LocalSource;
    public static readonly string RemoteSource = "https://raw.githubusercontent.com/awgil/ffxiv_navmesh/refs/heads/seeds/seeds.json";

    private readonly Seeds SeedsRemote;
    private readonly Seeds SeedsLocal;

    private FloodFill(Seeds remote, Seeds local)
    {
        SeedsRemote = remote;
        SeedsLocal = local;
    }

    static FloodFill()
    {
        var dir = new DirectoryInfo(Service.PluginInterface.GetPluginConfigDirectory());
        if (!dir.Exists)
            dir.Create();

        LocalSource = Path.Join(Service.PluginInterface.GetPluginConfigDirectory(), "seeds-local.json");
    }

    internal static void Clear() => _instance = null;

    public static FloodFill? Get() => _instance;

    public static async Task<FloodFill> GetAsync()
    {
        _instance ??= await Init();
        return _instance;
    }

    public void AddPoint(uint zone, Vector3 point)
    {
        SeedsLocal.TryAdd(zone, []);
        SeedsLocal[zone].Add(point);
        PointAdded.Invoke(zone, point);
    }

    public async Task Serialize()
    {
        var finfo = new FileInfo(LocalSource);
        using var st = finfo.Create();
        await JsonSerializer.SerializeAsync(st, new SortedDictionary<uint, List<JsonVec>>(SeedsLocal), serOpts);
    }

    private static async Task<FloodFill> Init()
    {
        Seeds remote = [];
        Seeds local = [];

        try
        {
            var client = new HttpClient();
            using HttpResponseMessage resp = await client.GetAsync(RemoteSource);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStreamAsync();
            remote = await FromStream(body);
        }
        catch (HttpRequestException ex)
        {
            Service.Log.Warning(ex, "Unable to fetch seeds from Github, quality will be lacking");
        }

        try
        {
            var finfo = new FileInfo(LocalSource);
            using var st = finfo.OpenRead();
            local = await FromStream(st);
        }
        catch (FileNotFoundException ex)
        {
            Service.Log.Info(ex, "Local seeds file not found");
        }

        foreach (var (zone, seedsRemote) in remote)
        {
            if (local.TryGetValue(zone, out var seedsLocal))
            {
                // cleanup local seeds when remote is updated
                seedsLocal.RemoveAll(l => seedsRemote.Any(r => Vector3.DistanceSquared(r, l) < 10));
            }
        }

        return new(remote, local);
    }

    private static readonly JsonSerializerOptions deOpts = new() { ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions serOpts = new()
    {
        WriteIndented = true,
        IndentSize = 2
    };

    private static async Task<Seeds> FromStream(Stream s)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<Seeds>(s, deOpts) ?? [];
        }
        catch (JsonException ex)
        {
            Service.Log.Warning(ex, "Unable to load flood points");
            return [];
        }
    }

    public bool TryLookup(uint territoryType, out IEnumerable<Vector3> points)
    {
        var collA = SeedsRemote.TryGetValue(territoryType, out var r) ? r : [];
        var collB = SeedsLocal.TryGetValue(territoryType, out var l) ? l : [];

        points = [.. collA, .. collB];
        return collA.Count > 0 || collB.Count > 0;
    }
}
