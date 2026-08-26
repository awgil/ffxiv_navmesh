using System.Numerics;
using Navmesh.NavVolume;
using Xunit;

namespace Navmesh.Tests;

// Covers the tuning knobs added in ADR 0008. The point of every test here is that a faster search
// is still a usable one: it reaches the goal, and no hop it returns passes through geometry.
public class VoxelPathfindTests
{
    // randomness is upstream's, and it perturbs the g-score; the tests turn it off so node counts
    // are a property of the search rather than of a seed
    private static VoxelPathfindTuning Tuning(float weight = 1, bool raycast = true, float range = 0, int maxSteps = 1000000)
        => new() { HeuristicWeight = weight, SearchRaycast = raycast, RaycastRange = range, MaxSteps = maxSteps, RandomnessMultiplier = 0 };

    // an empty world is a handful of coarse octree cells, which is too small to say anything about
    // how far a search spreads; pillars force subdivision down to leaf cells and leave a route
    private static VoxelWorld PillarField()
    {
        var w = new VoxelWorld(8, 2, 2); // 32 units a side, unit leaf cells
        for (var x = 3; x < 30; x += 6)
            for (var z = 3; z < 30; z += 6)
                w.SolidBox(x, 0, z, x + 1, 31, z + 1);
        return w;
    }

    private static List<(ulong voxel, Vector3 p)> Find(VoxelWorld w, VoxelPathfindTuning tuning, (int x, int y, int z) from, (int x, int y, int z) to, out VoxelPathfind pf)
    {
        pf = new VoxelPathfind(w.Map) { Settings = tuning };
        var a = w.At(from.x, from.y, from.z);
        var b = w.At(to.x, to.y, to.z);
        return pf.FindPath(a.voxel, b.voxel, a.p, b.p, true, false, default);
    }

    private static void AssertReaches(VoxelWorld w, List<(ulong voxel, Vector3 p)> path, (int x, int y, int z) to)
    {
        Assert.NotEmpty(path);
        var goal = w.At(to.x, to.y, to.z);
        Assert.True((path[^1].p - goal.p).Length() < 1.5f, $"path ended at {path[^1].p}, wanted {goal.p}");
    }

    private static void AssertEveryHopIsClear(VoxelWorld w, List<(ulong voxel, Vector3 p)> path)
    {
        for (var i = 1; i < path.Count; ++i)
            Assert.True(w.Visible(path[i - 1], path[i]), $"hop {path[i - 1].p} -> {path[i].p} passes through geometry");
    }

    private static float LongestHop(List<(ulong voxel, Vector3 p)> path)
    {
        var longest = 0f;
        for (var i = 1; i < path.Count; ++i)
            longest = MathF.Max(longest, (path[i].p - path[i - 1].p).Length());
        return longest;
    }

    [Fact]
    public void UpstreamTuningLeavesTheSearchAsItWas()
    {
        var t = VoxelPathfindTuning.Upstream;

        Assert.Equal(1, t.HeuristicWeight);
        Assert.True(t.SearchRaycast);
        Assert.Equal(0, t.RaycastRange); // 0 means no cap, which is what upstream used
        Assert.Equal(1000000, t.MaxSteps);
        Assert.Equal(1, t.RandomnessMultiplier);
    }

    [Fact]
    public void DefaultSettingsAreUpstreamSettings()
    {
        var w = new VoxelWorld();

        var pf = new VoxelPathfind(w.Map);

        Assert.Equal(VoxelPathfindTuning.Upstream, pf.Settings);
    }

    [Fact]
    public void ConfigSwitchOffIsUpstream()
    {
        // ADR 0002: a fresh install has to behave exactly like upstream until a switch is flipped
        var cfg = new Movement.Human.HumanizerConfig();

        Assert.False(cfg.VolumeTuningEnabled);
        Assert.Equal(VoxelPathfindTuning.Upstream, cfg.VolumePathfinding(1));
    }

    [Fact]
    public void ConfigSwitchOnCarriesTheKnobsThrough()
    {
        var cfg = new Movement.Human.HumanizerConfig
        {
            VolumeTuningEnabled = true,
            VolumeHeuristicWeight = 1.5f,
            VolumeRaycastRange = 25f,
            VolumeSearchRaycast = true,
            VolumeMaxSteps = 200000,
        };

        var tuning = cfg.VolumePathfinding(0.5f);

        Assert.Equal(1.5f, tuning.HeuristicWeight);
        Assert.Equal(25f, tuning.RaycastRange);
        Assert.True(tuning.SearchRaycast);
        Assert.Equal(200000, tuning.MaxSteps);
        Assert.Equal(0.5f, tuning.RandomnessMultiplier); // randomness stays upstream's, from Config
    }

