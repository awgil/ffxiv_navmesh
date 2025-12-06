using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;

namespace Navmesh;

public record struct JsonVec(float X, float Y, float Z)
{
    public static implicit operator Vector3(JsonVec v) => new(v.X, v.Y, v.Z);
    public static implicit operator JsonVec(Vector3 v) => new(v.X, v.Y, v.Z);
}

public class FloodFill
{
    public Dictionary<uint, List<JsonVec>> Seeds = [];

    private static FloodFill? _instance;

    public Action<FloodFill> Modified = delegate { };

    internal static void Clear() => _instance = null;

    public static FloodFill? Get() => _instance;

    public static async Task<FloodFill?> GetAsync()
    {
        try
        {
            _instance ??= await Init();
            return _instance;
        }
        catch (HttpRequestException ex)
        {
            Service.Log.Warning(ex, "Unable to fetch flood-fill data, navmesh pruning is disabled.");
            return null;
        }
    }

    public void AddPoint(uint zone, Vector3 point)
    {
        Seeds.TryAdd(zone, []);
        Seeds[zone].Add(point);
        Modified.Invoke(this);
    }

    public async Task Serialize()
    {
        if (Service.PluginInterface.IsDev)
        {
            var finfo = new FileInfo("C:\\Users\\me\\source\\repos\\vnav\\seeds\\seeds.json");
            using var st = finfo.Create();
            await JsonSerializer.SerializeAsync(st, new SortedDictionary<uint, List<JsonVec>>(Seeds), serOpts);
        }
    }

    private static async Task<FloodFill> Init()
    {
        if (Service.PluginInterface.IsDev)
        {
            var finfo = new FileInfo("C:\\Users\\me\\source\\repos\\vnav\\seeds\\seeds.json");
            using var st = finfo.OpenRead();
            return await FromStream(st);
        }
        else
        {
            var remote = "https://raw.githubusercontent.com/awgil/ffxiv_navmesh/refs/heads/seeds/seeds.json";
            var client = new HttpClient();
            using HttpResponseMessage resp = await client.GetAsync(remote);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStreamAsync();
            return await FromStream(body);
        }
    }

    private static readonly JsonSerializerOptions deOpts = new() { ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions serOpts = new()
    {
        WriteIndented = true,
        IndentSize = 2
    };

    private static async Task<FloodFill> FromStream(Stream s)
    {
        try
        {
            var seeds = await JsonSerializer.DeserializeAsync<Dictionary<uint, List<JsonVec>>>(s, deOpts);
            return new FloodFill() { Seeds = seeds! };
        }
        catch (JsonException ex)
        {
            Service.Log.Warning(ex, "Unable to load flood points");
            return new();
        }
    }
}
