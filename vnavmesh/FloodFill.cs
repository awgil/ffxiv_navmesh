using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public Dictionary<uint, List<Vector3>> Seeds = [];

    private static FloodFill? _instance;

    public static FloodFill? Get() => _instance;

    public static async Task<FloodFill> GetAsync()
    {
        if (Service.PluginInterface.IsDev)
            _instance = null;

        _instance ??= await Init();
        return _instance!;
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

    private static async Task<FloodFill> FromStream(Stream s)
    {
        var seeds = await JsonSerializer.DeserializeAsync<Dictionary<uint, List<JsonVec>>>(s, new JsonSerializerOptions() { ReadCommentHandling = JsonCommentHandling.Skip });
        return new FloodFill() { Seeds = seeds!.ToDictionary(kv => kv.Key, v => v.Value.Select(v => (Vector3)v).ToList()) };
    }
}
