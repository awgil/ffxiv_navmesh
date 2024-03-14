using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.NavVolume;

public class VoxelMap
{
    public class Level
    {
        public Vector3 CellSize { get; init; }
        public Vector3 InvCellSize { get; init; }
        public int NumCellsX { get; init; }
        public int NumCellsY { get; init; }
        public int NumCellsZ { get; init; }
        public int ShiftYX { get; init; }
        public int ShiftXZ { get; init; }

        public int NumCellsTotal => NumCellsX * NumCellsY * NumCellsZ;

        public Level(Vector3 extent, int nx, int ny, int nz)
        {
            if ((nx & (nx - 1)) != 0 || (ny & (ny - 1)) != 0 || (nz & (nz - 1)) != 0)
                throw new ArgumentException($"Non-power-of-2 cell size not supported: got {nx}*{ny}*{nz}");

            CellSize = extent / new Vector3(nx, ny, nz);
            InvCellSize = new Vector3(1) / CellSize;
            NumCellsX = nx;
            NumCellsY = ny;
            NumCellsZ = nz;
            ShiftYX = BitOperations.Log2((uint)ny);
            ShiftXZ = BitOperations.Log2((uint)nx);
        }

        public Level(Vector3 extent, int ncells) : this(extent, ncells, ncells, ncells) { }

        public bool InBounds(int x, int y, int z) => x >= 0 && x < NumCellsX && y >= 0 && y < NumCellsY && z >= 0 && z < NumCellsZ;
        public bool InBounds((int x, int y, int z) v) => InBounds(v.x, v.y, v.z);

        public (int x, int y, int z) ClampMin(int x, int y, int z) => (Math.Max(x, 0), Math.Max(y, 0), Math.Max(z, 0));
        public (int x, int y, int z) ClampMin((int x, int y, int z) v) => ClampMin(v.x, v.y, v.z);

        public (int x, int y, int z) ClampMax(int x, int y, int z) => (Math.Min(x, NumCellsX - 1), Math.Min(y, NumCellsY - 1), Math.Min(z, NumCellsZ - 1));
        public (int x, int y, int z) ClampMax((int x, int y, int z) v) => ClampMax(v.x, v.y, v.z);

        public (int x, int y, int z) Clamp(int x, int y, int z) => ClampMax(ClampMin(x, y, z));
        public (int x, int y, int z) Clamp((int x, int y, int z) v) => ClampMax(ClampMin(v.x, v.y, v.z));

        public (int x, int y, int z) IndexToVoxel(ushort index)
        {
            var y = index & (NumCellsY - 1);
            var xz = index >> ShiftYX;
            var x = xz & (NumCellsX - 1);
            var z = xz >> ShiftXZ;
            return (x, y, z);
        }

        public ushort VoxelToIndex(int x, int y, int z) => (ushort)((((z << ShiftXZ) + x) << ShiftYX) + y);
        public ushort VoxelToIndex((int x, int y, int z) v) => VoxelToIndex(v.x, v.y, v.z);
    }

    public class Tile
    {
        public VoxelMap Owner { get; init; }
        public Vector3 BoundsMin { get; init; }
        public Vector3 BoundsMax { get; init; }
        public int Level { get; init; }
        public ushort[] Contents { get; init; } // high bit unset: empty voxel (TODO: region id in low bits?); high bit set: voxel with solid geometry (VoxelIdMask if leaf, subvoxel index otherwise); order is (y,x,z)
        public List<Tile> Subdivision { get; init; } = new();

        public Level LevelDesc => Owner.Levels[Level];

        public Tile(VoxelMap owner, Vector3 boundsMin, Vector3 boundsMax, int level)
        {
            Owner = owner;
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            Level = level;
            Contents = new ushort[owner.Levels[level].NumCellsTotal];
        }

