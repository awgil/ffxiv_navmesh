using Navmesh;
using Navmesh.Benchmarking;
using Navmesh.Movement;
using System.Diagnostics;
using System.Globalization;

if (args.Contains("--help") || args.Contains("-h"))
{
    BenchOptions.PrintUsage();
    return 0;
}

var options = BenchOptions.Parse(args);
if (options == null)
{
    BenchOptions.PrintUsage();
    return 2;
}

NavmeshDiagnostics.RandomnessMultiplierProvider = () => options.RandomnessMultiplier;

var metadata = BenchmarkSnapshotFiles.LoadMetadata(options.SnapshotDirectory);
var navmesh = BenchmarkSnapshotFiles.LoadNavmesh(options.SnapshotDirectory, metadata);
if (options.InspectCount > 0)
{
    InspectSnapshot(navmesh, metadata, options.InspectCount);
    return 0;
}

var csvPrefix = options.PhaseMode ? "pathfind-phases" : "pathfind";
var csvPath = options.CsvPath ?? Path.Combine(options.SnapshotDirectory, $"{csvPrefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.csv");
var cases = metadata.Pairs
    .Select((pair, index) => new BenchCase(index, pair))
    .Where(c => options.FlyModes.Contains(c.Pair.Fly))
    .Where(c => !c.Pair.Fly || navmesh.Volume != null)
    .Where(c => !options.PhaseMode || !c.Pair.Fly)
    .ToList();

if (cases.Count == 0)
    throw new InvalidOperationException("No matching benchmark path pairs were found in the snapshot.");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);

var summaries = new Dictionary<string, List<double>>();
var successCounts = new Dictionary<string, int>();
var totalCounts = new Dictionary<string, int>();
var cacheHitCounts = new Dictionary<string, int>();

if (options.PhaseMode)
{
    var phaseSummaries = new Dictionary<string, PhaseSamples>();

    using var phaseWriter = new StreamWriter(csvPath);
    phaseWriter.WriteLine("variant,iteration,pair_index,use_raycast,use_string_pulling,success,total_us,nearest_us,find_path_us,straight_path_us,waypoint_us,alloc_bytes,nearest_alloc_bytes,find_path_alloc_bytes,straight_path_alloc_bytes,waypoint_alloc_bytes,find_path_popped_nodes,find_path_touched_nodes,find_path_scanned_edges,find_path_passed_filter_edges,find_path_skipped_parent_edges,find_path_skipped_same_parent_edges,find_path_skipped_open_worse,find_path_skipped_closed_worse,find_path_portal_calls,find_path_cost_calls,find_path_heuristic_calls,find_path_heap_pushes,find_path_heap_pops,find_path_heap_modifies,find_path_max_open_list,find_path_final_cost,poly_path,straight_points,waypoints,error");

    foreach (var raycast in options.RaycastModes)
    {
        foreach (var stringPulling in options.StringPullingModes)
        {
            var variant = $"raycast={raycast};string={stringPulling};h={options.MeshHeuristicScale:g}";
            phaseSummaries[variant] = new();
            successCounts[variant] = 0;
            totalCounts[variant] = 0;

            var query = new NavmeshQuery(navmesh);
            query.MeshHeuristicScale = options.MeshHeuristicScale;
            query.NearestMeshPolyCacheSize = options.NearestMeshPolyCacheSize;
            for (var warmup = 0; warmup < options.WarmupIterations; warmup++)
                RunPhasePass(query, cases, raycast, stringPulling, null, null, variant, warmup);

            for (var iteration = 0; iteration < options.Iterations; iteration++)
                RunPhasePass(query, cases, raycast, stringPulling, phaseWriter, phaseSummaries[variant], variant, iteration);
        }
    }

    Console.WriteLine($"Snapshot: {options.SnapshotDirectory}");
    Console.WriteLine($"CSV: {csvPath}");
    Console.WriteLine($"Pairs: {cases.Count}, iterations: {options.Iterations}, phase mode: mesh");
    foreach (var (variant, samples) in phaseSummaries)
    {
        samples.Sort();
        var total = totalCounts[variant];
        var success = successCounts[variant];
        Console.WriteLine($"{variant}: success {success}/{total}, total median {Percentile(samples.Total, 0.50):F1}us, p95 {Percentile(samples.Total, 0.95):F1}us, p99 {Percentile(samples.Total, 0.99):F1}us");
        Console.WriteLine($"  nearest median {Percentile(samples.Nearest, 0.50):F1}us, findPath {Percentile(samples.FindPath, 0.50):F1}us, straight {Percentile(samples.StraightPath, 0.50):F1}us, waypoint {Percentile(samples.Waypoint, 0.50):F1}us, alloc median {Percentile(samples.AllocBytes, 0.50):F0}B");
        Console.WriteLine($"  alloc median: nearest {Percentile(samples.NearestAllocBytes, 0.50):F0}B, findPath {Percentile(samples.FindPathAllocBytes, 0.50):F0}B, straight {Percentile(samples.StraightPathAllocBytes, 0.50):F0}B, waypoint {Percentile(samples.WaypointAllocBytes, 0.50):F0}B");
        Console.WriteLine($"  findPath work median: popped {Percentile(samples.FindPathPoppedNodes, 0.50):F0}, touched {Percentile(samples.FindPathTouchedNodes, 0.50):F0}, edges {Percentile(samples.FindPathScannedEdges, 0.50):F0}, portals {Percentile(samples.FindPathPortalCalls, 0.50):F0}, maxOpen {Percentile(samples.FindPathMaxOpenList, 0.50):F0}, cost {Percentile(samples.FindPathFinalCost, 0.50):F1}");
    }

    return 0;
}

