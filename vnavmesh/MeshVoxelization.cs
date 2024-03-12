using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

public class MeshVoxelization
{
    [Flags]
    public enum Voxel : byte
    {
        None = 0,
        Border = 1 << 0,
        Walkable = 1 << 1,
        Landable = 1 << 2,
        Interior = 1 << 3,
    }

    public struct Span
    {
        public int X;
        public int Z;
        public int Y0;
        public int Y1;
        public Voxel TopFlags;
    }

    public class Compressed
    {
        public Vector3 BoundsMin;
        public Vector3 InvCellSize;
        public List<Span> Spans = new();
    }

    public Vector3 CellSize { get; init; }
    public Vector3 InvCellSize { get; init; }
    public int MinCellX { get; init; }
    public int MinCellY { get; init; }
    public int MinCellZ { get; init; }
    public int NumCellsX { get; init; }
    public int NumCellsY { get; init; }
    public int NumCellsZ { get; init; }
    public Voxel[] Voxels { get; init; } // order is (y,x,z)

    public int MaxCellX => MinCellX + NumCellsX;
    public int MaxCellY => MinCellY + NumCellsY;
    public int MaxCellZ => MinCellZ + NumCellsZ;
    public Vector3 BoundsMin => new Vector3(MinCellX, MinCellY, MinCellZ) * CellSize;
    public Vector3 BoundsMax => new Vector3(MaxCellX, MaxCellY, MaxCellZ) * CellSize;

    public MeshVoxelization(Vector3 cellSize, Vector3 boundsMin, Vector3 boundsMax)
    {
        CellSize = cellSize;
        InvCellSize = new Vector3(1.0f) / cellSize;
        var cmin = (boundsMin * InvCellSize).Floor();
        var cmax = (boundsMax * InvCellSize).Ceiling();
        MinCellX = (int)cmin.X;
        MinCellY = (int)cmin.Y;
        MinCellZ = (int)cmin.Z;
        NumCellsX = Math.Max((int)(cmax.X - cmin.X), 0) + 1;
        NumCellsY = Math.Max((int)(cmax.Y - cmin.Y), 0) + 1;
        NumCellsZ = Math.Max((int)(cmax.Z - cmin.Z), 0) + 1;
        Voxels = new Voxel[NumCellsX * NumCellsY * NumCellsZ];
    }

