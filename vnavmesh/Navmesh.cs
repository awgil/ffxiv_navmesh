using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Navmesh.NavVolume;
using System;
using System.IO;

namespace Navmesh;

// full set of data needed for navigation in the zone
public record class Navmesh(DtNavMesh Mesh, VoxelMap Volume)
{
    public static readonly uint Magic = 0x444D564E; // 'NVMD'
    public static readonly uint Version = 3;

    // throws an exception on failure
    public static Navmesh Deserialize(BinaryReader reader)
    {
        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();
        if (magic != Magic || version != Version)
            throw new Exception("Incorrect header");

        var meshLen = reader.ReadInt32();
        var mesh = new DtMeshSetReader().Read(new RcByteBuffer(reader.ReadBytes(meshLen)));

        var minX = reader.ReadSingle();
        var minY = reader.ReadSingle();
        var minZ = reader.ReadSingle();
        var maxX = reader.ReadSingle();
        var maxY = reader.ReadSingle();
        var maxZ = reader.ReadSingle();
        var nx = reader.ReadInt32();
        var ny = reader.ReadInt32();
        var nz = reader.ReadInt32();
        var voxels = reader.ReadBytes((nx * ny * nz + 7) >> 3);
        var volume = new VoxelMap(new(minX, minY, minZ), new(maxX, maxY, maxZ), nx, ny, nz, voxels); // TODO: there's an extra allocation here we could avoid
        return new(mesh, volume);
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(Version);

        var meshSizePos = (int)writer.Seek(0, SeekOrigin.Current);
        writer.Write(0);
        new DtMeshSetWriter().Write(writer, Mesh, RcByteOrder.LITTLE_ENDIAN, false);
        var postMeshSizePos = (int)writer.Seek(0, SeekOrigin.Current);
        writer.Seek(meshSizePos, SeekOrigin.Begin);
        writer.Write(postMeshSizePos - meshSizePos - 4);
        writer.Seek(postMeshSizePos, SeekOrigin.Begin);

        writer.Write(Volume.BoundsMin.X);
        writer.Write(Volume.BoundsMin.Y);
        writer.Write(Volume.BoundsMin.Z);
        writer.Write(Volume.BoundsMax.X);
        writer.Write(Volume.BoundsMax.Y);
        writer.Write(Volume.BoundsMax.Z);
        writer.Write(Volume.NumCellsX);
        writer.Write(Volume.NumCellsY);
        writer.Write(Volume.NumCellsZ);
        var voxels = new byte[(Volume.Voxels.Length + 7) >> 3];
        Volume.Voxels.CopyTo(voxels, 0); // TODO: fuck this :(
        writer.Write(voxels);
    }
}