using var writer = new StreamWriter(csvPath);
writer.WriteLine("variant,iteration,pair_index,fly,use_raycast,use_string_pulling,success,elapsed_us,waypoints,cache_hit,error");

foreach (var raycast in options.RaycastModes)
{
    foreach (var stringPulling in options.StringPullingModes)
    {
        var variant = $"raycast={raycast};string={stringPulling};h={options.MeshHeuristicScale:g};pathCache={options.MeshPathCacheSize};nearestCache={options.NearestMeshPolyCacheSize}";
        summaries[variant] = [];
        successCounts[variant] = 0;
        totalCounts[variant] = 0;
        cacheHitCounts[variant] = 0;

        var query = new NavmeshQuery(navmesh);
        query.MeshHeuristicScale = options.MeshHeuristicScale;
        query.MeshPathCacheSize = options.MeshPathCacheSize;
        query.NearestMeshPolyCacheSize = options.NearestMeshPolyCacheSize;
        for (var warmup = 0; warmup < options.WarmupIterations; warmup++)
            RunPass(query, cases, raycast, stringPulling, null, null, variant, warmup);

        for (var iteration = 0; iteration < options.Iterations; iteration++)
            RunPass(query, cases, raycast, stringPulling, writer, summaries[variant], variant, iteration);
    }
}

Console.WriteLine($"Snapshot: {options.SnapshotDirectory}");
Console.WriteLine($"CSV: {csvPath}");
Console.WriteLine($"Pairs: {cases.Count}, iterations: {options.Iterations}");
foreach (var (variant, samples) in summaries)
{
    samples.Sort();
    var total = totalCounts[variant];
    var success = successCounts[variant];
    Console.WriteLine($"{variant}: success {success}/{total}, cache hits {cacheHitCounts[variant]}/{total}, median {Percentile(samples, 0.50):F1}us, p95 {Percentile(samples, 0.95):F1}us, p99 {Percentile(samples, 0.99):F1}us");
}

return 0;

void InspectSnapshot(Navmesh.Navmesh navmesh, BenchmarkSnapshotMetadata metadata, int count)
{
    var query = new NavmeshQuery(navmesh);
    var tileCount = 0;
    var polyCount = 0;
    for (var i = 0; i < navmesh.Mesh.GetMaxTiles(); i++)
    {
        var tile = navmesh.Mesh.GetTile(i);
        if (tile?.data?.header == null)
            continue;

        tileCount++;
        polyCount += tile.data.header.polyCount;
    }

    Console.WriteLine($"SourceKey: {metadata.SourceKey}");
    Console.WriteLine($"TerritoryId: {metadata.TerritoryId}");
    Console.WriteLine($"Tiles: {tileCount}/{navmesh.Mesh.GetMaxTiles()}, polys: {polyCount}, volume: {navmesh.Volume != null}");
    Console.WriteLine($"Pairs: {metadata.Pairs.Count}");
    for (var i = 0; i < Math.Min(count, metadata.Pairs.Count); i++)
    {
        var pair = metadata.Pairs[i];
        var from = pair.From.ToVector3();
        var to = pair.To.ToVector3();
        if (pair.Fly)
        {
            var fromVoxel = query.FindNearestVolumeVoxel(from);
            var toVoxel = query.FindNearestVolumeVoxel(to);
            Console.WriteLine($"[{i}] fly {from} -> {to}, voxels {fromVoxel:X} -> {toVoxel:X}");
        }
        else
        {
            var fromPoly = query.FindNearestMeshPoly(from);
            var toPoly = query.FindNearestMeshPoly(to);
            Console.WriteLine($"[{i}] mesh {from} -> {to}, polys {fromPoly:X} -> {toPoly:X}");
        }
    }
}

