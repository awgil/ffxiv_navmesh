using DotRecast.Detour;
using SharpDX.Win32;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh;

// this is all stolen from vbm - utility for working with 2d 1bpp bitmaps
// some notes:
// - supports only BITMAPINFOHEADER (could've been BITMAPCOREHEADER, but bottom-up bitmaps don't make sense with FF coordinate system)
// - supports only 1bpp bitmaps without compression; per bitmap spec, first pixel is highest bit, etc.
// - supports only top-down bitmaps (with negative height)
// - horizontal/vertical resolution is equal and is 'pixels per 1024 world units'
// - per bitmap spec, rows are padded to 4 byte alignment
public sealed class NavmeshBitmap
{
    [StructLayout(LayoutKind.Explicit, Size = 14)]
    public struct FileHeader
    {
        [FieldOffset(0)] public ushort Type; // 0x4D42 'BM'
        [FieldOffset(2)] public int Size; // size of the file in bytes
        [FieldOffset(6)] public uint Reserved;
        [FieldOffset(10)] public int OffBits; // offset from this to pixel data
    }
    public const ushort Magic = 0x4D42;

    public readonly float PixelSize;
    public readonly float Resolution;
    public readonly Vector3 MinBounds;
    public readonly Vector3 MaxBounds;
    public readonly int Width;
    public readonly int Height;
    public readonly int BytesPerRow;
    public readonly byte[] Pixels; // 1 if unwalkable

    public int CoordToIndex(int x, int y) => y * BytesPerRow + (x >> 3);
    public byte CoordToMask(int x) => (byte)(0x80u >> (x & 7));
    public ref byte ByteAt(int x, int y) => ref Pixels[CoordToIndex(x, y)];

    public bool this[int x, int y]
    {
        get => (ByteAt(x, y) & CoordToMask(x)) != 0;
        set
        {
            if (value)
                ByteAt(x, y) |= CoordToMask(x);
            else
                ByteAt(x, y) &= (byte)~CoordToMask(x);
        }
    }

    // note: pixelSize should be power-of-2
    public NavmeshBitmap(Vector3 min, Vector3 max, float pixelSize)
    {
        PixelSize = pixelSize;
        Resolution = 1.0f / pixelSize;
        MinBounds = (min * Resolution).Floor() * PixelSize;
        MaxBounds = (max * Resolution).Ceiling() * PixelSize;
        Width = (int)((MaxBounds.X - MinBounds.X) * Resolution);
        Height = (int)((MaxBounds.Z - MinBounds.Z) * Resolution);
        BytesPerRow = (Width + 31) >> 5 << 2;
        Pixels = new byte[Height * BytesPerRow];
        Array.Fill(Pixels, (byte)0xFF);
    }

    public void Save(string filename)
    {
        var intRes = (int)(1024 * Resolution);
        using var fstream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.Read);
        var headerSize = Marshal.SizeOf<FileHeader>() + Marshal.SizeOf<BitmapInfoHeader>() + 2 * Marshal.SizeOf<uint>();
        WriteStruct(fstream, new FileHeader() { Type = Magic, Size = headerSize + Pixels.Length, OffBits = headerSize });
        WriteStruct(fstream, new BitmapInfoHeader() { SizeInBytes = Marshal.SizeOf<BitmapInfoHeader>(), Width = Width, Height = -Height, PlaneCount = 1, BitCount = 1, XPixelsPerMeter = intRes, YPixelsPerMeter = intRes });
        WriteStruct(fstream, 0u);
        WriteStruct(fstream, 0xffff7f00u);
        fstream.Write(Pixels);
    }

    public void RasterizePolygon(DtNavMesh mesh, long poly)
    {
        mesh.GetTileAndPolyByRefUnsafe(poly, out var t, out var p);
        RasterizePolygon(t, p);
    }

    public void RasterizePolygon(DtMeshTile tile, DtPoly poly)
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

        int x0 = Math.Clamp((int)MathF.Floor((min.X - MinBounds.X) * Resolution), 0, Width - 1);
        int z0 = Math.Clamp((int)MathF.Floor((min.Z - MinBounds.Z) * Resolution), 0, Height - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling((max.X - MinBounds.X) * Resolution), 0, Width - 1);
        int z1 = Math.Clamp((int)MathF.Ceiling((max.Z - MinBounds.Z) * Resolution), 0, Height - 1);
        //Service.Log.Debug($"{x0},{z0} - {x1},{z1} ({min}-{max} vs {MinBounds}-{MaxBounds})");
        //for (int i = 0; i < poly.vertCount; ++i)
        //    Service.Log.Debug($"[{i}] {verts[i]} ({edges[i]})");
        Vector2 cz = new(MinBounds.X + (x0 + 0.5f) * PixelSize, MinBounds.Z + (z0 + 0.5f) * PixelSize);
        for (int z = z0; z <= z1; ++z)
        {
            var cx = cz;
            for (int x = x0; x <= x1; ++x)
            {
                var inside = PointInPolygon(verts, edges, cx);
                //Service.Log.Debug($"test {x},{z} ({cx}) = {inside}");
                if (inside)
                    this[x, z] = false;
                cx.X += PixelSize;
            }
            cz.Y += PixelSize;
        }
    }

    public static Vector3 GetVertex(DtMeshTile tile, int i) => new(tile.data.verts[i * 3], tile.data.verts[i * 3 + 1], tile.data.verts[i * 3 + 2]);

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInPolygon(ReadOnlySpan<Vector2> verts, ReadOnlySpan<Vector2> edges, Vector2 p)
    {
        float orient = 0;
        for (int i = 0; i < verts.Length; ++i)
        {
            var cur = Cross(p - verts[i], edges[i]);
            //Service.Log.Debug($"> {p} x {verts[i]} x {edges[i]} = {cur}");
            if (orient == 0)
                orient = cur;
            else if ((cur > 0 && orient < 0) || (cur < 0 && orient > 0))
                return false;
        }
        return true;
    }

    public static unsafe T ReadStruct<T>(Stream stream) where T : unmanaged
    {
        T res = default;
        stream.ReadExactly(new(&res, sizeof(T)));
        return res;
    }

    public static unsafe void WriteStruct<T>(Stream stream, in T value) where T : unmanaged
    {
        fixed (T* ptr = &value)
            stream.Write(new(ptr, sizeof(T)));
    }
}
