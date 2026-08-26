using System.Numerics;
using Navmesh.NavVolume;

namespace Navmesh.Tests;

// A three-level voxel map with unit leaf cells. VoxelPathfind's neighbour walk reads Levels[0..2]
// directly, so the single-level VoxelGrid the string pull tests use cannot be handed to it. Cells
// are unit sized and the map starts at the origin, so a cell index and the integer part of a world
// coordinate are still the same thing and a test still reads as a floor plan.
internal sealed class VoxelWorld
{
    public readonly int Side;
    private readonly int[] _tiles;
    private readonly bool[,,] _solid;
    private VoxelMap? _map;

    public VoxelWorld(int l0 = 4, int l1 = 2, int l2 = 2)
    {
        _tiles = [l0, l1, l2];
        Side = l0 * l1 * l2;
        _solid = new bool[Side, Side, Side];
    }

    // built on first use, so every Solid call has to come before the first query
    public VoxelMap Map => _map ??= Build();

    public void Solid(int x, int y, int z) => _solid[x, y, z] = true;

    // fills a closed box of cells, so a wall can be written as one call
    public void SolidBox(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var x = x0; x <= x1; ++x)
            for (var y = y0; y <= y1; ++y)
                for (var z = z0; z <= z1; ++z)
                    Solid(x, y, z);
    }

    // clears cells again, so a wall can be written as one box and then have a hole punched in it
    public void EmptyBox(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var x = x0; x <= x1; ++x)
            for (var y = y0; y <= y1; ++y)
                for (var z = z0; z <= z1; ++z)
                    _solid[x, y, z] = false;
    }

    // cell centres, which is where VoxelPathfind puts its nodes
    public (ulong voxel, Vector3 p) At(int x, int y, int z)
    {
        var p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        return (Map.FindLeafVoxel(p).voxel, p);
    }

    public bool Visible((ulong voxel, Vector3 p) a, (ulong voxel, Vector3 p) b)
        => VoxelSearch.LineOfSight(Map, a.voxel, b.voxel, a.p, b.p);

    private VoxelMap Build()
    {
        var map = new VoxelMap(Vector3.Zero, new Vector3(Side), _tiles);
        // VoxelMap.Build takes one L0 column at leaf resolution, which is how NavmeshBuilder feeds it
        var perColumn = _tiles[1] * _tiles[2];
        for (var tz = 0; tz < _tiles[0]; ++tz)
        {
            for (var tx = 0; tx < _tiles[0]; ++tx)
            {
                var vox = new Voxelizer(perColumn, Side, perColumn);
                for (var x = 0; x < perColumn; ++x)
                    for (var z = 0; z < perColumn; ++z)
                        for (var y = 0; y < Side; ++y)
                            if (_solid[tx * perColumn + x, y, tz * perColumn + z])
                                vox.AddSpan(x, z, y, y);
                map.Build(vox, tx, tz);
            }
        }
        return map;
    }
}
