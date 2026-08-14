using System.Numerics;
using Navmesh.NavVolume;

namespace Navmesh.Tests;

// A single-level voxel map with unit cells spanning (0,0,0)..(n,n,n), so a cell index and the
// integer part of a world coordinate are the same thing and a test can be read as a floor plan.
internal sealed class VoxelGrid
{
    public readonly VoxelMap Map;
    private readonly int _n;

    public VoxelGrid(int n = 8)
    {
        _n = n;
        Map = new VoxelMap(Vector3.Zero, new Vector3(n), [n]);
    }

    public void Solid(int x, int y, int z)
        => Map.RootTile.Contents[Map.Levels[0].VoxelToIndex(x, y, z)] = VoxelMap.VoxelOccupiedBit | VoxelMap.VoxelIdMask;

    // fills a closed box of cells, so a wall can be written as one call
    public void SolidBox(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var x = x0; x <= x1; ++x)
            for (var y = y0; y <= y1; ++y)
                for (var z = z0; z <= z1; ++z)
                    Solid(x, y, z);
    }

    // cell centres, which is where VoxelPathfind puts its nodes
    public (ulong voxel, Vector3 p) At(int x, int y, int z)
    {
        var p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        return (Map.FindLeafVoxel(p).voxel, p);
    }

    public List<(ulong voxel, Vector3 p)> Path(params (int x, int y, int z)[] cells)
        => cells.Select(c => At(c.x, c.y, c.z)).ToList();

    public bool Visible((ulong voxel, Vector3 p) a, (ulong voxel, Vector3 p) b)
        => VoxelSearch.LineOfSight(Map, a.voxel, b.voxel, a.p, b.p);
}