    public void Voxelize(SceneExtractor.Mesh mesh, SceneExtractor.MeshInstance instance, float walkableNormalThreshold)
    {
        Span<Vector3> worldVertices = stackalloc Vector3[256];
        Span<Vector3> clipRemainingZ = stackalloc Vector3[7];
        Span<Vector3> clipRemainingX = stackalloc Vector3[7];
        Span<Vector3> clipScratch = stackalloc Vector3[7];
        Span<Vector3> clipCell = stackalloc Vector3[7];
        foreach (var part in mesh.Parts)
        {
            // fill vertex buffer
            int iv = 0;
            foreach (var v in part.Vertices)
                worldVertices[iv++] = instance.WorldTransform.TransformCoordinate(v);

            foreach (var p in part.Primitives)
            {
                var flags = (p.Flags & ~instance.ForceClearPrimFlags) | instance.ForceSetPrimFlags;
                if (flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough))
                    continue; // TODO: rasterize to normal heightfield, can't do it right now, since we're using same heightfield for both mesh and volume

                var v1 = worldVertices[p.V1];
                var v2 = worldVertices[p.V2];
                var v3 = worldVertices[p.V3];

                var voxel = Voxel.Border;
                if (!flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceUnwalkable))
                {
                    var v12 = v2 - v1;
                    var v13 = v3 - v1;
                    var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
                    if (normal.Y >= walkableNormalThreshold)
                        voxel |= Voxel.Walkable;
                }
                if (!flags.HasFlag(SceneExtractor.PrimitiveFlags.Unlandable))
                    voxel |= Voxel.Landable;

                // prepare for clipping: while iterating over z, we'll keep the 'remaining polygon' in clipRemainingZ
                int numRemainingZ = 0;
                clipRemainingZ[numRemainingZ++] = v1;
                clipRemainingZ[numRemainingZ++] = v2;
                clipRemainingZ[numRemainingZ++] = v3;

                // calculate the footprint of the triangle on the grid's z-axis
                var (minZ, maxZ) = AxisMinMax(clipRemainingZ, numRemainingZ, 2);
                int z0 = (int)(minZ * InvCellSize.Z) - MinCellZ;
                int z1 = (int)(maxZ * InvCellSize.Z) - MinCellZ;
                if (z1 < 0 || z0 >= NumCellsZ)
                    continue;
                z0 = Math.Clamp(z0, -1, NumCellsZ - 1); // use -1 rather than 0 to cut the polygon properly at the start of the tile
                z1 = Math.Clamp(z1, 0, NumCellsZ - 1);

                for (int z = z0; z <= z1; ++z)
                {
                    if (numRemainingZ < 3)
                        break;

                    // clip polygon to 'row'
                    var cellZMin = (MinCellZ + z) * CellSize.Z;
                    int numRemainingX = SplitConvexPoly(clipRemainingZ, clipRemainingX, clipScratch, ref numRemainingZ, 2, cellZMin + CellSize.Z);

                    // previous buffer is now new scratch
                    var swapZ = clipRemainingZ;
                    clipRemainingZ = clipScratch;
                    clipScratch = swapZ;

                    if (numRemainingX < 3 || z < 0)
                        continue;

                    // find x bounds of the row
                    var (minX, maxX) = AxisMinMax(clipRemainingX, numRemainingX, 0);
                    int x0 = (int)(minX * InvCellSize.X) - MinCellX;
                    int x1 = (int)(maxX * InvCellSize.X) - MinCellX;
                    if (x1 < 0 || x0 >= NumCellsX)
                        continue;
                    x0 = Math.Clamp(x0, -1, NumCellsX - 1);
                    x1 = Math.Clamp(x1, 0, NumCellsX - 1);

                    var zOff = z * NumCellsX;
                    for (int x = x0; x <= x1; ++x)
                    {
                        if (numRemainingX < 3)
                            break;

                        // clip polygon to 'column'
                        var cellXMin = (MinCellX + x) * CellSize.X;
                        int numCell = SplitConvexPoly(clipRemainingX, clipCell, clipScratch, ref numRemainingX, 0, cellXMin + CellSize.X);

                        // previous buffer is now new scratch
                        var swapX = clipRemainingX;
                        clipRemainingX = clipScratch;
                        clipScratch = swapX;

                        if (numCell < 3 || x < 0)
                            continue;

                        // find y bounds of the cell
                        var (minY, maxY) = AxisMinMax(clipCell, numCell, 1);
                        int y0 = (int)(minY * InvCellSize.Y) - MinCellY;
                        int y1 = (int)(maxY * InvCellSize.Y) - MinCellY;
                        if (y1 < 0 || y0 >= NumCellsY)
                            continue;
                        y0 = Math.Clamp(y0, 0, NumCellsY - 1);
                        y1 = Math.Clamp(y1, 0, NumCellsY - 1);

                        var xzOff = (zOff + x) * NumCellsY;
                        for (int y = y0; y <= y1; ++y)
                            Voxels[xzOff + y] |= voxel; // TODO: think about flag merging...
                    }
                }
            }
        }
    }

    public void FillInterior()
    {
        // expand all cells accessible to exterior iteratively with 'interior' flag, then invert
        List<(int, int, int)> expand = new();
        for (int z = 0; z < NumCellsZ; ++z)
        {
            for (int x = 0; x < NumCellsX; ++x)
            {
                if (z == 0 || z == NumCellsZ - 1 || x == 0 || x == NumCellsX - 1)
                {
                    for (int y = 0; y < NumCellsY; ++y)
                        MarkAsExterior(x, y, z, expand);
                }
                else
                {
                    MarkAsExterior(x, 0, z, expand);
                    MarkAsExterior(x, NumCellsY - 1, z, expand);
                }
            }
        }

        while (expand.Count > 0)
        {
            var (x, y, z) = expand[expand.Count - 1];
            expand.RemoveAt(expand.Count - 1);
            if (z > 0)
                MarkAsExterior(x, y, z - 1, expand);
            if (z < NumCellsZ - 1)
                MarkAsExterior(x, y, z + 1, expand);
            if (x > 0)
                MarkAsExterior(x - 1, y, z, expand);
            if (x < NumCellsX - 1)
                MarkAsExterior(x + 1, y, z, expand);
            if (y > 0)
                MarkAsExterior(x, y - 1, z, expand);
            if (y < NumCellsY - 1)
                MarkAsExterior(x, y + 1, z, expand);
        }

        // invert exterior flags
        foreach (ref var v in Voxels.AsSpan())
            v ^= Voxel.Interior;
    }

    public Compressed Compress(int minGap)
    {
        var res = new Compressed();
        res.BoundsMin = BoundsMin;
        res.InvCellSize = InvCellSize;

        int icell = 0;
        for (int z = 0; z < NumCellsZ; ++z)
        {
            for (int x = 0; x < NumCellsX; ++x)
            {
                int y = 0;
                int gap = GetGapHeight(y, icell);
                while (true)
                {
                    y += gap;
                    icell += gap;

                    if (y == NumCellsY)
                        break; // reached the end without finding a new span

                    // ok, we have a span to add
                    int spanStart = y; // start of the next span

                    int solid = GetSolidHeight(y, icell);
                    y += solid;
                    icell += solid;

                    gap = GetGapHeight(y, icell);
                    while (gap > 0 && gap < minGap && y + gap < NumCellsY)
                    {
                        // the next gap is too small, consume it
                        y += gap;
                        icell += gap;

                        solid = GetSolidHeight(y, icell);
                        y += solid;
                        icell += solid;

                        gap = GetGapHeight(y, icell);
                    }

                    // we've either reached the top or a long gap
                    res.Spans.Add(new() { X = x, Z = z, Y0 = spanStart, Y1 = y, TopFlags = Voxels[icell - 1] });
                }
            }
        }
        return res;
    }

    private (float min, float max) AxisMinMax(Span<Vector3> vertices, int count, int axis)
    {
        float min = vertices[0][axis];
        float max = min;
        for (int i = 1; i < count; ++i)
        {
            float v = vertices[i][axis];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return (min, max);
    }

    // split a convex polygon along one of the axes
    // polygon 'smaller' than axis offset is to be processed next, rest is considered to be a leftover to process on the next iteration
    // count on input contains num vertices in src buffer, on output num vertices in remaining buffer
    private static int SplitConvexPoly(Span<Vector3> src, Span<Vector3> dest, Span<Vector3> remaining, ref int count, int axis, float axisOffset)
    {
        Span<float> axisDelta = stackalloc float[count];
        for (int i = 0; i < count; ++i)
            axisDelta[i] = axisOffset - src[i][axis];

        int cDest = 0, cRem = 0;
        var dPrev = axisDelta[count - 1];
        var vPrev = src[count - 1];
        for (int i = 0; i < count; ++i)
        {
            var dCurr = axisDelta[i];
            var vCurr = src[i];
            if ((dCurr >= 0) != (dPrev >= 0))
            {
                // two vertices are on the different sides of the separating axis
                float s = dPrev / (dPrev - dCurr);
                dest[cDest++] = remaining[cRem++] = vPrev + (vCurr - vPrev) * s;

                // add the i'th point to the right polygon; do NOT add points that are on the dividing line since these were already added above
                if (dCurr > 0)
                    dest[cDest++] = vCurr;
                else if (dCurr < 0)
                    remaining[cRem++] = vCurr;
            }
            else
            {
                // add the i'th point to the right polygon; addition is done even for points on the dividing line
                if (dCurr > 0)
                {
                    dest[cDest++] = vCurr;
                }
                else if (dCurr < 0)
                {
                    remaining[cRem++] = vCurr;
                }
                else
                {
                    dest[cDest++] = vCurr;
                    remaining[cRem++] = vCurr;
                }
            }
            dPrev = dCurr;
            vPrev = vCurr;
        }
        count = cRem;
        return cDest;
    }

    private void MarkAsExterior(int x, int y, int z, List<(int, int, int)> expand)
    {
        var idx = ((z * NumCellsX) + x) * NumCellsY + y;
        if (Voxels[idx] != Voxel.None)
            return;
        Voxels[idx] |= Voxel.Interior;
        expand.Add((x, y, z));
    }

    private int GetGapHeight(int y0, int icell)
    {
        int y = y0;
        while (y < NumCellsY && Voxels[icell++] == Voxel.None)
            ++y;
        return y - y0;
    }

    private int GetSolidHeight(int y0, int icell)
    {
        int y = y0;
        while (y < NumCellsY && Voxels[icell++] != Voxel.None)
            ++y;
        return y - y0;
    }
}
