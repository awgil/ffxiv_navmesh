using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Navmesh.NavVolume;
using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Navmesh;

// full set of data needed for navigation in the zone
public record class Navmesh(DtNavMesh Mesh, VoxelMap? Volume)
{
    public static readonly uint Magic = 0x444D564E; // 'NVMD'
    public static readonly uint Version = 11;

    // throws an exception on failure
    public static Navmesh Deserialize(BinaryReader reader, NavmeshSettings settings)
    {
        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();
        if (magic != Magic || version != Version)
            throw new Exception("Incorrect header");
        var meshLen = reader.ReadInt32();
        var volumeLen = reader.ReadInt32();

        var mesh = DeserializeNavmesh(reader, meshLen);
        var volume = volumeLen > 0 ? DeserializeNavvolume(reader, settings) : null;
        return new(mesh, volume);
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(0); // mesh size placeholder
        writer.Write(0); // volume size placeholder
        var payloadStartPos = (int)writer.Seek(0, SeekOrigin.Current);

        SerializeNavmesh(writer, Mesh);
        int meshLen = (int)writer.Seek(0, SeekOrigin.Current) - payloadStartPos;

        int volumeLen = 0;
        if (Volume != null)
        {
            SerializeNavvolume(writer, Volume);
            volumeLen = (int)writer.Seek(0, SeekOrigin.Current) - payloadStartPos - meshLen;
        }

        writer.Seek(payloadStartPos - 8, SeekOrigin.Begin);
        writer.Write(meshLen);
        writer.Write(volumeLen);
        writer.Seek(payloadStartPos + meshLen + volumeLen, SeekOrigin.Begin);

    }

    private static DtNavMesh DeserializeNavmesh(BinaryReader reader, int length)
    {
        using var compressedReader = new BinaryReader(new BrotliStream(reader.BaseStream, CompressionMode.Decompress, true));
        return new DtMeshSetReader().Read(new RcByteBuffer(compressedReader.ReadBytes(length)));
    }

    private static void SerializeNavmesh(BinaryWriter writer, DtNavMesh mesh)
    {
        using var compressedWriter = new BinaryWriter(new BrotliStream(writer.BaseStream, CompressionLevel.Optimal, true));
        new DtMeshSetWriter().Write(compressedWriter, mesh, RcByteOrder.LITTLE_ENDIAN, false);
    }

    private static VoxelMap DeserializeNavvolume(BinaryReader reader, NavmeshSettings settings)
    {
        using var compressedReader = new BinaryReader(new BrotliStream(reader.BaseStream, CompressionMode.Decompress, true));
        var (min, max) = DeserializeBounds(compressedReader);
        var volume = new VoxelMap(min, max, settings);
        DeserializeTile(compressedReader, volume.RootTile);
        return volume;
    }

    private static void SerializeNavvolume(BinaryWriter writer, VoxelMap volume)
    {
        using var compressedWriter = new BinaryWriter(new BrotliStream(writer.BaseStream, CompressionLevel.Optimal, true));
        SerializeBounds(writer, volume.RootTile.BoundsMin, volume.RootTile.BoundsMax);
        SerializeTile(writer, volume.RootTile);
    }

    private static unsafe void DeserializeTile(BinaryReader reader, VoxelMap.Tile tile)
    {
        var contentLength = reader.ReadInt32();
        if (tile.Contents.Length != contentLength)
            throw new Exception($"Unexpected tile content length");
        fixed (ushort* p = &tile.Contents[0])
            reader.Read(new Span<byte>(p, tile.Contents.Length * 2));

        var numSubtiles = reader.ReadInt32();
        for (int i = 0; i < numSubtiles; ++i)
        {
            var subBounds = DeserializeBounds(reader);
            var subTile = new VoxelMap.Tile(tile.Owner, subBounds.min, subBounds.max, tile.Level + 1);
            DeserializeTile(reader, subTile);
            tile.Subdivision.Add(subTile);
        }
    }

    private static unsafe void SerializeTile(BinaryWriter writer, VoxelMap.Tile tile)
    {
        writer.Write(tile.Contents.Length);
        fixed (ushort* p = &tile.Contents[0])
            writer.Write(new ReadOnlySpan<byte>(p, tile.Contents.Length * 2));

        writer.Write(tile.Subdivision.Count);
        foreach (var sub in tile.Subdivision)
        {
            SerializeBounds(writer, sub.BoundsMin, sub.BoundsMax);
            SerializeTile(writer, sub);
        }
    }

    private static (Vector3 min, Vector3 max) DeserializeBounds(BinaryReader reader)
    {
        var minX = reader.ReadSingle();
        var minY = reader.ReadSingle();
        var minZ = reader.ReadSingle();
        var maxX = reader.ReadSingle();
        var maxY = reader.ReadSingle();
        var maxZ = reader.ReadSingle();
        return (new(minX, minY, minZ), new(maxX, maxY, maxZ));
    }

    private static void SerializeBounds(BinaryWriter writer, Vector3 min, Vector3 max)
    {
        writer.Write(min.X);
        writer.Write(min.Y);
        writer.Write(min.Z);
        writer.Write(max.X);
        writer.Write(max.Y);
        writer.Write(max.Z);
    }
}
