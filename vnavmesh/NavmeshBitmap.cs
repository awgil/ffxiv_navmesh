using DotRecast.Detour;
using System;
using System.Numerics;

namespace Navmesh;

public class NavmeshBitmap
{
    public Vector3 MinBounds;
    public Vector3 MaxBounds;
    public float Resolution;
    public float InvResolution;
    public int Width;
    public int Height;
    public byte[] Data; // 1 if walkable

    private Vector3 GetVertex(DtMeshTile tile, int i) => new(tile.data.verts[i * 3], tile.data.verts[i * 3 + 1], tile.data.verts[i * 3 + 2]);

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInPolygon(ReadOnlySpan<Vector2> verts, ReadOnlySpan<Vector2> edges, Vector2 p)
    {
        var orient = Cross(p - verts[0], edges[0]);
        //Service.Log.Debug($"base {p} x {verts[0]} x {verts[^1]} = {orient}");
        if (orient == 0)
            return true;
        for (int i = 1; i < verts.Length; ++i)
        {
            var cur = Cross(p - verts[i], edges[i]);
            //Service.Log.Debug($"oth {p} x {verts[i]} x {verts[i - 1]} = {cur}");
            if (cur == 0)
                return true;
            if (cur * orient < 1)
                return false;
        }
        return true;
    }

    private void RasterizePolygon(DtMeshTile tile, DtPoly poly)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        Span<Vector2> verts = stackalloc Vector2[poly.vertCount];
        for (int i = 0; i < poly.vertCount; ++i)
        {
            var v = GetVertex(tile, poly.verts[i]);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
            verts[i] = new(v.X, v.Z);
        }
        if (min.Y > MaxBounds.Y || max.Y < MinBounds.Y)
            return; // polygon fully outside vertical bounds

        Span<Vector2> edges = stackalloc Vector2[poly.vertCount];
        edges[0] = verts[^1] - verts[0];
        for (int i = 1; i < poly.vertCount; ++i)
            edges[i] = verts[i - 1] - verts[i];

        int x0 = Math.Clamp((int)MathF.Floor((min.X - MinBounds.X) * InvResolution), 0, Width - 1);
        int z0 = Math.Clamp((int)MathF.Floor((min.Z - MinBounds.Z) * InvResolution), 0, Height - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling((max.X - MinBounds.X) * InvResolution), 0, Width - 1);
        int z1 = Math.Clamp((int)MathF.Ceiling((max.Z - MinBounds.Z) * InvResolution), 0, Height - 1);
        //Service.Log.Debug($"{x0},{z0} - {x1},{z1}");
        //for (int i = 0; i < poly.vertCount; ++i)
        //    Service.Log.Debug($"[{i}] {verts[i]} ({edges[i]})");
        Vector2 cz = new(MinBounds.X + (x0 + 0.5f) * Resolution, MinBounds.Z + (z0 + 0.5f) * Resolution);
        var iz = (Height - 1 - z0) * Width + x0; // TODO: z0 * Width to remove z inversion
        for (int z = z0; z <= z1; ++z)
        {
            var cx = cz;
            var ix = iz;
            for (int x = x0; x <= x1; ++x)
            {
                var inside = PointInPolygon(verts, edges, cx);
                //Service.Log.Debug($"test {x},{z} ({cx}) = {inside}");
                if (inside)
                    Data[ix >> 3] |= (byte)(1 << (ix & 7));
                ++ix;
                cx.X += Resolution;
            }
            iz -= Width; // TODO += to remove z inversion
            cz.Y += Resolution;
        }
    }

    public NavmeshBitmap(Navmesh mesh, Vector3 min, Vector3 max, float resolution)
    {
        MinBounds = min;
        MaxBounds = max;
        Resolution = resolution;
        InvResolution = 1 / resolution;
        Width = (int)MathF.Ceiling((max.X - min.X) * InvResolution);
        Width = (Width + 31) & ~31; // round up to multiple of 32
        Height = (int)MathF.Ceiling((max.Z - min.Z) * InvResolution);
        Data = new byte[Width * Height >> 3];
        for (int i = 0, numTiles = mesh.Mesh.GetParams().maxTiles; i < numTiles; ++i)
        {
            //if (i != 9)
            //    continue;
            var tile = mesh.Mesh.GetTile(i);
            if (tile.data == null)
                continue;

            for (int j = 0; j < tile.data.header.polyCount; ++j)
            {
                //if (j != 583)
                //    continue;
                var p = tile.data.polys[j];
                if (p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION && p.vertCount >= 3)
                {
                    RasterizePolygon(tile, p);
                }
            }
        }
    }
}
