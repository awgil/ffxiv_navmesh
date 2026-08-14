using System.Numerics;
using Navmesh.NavVolume;
using Xunit;

namespace Navmesh.Tests;

public class VoxelStringPullTests
{
    [Fact]
    public void OpenCorridorCollapsesToItsEndpoints()
    {
        var g = new VoxelGrid();
        var path = g.Path((0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0), (5, 0, 0));

        var res = VoxelStringPull.Simplify(g.Map, path);

        Assert.Equal([path[0], path[^1]], res);
    }

    [Fact]
    public void StaircaseThroughOpenAirCollapsesToItsEndpoints()
    {
        // what a flying path actually looks like: one point per voxel entered, alternating axes
        var g = new VoxelGrid();
        var path = g.Path((0, 0, 0), (1, 0, 0), (1, 1, 0), (2, 1, 0), (2, 2, 0), (3, 2, 0), (3, 3, 0));

        var res = VoxelStringPull.Simplify(g.Map, path);

        Assert.Equal([path[0], path[^1]], res);
    }

    [Fact]
    public void CornerIsKeptWhenTheShortcutWouldCutThroughGeometry()
    {
        var g = new VoxelGrid();
        g.SolidBox(2, 0, 2, 4, 0, 4); // a block in the inside of the turn
        var path = g.Path(
            (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0), (5, 0, 0),
            (5, 0, 1), (5, 0, 2), (5, 0, 3), (5, 0, 4), (5, 0, 5));

        var res = VoxelStringPull.Simplify(g.Map, path);

        Assert.True(res.Count > 2, "the turn cannot be a straight line");
        Assert.True(res.Count < path.Count, "but it should still be shorter than one point per voxel");
        AssertEveryHopIsClear(g, res);
    }

    [Fact]
    public void EndpointsAndOrderSurvive()
    {
        var g = new VoxelGrid();
        g.SolidBox(2, 0, 0, 2, 0, 3); // wall with a gap at z=4..7
        g.SolidBox(5, 0, 4, 5, 0, 7); // and a second one offset the other way
        var path = g.Path(
            (0, 0, 0), (1, 0, 0), (1, 0, 1), (1, 0, 2), (1, 0, 3), (1, 0, 4), (1, 0, 5),
            (2, 0, 5), (3, 0, 5), (4, 0, 5), (4, 0, 4), (4, 0, 3), (4, 0, 2), (5, 0, 2), (6, 0, 2));

        var res = VoxelStringPull.Simplify(g.Map, path);

        Assert.Equal(path[0], res[0]);
        Assert.Equal(path[^1], res[^1]);
        Assert.True(res.Count <= path.Count);
        AssertIsSubsequenceOf(path, res);
        AssertEveryHopIsClear(g, res);
    }

    [Fact]
    public void PathsTooShortToSimplifyAreLeftAlone()
    {
        var g = new VoxelGrid();
        Assert.Empty(VoxelStringPull.Simplify(g.Map, []));

        var single = g.Path((0, 0, 0));
        Assert.Equal(single, VoxelStringPull.Simplify(g.Map, single));

        var pair = g.Path((0, 0, 0), (5, 0, 5));
        Assert.Equal(pair, VoxelStringPull.Simplify(g.Map, pair));
    }

    [Fact]
    public void ABlockedHopIsNeverIntroduced()
    {
        // the input itself steps voxel by voxel, so every hop the output invents has to be checked
        var g = new VoxelGrid();
        g.SolidBox(3, 0, 0, 3, 0, 5);
        g.SolidBox(0, 0, 3, 1, 0, 3);
        var path = g.Path(
            (0, 0, 0), (0, 0, 1), (0, 0, 2), (1, 0, 2), (2, 0, 2), (2, 0, 3), (2, 0, 4),
            (2, 0, 5), (2, 0, 6), (3, 0, 6), (4, 0, 6), (5, 0, 6), (5, 0, 5), (5, 0, 4));

        var res = VoxelStringPull.Simplify(g.Map, path);

        AssertEveryHopIsClear(g, res);
    }

    private static void AssertEveryHopIsClear(VoxelGrid g, List<(ulong voxel, Vector3 p)> path)
    {
        for (var i = 1; i < path.Count; ++i)
            Assert.True(g.Visible(path[i - 1], path[i]), $"hop {path[i - 1].p} -> {path[i].p} passes through geometry");
    }

    private static void AssertIsSubsequenceOf(List<(ulong voxel, Vector3 p)> whole, List<(ulong voxel, Vector3 p)> part)
    {
        var at = 0;
        foreach (var p in part)
        {
            while (at < whole.Count && whole[at] != p)
                ++at;
            Assert.True(at < whole.Count, $"{p.p} is not a point of the original path, or is out of order");
            ++at;
        }
    }
}