        public (int x, int y, int z) WorldToVoxel(Vector3 v)
        {
            var frac = (v - BoundsMin) * LevelDesc.InvCellSize;
            return ((int)frac.X, (int)frac.Y, (int)frac.Z);
        }
        public Vector3 VoxelToWorld(int x, int y, int z) => BoundsMin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * LevelDesc.CellSize;
        public Vector3 VoxelToWorld((int x, int y, int z) v) => VoxelToWorld(v.x, v.y, v.z);

        public (Vector3 min, Vector3 max) CalculateSubdivisionBounds(int x, int y, int z)
        {
            var ld = LevelDesc;
            var min = BoundsMin + new Vector3(x, y, z) * ld.CellSize;
            return (min, min + ld.CellSize);
        }
        public (Vector3 min, Vector3 max) CalculateSubdivisionBounds((int x, int y, int z) v) => CalculateSubdivisionBounds(v.x, v.y, v.z);

        public (ulong index, bool empty) FindLeafVoxel(Vector3 p, bool checkBounds = true)
        {
            var v = WorldToVoxel(p);
            if (checkBounds && !LevelDesc.InBounds(v))
                return (InvalidVoxel, false); // out of bounds; consider everything outside to be occupied

            var idx = LevelDesc.VoxelToIndex(v);
            var data = Contents[idx];
            if ((data & VoxelOccupiedBit) == 0)
            {
                return (EncodeIndex(idx), true); // empty at this level
            }
            data &= VoxelIdMask;
            if (data == VoxelIdMask)
            {
                return (EncodeIndex(idx), false); // occupied leaf
            }
            else
            {
                var sub = Subdivision[data].FindLeafVoxel(p, false); // guaranteed to be in bounds
                return (EncodeIndex(idx, sub.index), sub.empty);
            }
        }

        public IEnumerable<(ulong index, bool empty)> EnumerateLeafVoxels(Vector3 bmin, Vector3 bmax)
        {
            var ld = LevelDesc;
            var vmin = ld.ClampMin(WorldToVoxel(bmin));
            var vmax = ld.ClampMax(WorldToVoxel(bmax));
            for (int z = vmin.z; z <= vmax.z; z++)
            {
                for (int x = vmin.x; x <= vmax.x; ++x)
                {
                    for (int y = vmin.y; y <= vmax.y; ++y)
                    {
                        var idx = ld.VoxelToIndex(x, y, z);
                        var data = Contents[idx];
                        if ((data & VoxelOccupiedBit) == 0)
                        {
                            yield return (EncodeIndex(idx), true); // empty at this level
                            continue;
                        }
                        data &= VoxelIdMask;
                        if (data == VoxelIdMask)
                        {
                            yield return(EncodeIndex(idx), false); // occupied leaf
                        }
                        else
                        {
                            foreach (var sub in Subdivision[data].EnumerateLeafVoxels(bmin, bmax))
                                yield return (EncodeIndex(idx, sub.index), sub.empty);
                        }
                    }
                }
            }
        }
    }

    public Level[] Levels { get; init; }
    public Tile RootTile { get; init; }

    public const ushort VoxelOccupiedBit = 0x8000;
    public const ushort VoxelIdMask = 0x7fff;

    // voxel index is stored as (... L2 L1 L0)
    public const ushort IndexLevelMask = 0xffff;
    public const int IndexLevelShift = 16;

    public const ulong InvalidVoxel = ulong.MaxValue;

    public static ulong EncodeIndex(ushort tileIndex, ulong subIndex = InvalidVoxel) => (subIndex << IndexLevelShift) + tileIndex;
    public static ulong EncodeSubIndex(ulong voxel, ushort tileIndex, int level)
    {
        var shift = IndexLevelShift * level;
        voxel &= ~((ulong)IndexLevelMask << shift);
        voxel |= (ulong)tileIndex << shift;
        return voxel;
    }
    public static ushort DecodeIndex(ref ulong index)
    {
        var tileIndex = (ushort)(index & IndexLevelMask);
        index >>= IndexLevelShift;
        return tileIndex;
    }

