using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Security.Cryptography;

namespace Navmesh;

public sealed class SceneTracker : IDisposable
{
    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyAnalyticPlaneSingle = "<plane one-sided>";
    private const string _keyAnalyticPlaneDouble = "<plane two-sided>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    public static readonly Dictionary<string, SceneExtractor.Mesh> MeshesGlobal = new()
    {
        [_keyAnalyticBox] = new() { Parts = SceneExtractor.MeshBox, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticSphere] = new() { Parts = SceneExtractor.MeshSphere, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticCylinder] = new() { Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticPlaneSingle] = new() { Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyAnalyticPlaneDouble] = new() { Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyMeshCylinder] = new() { Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.CylinderMesh }
    };

    public readonly List<string> Terrains = [];
    public readonly Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = [];

    private readonly Dictionary<uint, string> MeshPaths = [];
    private readonly Dictionary<string, SceneExtractor.Mesh> Meshes;

    public readonly Dictionary<ulong, Instance> LayoutObjects = [];

    public record struct Instance(ulong Key, string Path, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask);

    private unsafe delegate void BgPartsSetActiveDelegate(BgPartsLayoutInstance* thisPtr, byte active);
    private readonly Hook<BgPartsSetActiveDelegate> _bgPartsSetActiveHook;
    private unsafe delegate void TriggerBoxSetActiveDelegate(TriggerBoxLayoutInstance* thisPtr, byte active);
    private readonly Hook<TriggerBoxSetActiveDelegate> _triggerBoxSetActiveHook;

    public int NumTilesInRow
    {
        get;
        set
        {
            var rebuildColliders = field != value;
            field = value;
            if (rebuildColliders)
                RebuildCache();
        }
    }
    public int NumTiles => NumTilesInRow * NumTilesInRow;
    public int TileLength => 2048 / NumTilesInRow;

    public const double DebounceMS = 500.0;

    public class Tile(int x, int Z)
    {
        public int X = x;
        public int Z = Z;
        public Dictionary<ulong, (SceneExtractor.Mesh, SceneExtractor.MeshInstance)> Objects = [];
        public double Timer = 0;
    }

    // initialized by NumTilesInRow setter
    public Tile[,] Tiles { get; set; } = null!;

    public event Action<List<Tile>> TileChanged = delegate { };

    public record struct ChangedTile(int X, int Z, SortedSet<ulong> SortedIds);

    public unsafe SceneTracker(int numTilesInRow)
    {
        Meshes = MeshesGlobal.ToDictionary();

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsSetActiveDelegate>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxSetActiveDelegate>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();

        NumTilesInRow = numTilesInRow;
    }

