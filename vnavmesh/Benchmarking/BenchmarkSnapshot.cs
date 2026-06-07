using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Navmesh.Benchmarking;

public readonly record struct BenchmarkPoint(float X, float Y, float Z)
{
    public static BenchmarkPoint FromVector3(Vector3 v) => new(v.X, v.Y, v.Z);
    public Vector3 ToVector3() => new(X, Y, Z);
}

public sealed record BenchmarkPathPair(BenchmarkPoint From, BenchmarkPoint To, bool Fly);

public sealed record BenchmarkSnapshotMetadata
{
    public int FormatVersion { get; init; } = 1;
    public string CapturedAtUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public string SourceKey { get; init; } = "";
    public uint TerritoryId { get; init; }
    public int CustomizationVersion { get; init; }
    public int NavmeshFormatVersion { get; init; } = (int)global::Navmesh.Navmesh.Version;
    public int PairSeed { get; init; }
    public int RequestedPairsPerMode { get; init; }
    public List<BenchmarkPathPair> Pairs { get; init; } = [];
}

public static class BenchmarkSnapshotFiles
{
    public const string MetadataFileName = "metadata.json";
    public const string NavmeshFileName = "navmesh.navmesh";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void Save(string directory, BenchmarkSnapshotMetadata metadata, global::Navmesh.Navmesh navmesh)
    {
        Directory.CreateDirectory(directory);

        var metadataPath = Path.Combine(directory, MetadataFileName);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));

        var navmeshPath = Path.Combine(directory, NavmeshFileName);
        using var stream = File.Open(navmeshPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        navmesh.Serialize(writer);
    }

    public static BenchmarkSnapshotMetadata LoadMetadata(string directory)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        var metadata = JsonSerializer.Deserialize<BenchmarkSnapshotMetadata>(File.ReadAllText(metadataPath), JsonOptions);
        return metadata ?? throw new InvalidDataException($"Failed to read benchmark metadata from '{metadataPath}'");
    }

    public static global::Navmesh.Navmesh LoadNavmesh(string directory, BenchmarkSnapshotMetadata metadata)
    {
        var navmeshPath = Path.Combine(directory, NavmeshFileName);
        using var stream = File.OpenRead(navmeshPath);
        using var reader = new BinaryReader(stream);
        return global::Navmesh.Navmesh.Deserialize(reader, metadata.CustomizationVersion);
    }
}