    [Fact]
    public void FindsAPathThroughOpenAir()
    {
        var w = new VoxelWorld();

        var path = Find(w, Tuning(), (1, 1, 1), (14, 14, 14), out _);

        AssertReaches(w, path, (14, 14, 14));
        AssertEveryHopIsClear(w, path);
    }

    [Fact]
    public void FindsAPathThroughTheGapInAWall()
    {
        var w = new VoxelWorld();
        w.SolidBox(0, 0, 8, 15, 15, 8); // wall across the middle
        w.EmptyBox(7, 7, 8, 8, 8, 8); // ...with a hole punched in it

        var path = Find(w, Tuning(), (1, 1, 1), (14, 14, 14), out _);

        AssertReaches(w, path, (14, 14, 14));
        AssertEveryHopIsClear(w, path);
    }

    [Fact]
    public void WeightedHeuristicStillReachesTheGoalThroughAGap()
    {
        var w = new VoxelWorld();
        w.SolidBox(0, 0, 8, 15, 15, 8);
        w.EmptyBox(7, 7, 8, 8, 8, 8);

        var path = Find(w, Tuning(weight: 2), (1, 1, 1), (14, 14, 14), out _);

        AssertReaches(w, path, (14, 14, 14));
        AssertEveryHopIsClear(w, path);
    }

    [Fact]
    public void WeightedHeuristicExpandsFewerNodes()
    {
        // the whole point of the weight: moves are axis-only while the heuristic is euclidean, so an
        // unweighted search spreads sideways far more than it needs to
        var w = PillarField();

        Find(w, Tuning(weight: 1), (1, 1, 1), (30, 30, 30), out var plain);
        Find(w, Tuning(weight: 2), (1, 1, 1), (30, 30, 30), out var weighted);

        Assert.True(weighted.NodeSpan.Length < plain.NodeSpan.Length,
            $"weighted expanded {weighted.NodeSpan.Length}, unweighted expanded {plain.NodeSpan.Length}");
    }

    [Fact]
    public void SearchRaycastOffStillProducesAClearPath()
    {
        var w = new VoxelWorld();
        w.SolidBox(0, 0, 8, 15, 15, 8);
        w.EmptyBox(7, 7, 8, 8, 8, 8);

        var path = Find(w, Tuning(raycast: false), (1, 1, 1), (14, 14, 14), out _);

        AssertReaches(w, path, (14, 14, 14));
        AssertEveryHopIsClear(w, path);
    }

    [Fact]
    public void SearchRaycastOffOverridesTheCallersRequest()
    {
        // PathfindVolume passes NavmeshManager.UseRaycasts, which is shared with the mesh query, so
        // turning the volume raycast off has to win over what the caller asked for
        var w = PillarField();

        var off = Find(w, Tuning(raycast: false), (1, 1, 1), (30, 30, 30), out _);
        var on = Find(w, Tuning(raycast: true), (1, 1, 1), (30, 30, 30), out _);

        Assert.True(off.Count > on.Count,
            $"raycast off left {off.Count} points, on left {on.Count}; off should stay uncollapsed");
    }

    [Fact]
    public void RaycastRangeShortensTheHopsTheSearchCollapses()
    {
        var w = PillarField();

        var capped = Find(w, Tuning(range: 4), (1, 1, 1), (30, 30, 30), out _);
        var uncapped = Find(w, Tuning(range: 0), (1, 1, 1), (30, 30, 30), out _);

        AssertReaches(w, capped, (30, 30, 30));
        AssertEveryHopIsClear(w, capped);
        Assert.True(LongestHop(capped) < LongestHop(uncapped),
            $"capped longest hop {LongestHop(capped)}, uncapped {LongestHop(uncapped)}");
    }

    [Fact]
    public void MaxStepsBoundsASearchThatCannotSucceed()
    {
        // goal sealed inside a shell: no budget can find it, so the only question is how long the
        // search spends proving that
        var w = PillarField();
        w.SolidBox(24, 24, 24, 28, 28, 24);
        w.SolidBox(24, 24, 28, 28, 28, 28);
        w.SolidBox(24, 24, 24, 24, 28, 28);
        w.SolidBox(28, 24, 24, 28, 28, 28);
        w.SolidBox(24, 24, 24, 28, 24, 28);
        w.SolidBox(24, 28, 24, 28, 28, 28);

        var bounded = Find(w, Tuning(maxSteps: 100), (1, 1, 1), (26, 26, 26), out var boundedPf);
        Find(w, Tuning(maxSteps: 1000000), (1, 1, 1), (26, 26, 26), out var unboundedPf);

        Assert.True((bounded[^1].p - w.At(26, 26, 26).p).Length() > 1.5f, "the sealed goal must not be reached");
        Assert.True(boundedPf.NodeSpan.Length < unboundedPf.NodeSpan.Length,
            $"budget of 100 expanded {boundedPf.NodeSpan.Length}, unbounded expanded {unboundedPf.NodeSpan.Length}");
    }
}
