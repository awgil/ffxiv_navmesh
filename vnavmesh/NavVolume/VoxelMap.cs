using DotRecast.Recast;
using System;
using System.Collections;
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

    // TODO: remove and rasterize triangles directly?
    public void AddFromHeightfield(RcHeightfield hf, int tx, int tz)
    {
        var hfBorderWorld = hf.borderSize * hf.cs;
        var l0Bounds = RootTile.CalculateSubdivisionBounds((tx, 0, tz));
        if (l0Bounds.min.X != hf.bmin.X + hfBorderWorld || l0Bounds.max.X != hf.bmax.X - hfBorderWorld || l0Bounds.min.Z != hf.bmin.Z + hfBorderWorld || l0Bounds.max.Z != hf.bmax.Z - hfBorderWorld)
            throw new Exception($"Border mismatch: expected {l0Bounds}, got {hf.bmin}-{hf.bmax} + {hf.cs}*{hf.borderSize}");
        if (l0Bounds.min.Y != hf.bmin.Y)
            throw new Exception($"MinY mismatch: expected {l0Bounds.min.Y}, got {hf.bmin.Y}");
        if (Levels.Length != 3)
            throw new Exception($"Unexpected depth: got {Levels.Length}");

        var l0 = Levels[0];
        var l1 = Levels[1];
        var l2 = Levels[2];
        var dv = l2.CellSize / new Vector3(hf.cs, hf.ch, hf.cs);
        int dx = (int)dv.X;
        int dy = (int)dv.Y;
        int dz = (int)dv.Z;
        if (dx != dv.X || dy != dv.Y || dz != dv.Z || !BitOperations.IsPow2(dx) || !BitOperations.IsPow2(dy) || !BitOperations.IsPow2(dz))
            throw new Exception($"Cell size mismatch: {dv}");
        var shiftY = BitOperations.Log2((uint)dy);

        // downsample to L2
        var ny1 = l1.NumCellsY * l0.NumCellsY;
        var nx = l2.NumCellsX * l1.NumCellsX;
        var ny = l2.NumCellsY * ny1;
        var nz = l2.NumCellsZ * l1.NumCellsZ;
        var rawL2 = new BitArray(nx * ny * nz); // this assumes that heightfield corresponds to 1x1xN column; TODO consider swizzle
        int z = hf.borderSize;
        for (int iz = 0; iz < nz; ++iz)
        {
            for (int jz = 0; jz < dz; ++jz, ++z)
            {
                int x = hf.borderSize;
                for (int ix = 0; ix < nx; ++ix)
                {
                    for (int jx = 0; jx < dx; ++jx, ++x)
                    {
                        var offDest = (iz * nx + ix) * ny;
                        var spanIndex = hf.spans[z * hf.width + x];
                        while (spanIndex != 0)
                        {
                            ref var span = ref hf.Span(spanIndex);
                            var y0 = span.smin >> shiftY;
                            var y1 = Math.Min(span.smax >> shiftY, ny - 1);
                            for (int y = y0; y <= y1; ++y)
                                rawL2[offDest + y] = true;
                            spanIndex = span.next;
                        }
                    }
                }
            }
        }

        // downsample to L1
        var rawL1 = new BitArray(l1.NumCellsX * ny1 * l1.NumCellsZ * 2);
        var offSrc = 0;
        for (int iz = 0; iz < l1.NumCellsZ; ++iz)
        {
            for (int jz = 0; jz < l2.NumCellsZ; ++jz)
            {
                for (int ix = 0; ix < l1.NumCellsX; ++ix)
                {
                    for (int jx = 0; jx < l2.NumCellsX; ++jx)
                    {
                        var offDest = (iz * l1.NumCellsX + ix) * ny1 * 2;
                        for (int iy = 0; iy < ny1; ++iy, offDest += 2)
                        {
                            for (int jy = 0; jy < l2.NumCellsY; ++jy)
                            {
                                var v = rawL2[offSrc++] ? 1 : 0;
                                rawL1[offDest + v] = true;
                            }
                        }
                    }
                }
            }
        }

        // subdivide L0
        var l0Index = l0.VoxelToIndex(tx, 0, tz);
        for (int y0 = 0; y0 < l0.NumCellsY; ++y0, ++l0Index)
        {
            bool haveEmpty = false, haveSolid = false;
            offSrc = y0 * l1.NumCellsY * 2;
            for (int z1 = 0; z1 < l1.NumCellsZ; ++z1)
            {
                for (int x1 = 0; x1 < l1.NumCellsX; ++x1)
                {
                    for (int y1 = 0; y1 < l1.NumCellsY; ++y1)
                    {
                        haveEmpty |= rawL1[offSrc + y1 * 2];
                        haveSolid |= rawL1[offSrc + y1 * 2 + 1];
                    }
                    offSrc += ny1 * 2;
                }
            }

            if (!haveSolid)
                continue; // fully empty

            if (!haveEmpty)
            {
                // fully solid
                RootTile.Contents[l0Index] = VoxelOccupiedBit | VoxelIdMask;
                continue;
            }

            // ok, subdivide
            RootTile.Contents[l0Index] = (ushort)(VoxelOccupiedBit | RootTile.Subdivision.Count);
            var (l1Min, l1Max) = RootTile.CalculateSubdivisionBounds(tx, y0, tz);
            var l1Tile = new Tile(this, l1Min, l1Max, 1);
            RootTile.Subdivision.Add(l1Tile);

            // subdivide L1
            offSrc = y0 * l1.NumCellsY * 2;
            for (int z1 = 0; z1 < l1.NumCellsZ; ++z1)
            {
                for (int x1 = 0; x1 < l1.NumCellsX; ++x1)
                {
                    for (int y1 = 0; y1 < l1.NumCellsY; ++y1)
                    {
                        if (!rawL1[offSrc + y1 * 2 + 1])
                        {
                            // fully empty
                        }
                        else if (!rawL1[offSrc + y1 * 2])
                        {
                            // fully solid
                            l1Tile.Contents[l1.VoxelToIndex(x1, y1, z1)] = VoxelOccupiedBit | VoxelIdMask;
                        }
                        else
                        {
                            // subdivide
                            l1Tile.Contents[l1.VoxelToIndex(x1, y1, z1)] = (ushort)(VoxelOccupiedBit | l1Tile.Subdivision.Count);
                            var (l2Min, l2Max) = l1Tile.CalculateSubdivisionBounds(x1, y1, z1);
                            var l2Tile = new Tile(this, l2Min, l2Max, 2);
                            l1Tile.Subdivision.Add(l2Tile);

                            for (int z2 = 0; z2 < l2.NumCellsZ; ++z2)
                            {
                                for (int x2 = 0; x2 < l2.NumCellsX; ++x2)
                                {
                                    for (int y2 = 0; y2 < l2.NumCellsY; ++y2)
                                    {
                                        var index = ((z1 * l2.NumCellsZ + z2) * nx + x1 * l2.NumCellsX + x2) * ny + (y0 * l1.NumCellsY + y1) * l2.NumCellsY + y2;
                                        if (rawL2[index])
                                            l2Tile.Contents[l2.VoxelToIndex(x2, y2, z2)] = VoxelOccupiedBit | VoxelIdMask;
                                    }
                                }
                            }
                        }
                    }
                    offSrc += ny1 * 2;
                }
            }
        }
    }
}
