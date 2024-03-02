using DotRecast.Recast;
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
        public ushort[] Contents { get; init; } // high bit unset: empty voxel (TODO: region id in low bits); high bit set: voxel with solid geometry (subvoxel index in lower bits if non-leaf); order is (y,x,z)
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
            else if (Level + 1 == Owner.Levels.Length)
            {
                return (EncodeIndex(idx), false); // occupied leaf
            }
            else
            {
                var sub = Subdivision[data & VoxelIdMask].FindLeafVoxel(p, false); // guaranteed to be in bounds
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
                        }
                        else if (Level + 1 == Owner.Levels.Length)
                        {
                            yield return(EncodeIndex(idx), false); // occupied leaf
                        }
                        else
                        {
                            foreach (var sub in Subdivision[data & VoxelIdMask].EnumerateLeafVoxels(bmin, bmax))
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

    public VoxelMap(Vector3 boundsMin, Vector3 boundsMax)
    {
        Levels = new Level[NavmeshSettings.NumTiles.Length];
        var levelExtent = boundsMax - boundsMin;
        for (int i = 0; i < Levels.Length; ++i)
        {
            Levels[i] = new(levelExtent, NavmeshSettings.NumTiles[i]);
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

            //Service.Log.Debug($"testing {tileIndex} (s={tile.Contents.Length})");
            var data = tile.Contents[tileIndex];
            if ((data & VoxelOccupiedBit) == 0)
                return true; // found empty voxel
            if (tile.Level + 1 == Levels.Length)
                return false; // found non-empty leaf
            //Service.Log.Debug($"-> {data:X} (s={tile.Subdivision.Count})");
            tile = tile.Subdivision[data & VoxelIdMask];
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
            if ((data & VoxelOccupiedBit) == 0 || tile.Level + 1 == Levels.Length)
            {
                var bb = tile.CalculateSubdivisionBounds(Levels[tile.Level].IndexToVoxel(tileIndex));
                return (bb.min + eps3, bb.max - eps3);
            }

            tile = tile.Subdivision[data & VoxelIdMask];
        }
    }

    // TODO: remove, rasterize triangles directly
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
                    AddFromHeightfieldSpan(RootTile, x, z, y0, y1);
                    span = span.next;
                }
                x += hf.cs;
            }
            z += hf.cs;
        }
    }

    private void AddFromHeightfieldSpan(Tile tile, float x, float z, float y0, float y1)
    {
        var ld = tile.LevelDesc;
        var v0 = tile.WorldToVoxel(new(x, y0, z));
        if (v0.x >= 0 && v0.x < ld.NumCellsX && v0.z >= 0 && v0.z < ld.NumCellsZ)
        {
            var vy0 = Math.Max(0, v0.y);
            var vy1 = Math.Min(ld.NumCellsY, (int)Math.Ceiling((y1 - tile.BoundsMin.Y) * ld.InvCellSize.Y));
            for (int vy = vy0; vy < vy1; ++vy)
            {
                ref ushort voxel = ref tile.Contents[ld.VoxelToIndex(v0.x, vy, v0.z)];
                if (tile.Level + 1 == Levels.Length)
                {
                    // last level, just mark voxel as non-empty
                    voxel = VoxelOccupiedBit;
                }
                else
                {
                    // create subdivision if not already done, then rasterize inside it
                    if (voxel == 0)
                    {
                        voxel = (ushort)(VoxelOccupiedBit | tile.Subdivision.Count);
                        var (bmin, bmax) = tile.CalculateSubdivisionBounds(v0.x, vy, v0.z);
                        tile.Subdivision.Add(new(this, bmin, bmax, tile.Level + 1));
                    }
                    AddFromHeightfieldSpan(tile.Subdivision[voxel & VoxelIdMask], x, z, y0, y1);
                }
            }
        }
    }
}
