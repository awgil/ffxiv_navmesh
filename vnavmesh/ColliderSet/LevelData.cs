using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Lumina.Data.Files;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

public partial class ColliderSet
{
    private readonly Dictionary<uint, string> MeshPaths = [];
    private readonly Dictionary<string, SceneExtractor.Mesh> Meshes;

    private unsafe string GetCollisionMeshPathByCrc(uint crc, LayoutManager* layout)
    {
        if (MeshPaths.TryGetValue(crc, out var path))
            return path;

        return MeshPaths[crc] = LayoutUtils.ReadString(layout->CrcToPath.FindPtr(crc));
    }

    private readonly Dictionary<uint, AnalyticShape?> AnalyticShapes = [];
    private record struct AnalyticShape(Transform Transform, Vector3 BoundsMin, Vector3 BoundsMax);

    private unsafe bool TryGetAnalyticShapeData(uint crc, LayoutManager* layout, out AnalyticShape shape)
    {
        shape = default;

        if (AnalyticShapes.TryGetValue(crc, out var sh))
        {
            if (sh != null)
            {
                shape = sh.Value;
                return true;
            }
            return false;
        }

        var dkey = new LayoutManager.AnalyticShapeDataKey() { Key = crc };
        if (layout->CrcToAnalyticShapeData.TryGetValuePointer(dkey, out var data))
        {
            shape = new AnalyticShape(data->Transform, data->BoundsMin, data->BoundsMax);
            AnalyticShapes[crc] = shape;
            return true;
        }
        else
        {
            Service.Log.Warning($"analytic shape {crc:X} is missing from layout shape data and won't be loaded - this is a bug!");
            AnalyticShapes[crc] = null;
            return false;
        }
    }

    private unsafe SceneExtractor.Mesh GetMeshByPath(string path, SceneExtractor.MeshType meshType)
    {
        if (Meshes.TryGetValue(path, out var cached))
            return cached;

        var mesh = new SceneExtractor.Mesh() { Path = path };
        var f = Service.DataManager.GetFile(path);
        if (f != null)
        {
            fixed (byte* rawData = &f.Data[0])
            {
                var data = (MeshPCB.FileHeader*)rawData;
                if (data->Version is 1 or 4)
                    SceneExtractor.FillFromFileNode(mesh.Parts, (MeshPCB.FileNode*)(data + 1));
            }
        }
        mesh.MeshType = meshType;
        return Meshes[path] = mesh;
    }

    /// <summary>
    /// Set of layers that contains the invisible walls enclosing flyable zones. We can't uniquely determine if an arbitrary collider is an invisible wall because they share material flags with many other layout objects, so instead we filter out all colliders that are contained in these layers.
    /// </summary>
    private readonly Dictionary<uint, HashSet<uint>> _boundaryLayers = [];

    private unsafe void LoadTerrain(uint zoneId, Lumina.Excel.Sheets.TerritoryType tt)
    {
        var ttBg = tt.Bg.ToString();
        var bgPrefix = "bg/" + ttBg[..ttBg.IndexOf("level/")];
        var terr = bgPrefix + "collision";
        if (Service.DataManager.GetFile(terr + "/list.pcb") is { } list)
        {
            fixed (byte* data = &list.Data[0])
            {
                var header = (ColliderStreamed.FileHeader*)data;
                foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                {
                    var path = $"{terr}/tr{entry.MeshId:d4}.pcb";
                    var mesh = GetMeshByPath(path, SceneExtractor.MeshType.Terrain);
                    AddObject(path, new SceneExtractor.MeshInstance(0xFFFF000000000000u | (ulong)zoneId << 32 | (ulong)(long)entry.MeshId, Matrix4x3.Identity, entry.Bounds, (ulong)0, 0), default);
                }
            }
        }

        _boundaryLayers.TryAdd(zoneId, []);
        if (Service.DataManager.GetFile<LgbFile>(bgPrefix + "level/bg.lgb") is { } bg)
            foreach (var layer in bg.Layers)
                if (layer.Name.Equals("bg_endofworld_01", StringComparison.InvariantCultureIgnoreCase))
                    _boundaryLayers[zoneId].Add(layer.LayerId & 0xFFFF);
    }
}