    public VoxelMap(Vector3 boundsMin, Vector3 boundsMax, NavmeshSettings settings)
    {
        Levels = new Level[settings.NumTiles.Length];
        var levelExtent = boundsMax - boundsMin;
        for (int i = 0; i < Levels.Length; ++i)
        {
            Levels[i] = new(levelExtent, settings.NumTiles[i]);
            levelExtent = Levels[i].CellSize;
        }

        RootTile = new(this, boundsMin, boundsMax, 0);
    }

    public bool IsEmpty(ulong voxel)
    {
        var tile = RootTile;
        while (true)
        {
            var tileIndex = DecodeIndex(ref voxel);
            if (tileIndex == IndexLevelMask)
                return false; // asking for non-leaf => consider non-empty

            var data = tile.Contents[tileIndex];
            if ((data & VoxelOccupiedBit) == 0)
                return true; // found empty voxel
            data &= VoxelIdMask;
            if (data == VoxelIdMask)
                return false; // found non-empty leaf
            tile = tile.Subdivision[data];
        }
    }

    public (ulong voxel, bool empty) FindLeafVoxel(Vector3 p) => RootTile.FindLeafVoxel(p);

    public (Vector3 min, Vector3 max) VoxelBounds(ulong voxel, float eps)
    {
        var tile = RootTile;
        var eps3 = new Vector3(eps);
        while (true)
        {
            var tileIndex = DecodeIndex(ref voxel);
            if (tileIndex == IndexLevelMask)
            {
                return (tile.BoundsMin + eps3, tile.BoundsMax - eps3);
            }

            var data = tile.Contents[tileIndex];
            var id = data & VoxelIdMask;
            if ((data & VoxelOccupiedBit) == 0 || id == VoxelIdMask)
            {
                var bb = tile.CalculateSubdivisionBounds(Levels[tile.Level].IndexToVoxel(tileIndex));
                return (bb.min + eps3, bb.max - eps3);
            }

            tile = tile.Subdivision[id];
        }
    }

    public void Build(Voxelizer vox, int tx, int tz)
    {
        // downsample
        var voxelizers = new Voxelizer[Levels.Length];
        voxelizers[Levels.Length - 1] = vox;
        for (int i = Levels.Length - 1; i > 0; --i)
        {
            ref var l = ref Levels[i];
            voxelizers[i - 1] = voxelizers[i].Downsample(l.NumCellsX, l.NumCellsY, l.NumCellsZ);
        }

        // subdivide L0
        var ny = Levels[0].NumCellsY;
        var idx = Levels[0].VoxelToIndex(tx, 0, tz);
        for (int ty = 0; ty < ny; ++ty, ++idx)
        {
            BuildTile(RootTile, idx, 0, ty, 0, voxelizers);
        }
    }

    private void BuildTile(Tile parent, ushort index, int x0, int y0, int z0, Span<Voxelizer> source)
    {
        var (solid, empty) = source[0].Get(x0, y0, z0);
        if (empty)
        {
            // nothing to do
        }
        else if (solid)
        {
            parent.Contents[index] = VoxelOccupiedBit | VoxelIdMask;
        }
        else
        {
            parent.Contents[index] = (ushort)(VoxelOccupiedBit | parent.Subdivision.Count);
            var (min, max) = parent.CalculateSubdivisionBounds(parent.LevelDesc.IndexToVoxel(index));
            var tile = new Tile(this, min, max, parent.Level + 1);
            parent.Subdivision.Add(tile);

            // subdivide
            ref var l = ref Levels[tile.Level];
            int xOff = x0 * l.NumCellsX;
            int yOff = y0 * l.NumCellsY;
            int zOff = z0 * l.NumCellsZ;
            ushort i = 0;
            for (int z = 0; z < l.NumCellsZ; ++z)
            {
                for (int x = 0; x < l.NumCellsX; ++x)
                {
                    for (int y = 0; y < l.NumCellsY; ++y, ++i)
                    {
                        BuildTile(tile, i, xOff + x, yOff + y, zOff + z, source.Slice(1));
                    }
                }
            }
        }
    }
}