void RunPass(
    NavmeshQuery query,
    IReadOnlyList<BenchCase> cases,
    bool useRaycast,
    bool useStringPulling,
    StreamWriter? csv,
    List<double>? samples,
    string variant,
    int iteration)
{
    foreach (var c in cases)
    {
        var from = c.Pair.From.ToVector3();
        var to = c.Pair.To.ToVector3();
        var start = Stopwatch.GetTimestamp();
        var success = false;
        var waypoints = 0;
        var cacheHit = false;
        var error = "";
        try
        {
            List<Waypoint> path;
            if (c.Pair.Fly)
            {
                path = query.PathfindVolume(from, to, useRaycast, useStringPulling, CancellationToken.None);
            }
            else
            {
                path = query.PathfindMesh(from, to, useRaycast, useStringPulling, 0, CancellationToken.None);
                cacheHit = query.LastMeshPathCacheHit;
            }
            success = path.Count > 0;
            waypoints = path.Count;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        var elapsedUs = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
        if (csv != null)
        {
            samples!.Add(elapsedUs);
            totalCounts[variant]++;
            if (success)
                successCounts[variant]++;
            if (cacheHit)
                cacheHitCounts[variant]++;

            csv.WriteLine(string.Join(',', [
                Csv(variant),
                iteration.ToString(CultureInfo.InvariantCulture),
                c.Index.ToString(CultureInfo.InvariantCulture),
                c.Pair.Fly.ToString(CultureInfo.InvariantCulture),
                useRaycast.ToString(CultureInfo.InvariantCulture),
                useStringPulling.ToString(CultureInfo.InvariantCulture),
                success.ToString(CultureInfo.InvariantCulture),
                elapsedUs.ToString("F3", CultureInfo.InvariantCulture),
                waypoints.ToString(CultureInfo.InvariantCulture),
                cacheHit.ToString(CultureInfo.InvariantCulture),
                Csv(error),
            ]));
        }
    }
}

void RunPhasePass(
    NavmeshQuery query,
    IReadOnlyList<BenchCase> cases,
    bool useRaycast,
    bool useStringPulling,
    StreamWriter? csv,
    PhaseSamples? samples,
    string variant,
    int iteration)
{
    foreach (var c in cases)
    {
        var from = c.Pair.From.ToVector3();
        var to = c.Pair.To.ToVector3();
        NavmeshQuery.MeshPathfindPhaseResult result;
        try
        {
            result = query.BenchmarkPathfindMeshPhases(from, to, useRaycast, useStringPulling, 0, CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = new(
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                ex.GetType().Name + ": " + ex.Message);
        }

        if (csv != null)
        {
            var totalUs = TicksToMicroseconds(result.TotalTicks);
            var nearestUs = TicksToMicroseconds(result.NearestTicks);
            var findPathUs = TicksToMicroseconds(result.FindPathTicks);
            var straightPathUs = TicksToMicroseconds(result.StraightPathTicks);
            var waypointUs = TicksToMicroseconds(result.WaypointTicks);

            samples!.Total.Add(totalUs);
            samples.Nearest.Add(nearestUs);
            samples.FindPath.Add(findPathUs);
            samples.StraightPath.Add(straightPathUs);
            samples.Waypoint.Add(waypointUs);
            samples.AllocBytes.Add(result.AllocBytes);
            samples.NearestAllocBytes.Add(result.NearestAllocBytes);
            samples.FindPathAllocBytes.Add(result.FindPathAllocBytes);
            samples.StraightPathAllocBytes.Add(result.StraightPathAllocBytes);
            samples.WaypointAllocBytes.Add(result.WaypointAllocBytes);
            samples.FindPathPoppedNodes.Add(result.FindPathPoppedNodes);
            samples.FindPathTouchedNodes.Add(result.FindPathTouchedNodes);
            samples.FindPathScannedEdges.Add(result.FindPathScannedEdges);
            samples.FindPathPortalCalls.Add(result.FindPathPortalCalls);
            samples.FindPathMaxOpenList.Add(result.FindPathMaxOpenList);
            samples.FindPathFinalCost.Add(result.FindPathFinalCost);
            totalCounts[variant]++;
            if (result.Success)
                successCounts[variant]++;

            csv.WriteLine(string.Join(',', [
                Csv(variant),
                iteration.ToString(CultureInfo.InvariantCulture),
                c.Index.ToString(CultureInfo.InvariantCulture),
                useRaycast.ToString(CultureInfo.InvariantCulture),
                useStringPulling.ToString(CultureInfo.InvariantCulture),
                result.Success.ToString(CultureInfo.InvariantCulture),
                totalUs.ToString("F3", CultureInfo.InvariantCulture),
                nearestUs.ToString("F3", CultureInfo.InvariantCulture),
                findPathUs.ToString("F3", CultureInfo.InvariantCulture),
                straightPathUs.ToString("F3", CultureInfo.InvariantCulture),
                waypointUs.ToString("F3", CultureInfo.InvariantCulture),
                result.AllocBytes.ToString(CultureInfo.InvariantCulture),
                result.NearestAllocBytes.ToString(CultureInfo.InvariantCulture),
                result.FindPathAllocBytes.ToString(CultureInfo.InvariantCulture),
                result.StraightPathAllocBytes.ToString(CultureInfo.InvariantCulture),
                result.WaypointAllocBytes.ToString(CultureInfo.InvariantCulture),
                result.FindPathPoppedNodes.ToString(CultureInfo.InvariantCulture),
                result.FindPathTouchedNodes.ToString(CultureInfo.InvariantCulture),
                result.FindPathScannedEdges.ToString(CultureInfo.InvariantCulture),
                result.FindPathPassedFilterEdges.ToString(CultureInfo.InvariantCulture),
                result.FindPathSkippedParentEdges.ToString(CultureInfo.InvariantCulture),
                result.FindPathSkippedSameParentEdges.ToString(CultureInfo.InvariantCulture),
                result.FindPathSkippedOpenWorse.ToString(CultureInfo.InvariantCulture),
                result.FindPathSkippedClosedWorse.ToString(CultureInfo.InvariantCulture),
                result.FindPathPortalCalls.ToString(CultureInfo.InvariantCulture),
                result.FindPathCostCalls.ToString(CultureInfo.InvariantCulture),
                result.FindPathHeuristicCalls.ToString(CultureInfo.InvariantCulture),
                result.FindPathHeapPushes.ToString(CultureInfo.InvariantCulture),
                result.FindPathHeapPops.ToString(CultureInfo.InvariantCulture),
                result.FindPathHeapModifies.ToString(CultureInfo.InvariantCulture),
                result.FindPathMaxOpenList.ToString(CultureInfo.InvariantCulture),
                result.FindPathFinalCost.ToString("F3", CultureInfo.InvariantCulture),
                result.PolyPathCount.ToString(CultureInfo.InvariantCulture),
                result.StraightPathCount.ToString(CultureInfo.InvariantCulture),
                result.Waypoints.ToString(CultureInfo.InvariantCulture),
                Csv(result.Error),
            ]));
        }
    }
}

static double Percentile(IReadOnlyList<double> values, double percentile)
{
    if (values.Count == 0)
        return 0;

    var index = Math.Clamp((int)Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1);
    return values[index];
}

static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

internal sealed record BenchCase(int Index, BenchmarkPathPair Pair);

internal sealed class PhaseSamples
{
    public readonly List<double> Total = [];
    public readonly List<double> Nearest = [];
    public readonly List<double> FindPath = [];
    public readonly List<double> StraightPath = [];
    public readonly List<double> Waypoint = [];
    public readonly List<double> AllocBytes = [];
    public readonly List<double> NearestAllocBytes = [];
    public readonly List<double> FindPathAllocBytes = [];
    public readonly List<double> StraightPathAllocBytes = [];
    public readonly List<double> WaypointAllocBytes = [];
    public readonly List<double> FindPathPoppedNodes = [];
    public readonly List<double> FindPathTouchedNodes = [];
    public readonly List<double> FindPathScannedEdges = [];
    public readonly List<double> FindPathPortalCalls = [];
    public readonly List<double> FindPathMaxOpenList = [];
    public readonly List<double> FindPathFinalCost = [];

    public void Sort()
    {
        Total.Sort();
        Nearest.Sort();
        FindPath.Sort();
        StraightPath.Sort();
        Waypoint.Sort();
        AllocBytes.Sort();
        NearestAllocBytes.Sort();
        FindPathAllocBytes.Sort();
        StraightPathAllocBytes.Sort();
        WaypointAllocBytes.Sort();
        FindPathPoppedNodes.Sort();
        FindPathTouchedNodes.Sort();
        FindPathScannedEdges.Sort();
        FindPathPortalCalls.Sort();
        FindPathMaxOpenList.Sort();
        FindPathFinalCost.Sort();
    }
}

internal sealed class BenchOptions
{
    public required string SnapshotDirectory { get; init; }
    public string? CsvPath { get; init; }
    public int Iterations { get; init; } = 5;
    public int WarmupIterations { get; init; } = 1;
    public float RandomnessMultiplier { get; init; }
    public float MeshHeuristicScale { get; init; } = 10f;
    public int MeshPathCacheSize { get; init; } = 2048;
    public int NearestMeshPolyCacheSize { get; init; } = 4096;
    public IReadOnlyList<bool> RaycastModes { get; init; } = [false, true];
    public IReadOnlyList<bool> StringPullingModes { get; init; } = [false, true];
    public IReadOnlyList<bool> FlyModes { get; init; } = [false, true];
    public int InspectCount { get; init; }
    public bool PhaseMode { get; init; }

    public static BenchOptions? Parse(string[] args)
    {
        if (args.Length == 0)
            return null;

        var snapshotDirectory = args[0];
        string? csvPath = null;
        var iterations = 5;
        var warmupIterations = 1;
        var randomnessMultiplier = 0f;
        var meshHeuristicScale = 10f;
        var meshPathCacheSize = 2048;
        var nearestMeshPolyCacheSize = 4096;
        IReadOnlyList<bool> raycastModes = [false, true];
        IReadOnlyList<bool> stringPullingModes = [false, true];
        IReadOnlyList<bool> flyModes = [false, true];
        var inspectCount = 0;
        var phaseMode = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            string next()
            {
                if (++i >= args.Length)
                    throw new ArgumentException($"Missing value after {arg}");
                return args[i];
            }

            switch (arg)
            {
                case "--csv":
                    csvPath = next();
                    break;
                case "--iterations":
                    iterations = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    warmupIterations = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--randomness":
                    randomnessMultiplier = float.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--heuristic-scale":
                    meshHeuristicScale = float.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--mesh-cache-size":
                    meshPathCacheSize = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--nearest-cache-size":
                    nearestMeshPolyCacheSize = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--raycast":
                    raycastModes = ParseBoolModes(next());
                    break;
                case "--string-pulling":
                    stringPullingModes = ParseBoolModes(next());
                    break;
                case "--fly":
                    flyModes = ParseBoolModes(next());
                    break;
                case "--inspect":
                    inspectCount = int.Parse(next(), CultureInfo.InvariantCulture);
                    break;
                case "--phase":
                    phaseMode = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new()
        {
            SnapshotDirectory = snapshotDirectory,
            CsvPath = csvPath,
            Iterations = Math.Max(1, iterations),
            WarmupIterations = Math.Max(0, warmupIterations),
            RandomnessMultiplier = randomnessMultiplier,
            MeshHeuristicScale = Math.Max(0.01f, meshHeuristicScale),
            MeshPathCacheSize = Math.Max(0, meshPathCacheSize),
            NearestMeshPolyCacheSize = Math.Max(0, nearestMeshPolyCacheSize),
            RaycastModes = raycastModes,
            StringPullingModes = stringPullingModes,
            FlyModes = flyModes,
            InspectCount = Math.Max(0, inspectCount),
            PhaseMode = phaseMode,
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project vnavmesh.Benchmarks -- <snapshot-dir> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --iterations <n>          Measured passes over all pairs. Default: 5");
        Console.WriteLine("  --warmup <n>              Warmup passes. Default: 1");
        Console.WriteLine("  --csv <path>              Output CSV path. Default: snapshot-dir/pathfind-*.csv");
        Console.WriteLine("  --raycast true|false|both Default: both");
        Console.WriteLine("  --string-pulling true|false|both Default: both");
        Console.WriteLine("  --fly true|false|both     Default: both");
        Console.WriteLine("  --randomness <value>      Voxel path randomness multiplier. Default: 0");
        Console.WriteLine("  --heuristic-scale <value> Mesh A* heuristic multiplier. Default: 10");
        Console.WriteLine("  --mesh-cache-size <n>     Mesh polygon path cache entries in normal mode. Default: 2048, 0 disables");
        Console.WriteLine("  --nearest-cache-size <n>  Exact nearest-poly cache entries. Default: 4096, 0 disables");
        Console.WriteLine("  --inspect <n>             Print navmesh and first n pair refs, then exit");
        Console.WriteLine("  --phase                   Measure mesh pathfinding phases and allocations");
    }

    private static IReadOnlyList<bool> ParseBoolModes(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => [true],
            "false" or "no" or "0" => [false],
            "both" or "all" => [false, true],
            _ => throw new ArgumentException($"Expected true, false, or both; got '{value}'"),
        };
    }

    private static bool ParseBool(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new ArgumentException($"Expected true or false; got '{value}'"),
        };
    }
}