    public void Dispose()
    {
        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    public void Tick(Dalamud.Plugin.Services.IFramework framework)
    {
        var tick = framework.UpdateDelta.TotalMilliseconds;

        List<Tile> tilesChanged = [];
        for (var i = 0; i < Tiles.GetLength(0); i++)
        {
            for (var j = 0; j < Tiles.GetLength(1); j++)
            {
                var cnt = Tiles[i, j];
                if (cnt == null)
                    continue;
                var pos = cnt.Timer > 0;
                cnt.Timer -= tick;

                if (pos && cnt.Timer <= 0)
                    tilesChanged.Add(cnt);
            }
        }

        if (tilesChanged.Count > 0)
            TileChanged.Invoke(tilesChanged);
    }

    private void RebuildCache()
    {
        Tiles = new Tile[NumTilesInRow, NumTilesInRow];
    }

    public unsafe void Reload(bool firstTimeInit)
    {
        RebuildCache();
        Terrains.Clear();
        LayoutObjects.Clear();

        FillFromLayout(LayoutWorld.Instance()->GlobalLayout, firstTimeInit);
        FillFromLayout(LayoutWorld.Instance()->ActiveLayout, firstTimeInit);
    }

    private unsafe void FillFromLayout(LayoutManager* layout, bool pluginInit)
    {
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return;

        foreach (var (k, v) in layout->Terrains)
        {
            var terr = $"{v.Value->PathString}/collision";
            if (Service.DataManager.GetFile(terr + "/list.pcb") is { } list)
            {
                fixed (byte* data = &list.Data[0])
                {
                    var header = (ColliderStreamed.FileHeader*)data;
                    foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                    {
                        var mesh = LoadFileMesh($"{terr}/tr{entry.MeshId:d4}.pcb", SceneExtractor.MeshType.Terrain);
                        SceneExtractor.AddInstance(mesh, 0, ref Matrix4x3.Identity, ref entry.Bounds, 0, 0);
                    }
                }
            }
        }

        foreach (var (k, v) in layout->CrcToAnalyticShapeData)
            AnalyticShapes[k.Key] = (v.Transform, v.BoundsMin, v.BoundsMax);

        if (!pluginInit)
            return;

        // trigger add events for existing colliders
        var bgParts = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.BgPart);
        if (bgParts != null)
            foreach (var (k, v) in *bgParts)
                if ((v.Value->Flags3 & 0x10) != 0)
                    SetActive((BgPartsLayoutInstance*)v.Value, true);

        var colliders = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.CollisionBox);
        if (colliders != null)
            foreach (var (k, v) in *colliders)
                if ((v.Value->Flags3 & 0x10) != 0)
                    SetActive((TriggerBoxLayoutInstance*)v.Value, true);
    }

    private unsafe SceneExtractor.Mesh LoadFileMesh(string path, SceneExtractor.MeshType type)
    {
        var mesh = new SceneExtractor.Mesh();
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
        mesh.MeshType = type;
        return Meshes[path] = mesh;
    }

    private unsafe void BgPartsSetActiveDetour(BgPartsLayoutInstance* thisPtr, byte active)
    {
        _bgPartsSetActiveHook.Original(thisPtr, active);
        SetActive(thisPtr, active == 1);
    }

    private unsafe void TriggerBoxSetActiveDetour(TriggerBoxLayoutInstance* thisPtr, byte active)
    {
        _triggerBoxSetActiveHook.Original(thisPtr, active);
        SetActive(thisPtr, active == 1);
    }

    private unsafe void SetActive(BgPartsLayoutInstance* thisPtr, bool active)
    {
        var key = (ulong)thisPtr->Id.InstanceKey << 32 | thisPtr->SubId;
        if (active)
        {
            if (LayoutObjects.ContainsKey(key))
                return;

            ulong mat = ((ulong)thisPtr->CollisionMaterialIdHigh << 32) | thisPtr->CollisionMaterialIdLow;
            ulong matMask = ((ulong)thisPtr->CollisionMaterialMaskHigh << 32) | thisPtr->CollisionMaterialMaskLow;

            if (thisPtr->AnalyticShapeDataCrc != 0)
            {
                if (!AnalyticShapes.TryGetValue(thisPtr->AnalyticShapeDataCrc, out var shape))
                    return;

                var transform = *thisPtr->GetTransformImpl();
                var mtxBounds = Matrix4x4.CreateScale((shape.bbMax - shape.bbMin) * 0.5f);
                mtxBounds.Translation = (shape.bbMin + shape.bbMax) * 0.5f;
                var fullTransform = mtxBounds * shape.transform.Compose() * transform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);

                switch ((FileLayerGroupAnalyticCollider.Type)shape.transform.Type)
                {
                    case FileLayerGroupAnalyticCollider.Type.Box:
                        AddObject(key, new Instance(key, _keyAnalyticBox, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Sphere:
                        AddObject(key, new Instance(key, _keyAnalyticSphere, resultingTransform, SceneExtractor.CalculateSphereBounds(key, ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Cylinder:
                        AddObject(key, new Instance(key, _keyMeshCylinder, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Plane:
                        AddObject(key, new Instance(key, _keyAnalyticPlaneSingle, resultingTransform, SceneExtractor.CalculatePlaneBounds(ref resultingTransform), mat, matMask));
                        break;
                }
            }
            else if (thisPtr->CollisionMeshPathCrc != 0)
            {
                if (!MeshPaths.TryGetValue(thisPtr->CollisionMeshPathCrc, out var path))
                {
                    path = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref thisPtr->Layout->CrcToPath, thisPtr->CollisionMeshPathCrc));
                    MeshPaths[thisPtr->CollisionMeshPathCrc] = path;
                }

                if (!Meshes.TryGetValue(path, out var mesh))
                    mesh = LoadFileMesh(path, SceneExtractor.MeshType.FileMesh);

                var transform = *thisPtr->GetTransformImpl();
                var t2 = new Matrix4x3(transform.Compose());
                var bounds = SceneExtractor.CalculateMeshBounds(mesh, ref t2);

                AddObject(key, new Instance(key, path, t2, bounds, mat, matMask));
            }
        }
        else
        {
            RemoveObject(key);
        }
    }

    private unsafe void SetActive(TriggerBoxLayoutInstance* thisPtr, bool active)
    {
        if (thisPtr->Id.Type != InstanceType.CollisionBox)
            return;

        var cast = (CollisionBoxLayoutInstance*)thisPtr;
        ulong mat = ((ulong)cast->MaterialIdHigh << 32) | cast->MaterialIdLow;
        ulong matMask = ((ulong)cast->MaterialMaskHigh << 32) | cast->MaterialMaskLow;

        var key = (ulong)thisPtr->Id.InstanceKey << 32 | thisPtr->SubId;
        if (active)
        {
            if (LayoutObjects.ContainsKey(key))
                return;

            string? path = null;
            SceneExtractor.Mesh? mesh = null;

            if (cast->PcbPathCrc != 0 && !MeshPaths.ContainsKey(cast->PcbPathCrc))
            {
                path = MeshPaths[cast->PcbPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref thisPtr->Layout->CrcToPath, cast->PcbPathCrc));
                if (!Meshes.TryGetValue(path, out mesh))
                    mesh = LoadFileMesh(path, SceneExtractor.MeshType.FileMesh);
            }

            var transform = new Matrix4x3(cast->Transform.Compose());
            switch (cast->TriggerBoxLayoutInstance.Type)
            {
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box:
                    AddObject(key, new(key, _keyAnalyticBox, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere:
                    AddObject(key, new(key, _keyAnalyticSphere, transform, SceneExtractor.CalculateSphereBounds(key, ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder:
                    AddObject(key, new(key, _keyAnalyticCylinder, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane:
                    AddObject(key, new(key, _keyAnalyticPlaneSingle, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh:
                    if (mesh == null)
                    {
                        Service.Log.Warning($"Mesh-type collider found with path CRC {cast->PcbPathCrc}, but no matching mesh exists! This is a bug");
                        return;
                    }
                    AddObject(key, new(key, path!, transform, SceneExtractor.CalculateMeshBounds(mesh!, ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided:
                    AddObject(key, new(key, _keyAnalyticPlaneDouble, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask));
                    break;
            }
        }
        else
        {
            RemoveObject(key);
        }
    }

    private void AddObject(ulong key, Instance part)
    {
        LayoutObjects[key] = part;
        var t = part.Transform;
        var b = part.Bounds;
        var mesh = Meshes[part.Path];
        AddMeshInstance(mesh, SceneExtractor.AddInstance(mesh, part.Key, ref t, ref b, part.MaterialId, part.MaterialMask));
    }

    private void RemoveObject(ulong key)
    {
        if (LayoutObjects.TryGetValue(key, out var part))
        {
            LayoutObjects.Remove(key);
            RemoveMeshInstance(key, part.Bounds);
        }
    }

    private void AddMeshInstance(SceneExtractor.Mesh mesh, SceneExtractor.MeshInstance instance)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(instance.WorldBounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                Tiles[i, j] ??= new(i, j);
                Tiles[i, j].Objects.Add(instance.Id, (mesh, instance));
                Tiles[i, j].Timer = DebounceMS;
            }
        }
    }

    private void RemoveMeshInstance(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                Tiles[i, j].Objects.Remove(key);
                Tiles[i, j].Timer = DebounceMS;
            }
        }
    }

    private ((int, int) Min, (int, int) Max) GetTiles(AABB box)
    {
        var min = box.Min - new Vector3(-1024);
        var max = box.Max - new Vector3(-1024);

        return (((int)min.X / TileLength, (int)min.Z / TileLength), ((int)max.X / TileLength, (int)max.Z / TileLength));
    }

    public static string GetHashFromKeys(SortedSet<ulong> keys)
    {
        var span = keys.ToArray();
        var bytes = new byte[span.Length * sizeof(ulong)];
        Buffer.BlockCopy(span, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(MD5.HashData(bytes));
    }
}

