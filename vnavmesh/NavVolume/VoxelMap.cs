using DotRecast.Recast;
using System;
using System.Collections;
using System.Numerics;

namespace Navmesh.NavVolume;

public class VoxelMap
{
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector3 CellSize { get; init; }
    public Vector3 InvCellSize { get; init; }
    public int NumCellsX { get; init; }
    public int NumCellsY { get; init; }
    public int NumCellsZ { get; init; }
    public BitArray Voxels { get; init; } // bit is set if voxel is solid (untraversable); order is (y,x,z)

    public bool this[int x, int y, int z]
    {
        get => Voxels[VoxelToIndex(x, y, z)];
        set => Voxels[VoxelToIndex(x, y, z)] = value;
    }

    public VoxelMap(Vector3 boundsMin, Vector3 boundsMax, int nx, int ny, int nz)
    {
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        CellSize = (boundsMax - boundsMin) / new Vector3(nx, ny, nz);
        InvCellSize = new Vector3(1) / CellSize;
        NumCellsX = nx;
        NumCellsY = ny;
        NumCellsZ = nz;
        Voxels = new(nx * ny * nz);
    }

    public void AddFromHeightfield(RcHeightfield hf)
    {
        float x0 = hf.bmin.X + (hf.borderSize + 0.5f) * hf.cs;
        float z = hf.bmin.Z + (hf.borderSize + 0.5f) * hf.cs;
        for (int iz = hf.borderSize; iz < hf.height - hf.borderSize; ++iz)
        {
            float x = x0;
            for (int ix = hf.borderSize; ix < hf.width - hf.borderSize; ++ix)
            {
                var span = hf.spans[iz * hf.width + ix];
                while (span != null)
                {
                    float y0 = hf.bmin.Y + span.smin * hf.ch;
                    float y1 = hf.bmin.Y + span.smax * hf.ch;

                    var v0 = WorldToVoxel(new(x, y0, z));
                    if (v0.x >= 0 && v0.x < NumCellsX && v0.z >= 0 && v0.z < NumCellsZ)
                    {
                        var vy0 = Math.Max(0, v0.y);
                        var vy1 = Math.Min(NumCellsY, (int)Math.Ceiling((y1 - BoundsMin.Y) * InvCellSize.Y));
                        for (int vy = vy0; vy < vy1; ++vy)
                            this[v0.x, vy, v0.z] = true;
                    }
                    span = span.next;
                }
                x += hf.cs;
            }
            z += hf.cs;
        }
    }

    public (int x, int y, int z) WorldToVoxel(Vector3 v)
    {
        var frac = (v - BoundsMin) * InvCellSize;
        return ((int)frac.X, (int)frac.Y, (int)frac.Z);
    }
    public Vector3 VoxelToWorld(int x, int y, int z) => BoundsMin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * CellSize;
    public Vector3 VoxelToWorld((int x, int y, int z) v) => VoxelToWorld(v.x, v.y, v.z);

    public (int x, int y, int z) Clamp(int x, int y, int z) => (Math.Clamp(x, 0, NumCellsX), Math.Clamp(y, 0, NumCellsY), Math.Clamp(z, 0, NumCellsZ));
    public (int x, int y, int z) Clamp((int x, int y, int z) v) => Clamp(v.x, v.y, v.z);

    public (int x, int y, int z) IndexToVoxel(int index)
    {
        // TODO: optimize for power-of-two case?
        var y = index % NumCellsY;
        var xz = index / NumCellsY;
        return (xz % NumCellsX, y, xz / NumCellsX);
    }

    public int VoxelToIndex(int x, int y, int z) => (z * NumCellsX + x) * NumCellsY + y;
    public int VoxelToIndex((int x, int y, int z) v) => VoxelToIndex(v.x, v.y, v.z);
}
