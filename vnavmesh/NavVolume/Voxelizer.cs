using System;
using System.Collections;
using System.Numerics;

namespace Navmesh.NavVolume;

// raw 1/2-bit-per-voxel container
// for downsampled mips, w is 0 for 'has solids', 1 for 'has non-solids'
public class Voxelizer
{
    public int NumX;
    public int NumY;
    public int NumZ;
    public int NumW; // 1 (if each voxel is solid or empty) or 2 (if it can be partially filled)
    public BitArray Contents; // order is (w,y,x,z), TODO swizzle?

    public Voxelizer(int nx, int ny, int nz, bool partial = false)
    {
        if (!BitOperations.IsPow2(nx) || !BitOperations.IsPow2(ny) || !BitOperations.IsPow2(nz))
            throw new Exception($"Non-power-of-two size not supported: {nx}x{ny}x{nz}");
        NumX = nx;
        NumY = ny;
        NumZ = nz;
        NumW = partial ? 2 : 1;
        Contents = new(nx * ny * nz * NumW);
    }

    public int VoxelToIndex(int x, int y, int z) => (((z * NumX) + x) * NumY + y) * NumW;

    public (bool solid, bool empty) Get(int idx)
    {
        var solid = Contents[idx];
        var empty = NumW > 1 ? Contents[idx + 1] : !solid;
        return (solid, empty);
    }
    public (bool solid, bool empty) Get(int x, int y, int z) => Get(VoxelToIndex(x, y, z));

    public void AddSpan(int x, int z, int y0, int y1)
    {
        var idx = VoxelToIndex(x, y0, z);
        for (int y = y0; y <= y1; ++y, idx += NumW)
            Contents[idx] = true;
    }

    public Voxelizer Downsample(int dx, int dy, int dz)
    {
        var result = new Voxelizer(NumX / dx, NumY / dy, NumZ / dz, true);
        var shiftX = BitOperations.Log2((uint)dx);
        var shiftY = BitOperations.Log2((uint)dy);
        var shiftZ = BitOperations.Log2((uint)dz);
        int idx = 0;
        for (int z = 0; z < NumZ; ++z)
        {
            for (int x = 0; x < NumX; ++x)
            {
                for (int y = 0; y < NumY; ++y, idx += NumW)
                {
                    var (solid, empty) = Get(idx);
                    var resIndex = result.VoxelToIndex(x >> shiftX, y >> shiftY, z >> shiftZ);
                    result.Contents[resIndex] |= solid;
                    result.Contents[resIndex + 1] |= empty;
                }
            }
        }
        return result;
    }
}
