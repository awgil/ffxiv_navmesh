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

    public readonly Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = [];

    private readonly Dictionary<uint, string> MeshPaths = [];
    private readonly Dictionary<string, SceneExtractor.Mesh> Meshes;

    private readonly Dictionary<ulong, AABB> _allObjects = [];

    public record struct Instance(ulong Key, string Path, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask, InstanceType Type);

    private readonly Hook<BgPartsLayoutInstance.Delegates.SetActive> _bgPartsSetActiveHook;
    private readonly Hook<TriggerBoxLayoutInstance.Delegates.SetActive> _triggerBoxSetActiveHook;
    private readonly Hook<LayoutManager.Delegates.Initialize> _layoutManagerInitHook;

    public int NumTilesInRow;
    public int NumTiles => NumTilesInRow * NumTilesInRow;
    public int TileLength => 2048 / NumTilesInRow;

    public double DebounceMS = 500.0;

    public class Tile(int x, int Z)
    {
        public int X = x;
        public int Z = Z;
        public Dictionary<ulong, (SceneExtractor.Mesh Mesh, SceneExtractor.MeshInstance Instance, InstanceType Type)> Objects = [];
    }

    public Tile[,] Tiles { get; private set; }

    public Tile CloneTile(int x, int z) => new(x, z) { Objects = new(Tiles[x, z].Objects) };

    public event Action<int, int> TileChanged = delegate { };

    public unsafe SceneTracker(int numTilesInRow)
    {
        Meshes = MeshesGlobal.ToDictionary();

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsLayoutInstance.Delegates.SetActive>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxLayoutInstance.Delegates.SetActive>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _layoutManagerInitHook = Service.Hook.HookFromSignature<LayoutManager.Delegates.Initialize>("41 54 48 83 EC 50 48 89 5C 24 ?? ", LayoutManagerInitDetour);

        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();
        _layoutManagerInitHook.Enable();

        NumTilesInRow = numTilesInRow;
        Tiles = new Tile[numTilesInRow, numTilesInRow];
    }

    public unsafe void Init()
    {
        var world = LayoutWorld.Instance();
        if (world->ActiveLayout != null)
            InitLayout(world->ActiveLayout);
        if (world->GlobalLayout != null)
            InitLayout(world->GlobalLayout);
    }

    public void Dispose()
    {
        _layoutManagerInitHook.Dispose();
        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    public unsafe void Clear()
    {
        Tiles = new Tile[NumTilesInRow, NumTilesInRow];
        _allObjects.Clear();
    }

    private unsafe string GetCollisionMeshPathByCrc(uint crc, LayoutManager* layout)
    {
        if (MeshPaths.TryGetValue(crc, out var path))
            return path;

        return MeshPaths[crc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, crc));
    }

    private SceneExtractor.Mesh GetMeshByPath(string path, SceneExtractor.MeshType meshType)
    {
        if (Meshes.TryGetValue(path, out var mesh))
            return mesh;

        return LoadFileMesh(path, meshType);
    }

    private unsafe void LayoutManagerInitDetour(LayoutManager* mgr)
    {
        _layoutManagerInitHook.Original(mgr);
        InitLayout(mgr);
    }

    private unsafe void InitLayout(LayoutManager* layout)
    {
        var terrid = layout->TerritoryTypeId;

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
                        var path = $"{terr}/tr{entry.MeshId:d4}.pcb";
                        var mesh = GetMeshByPath(path, SceneExtractor.MeshType.Terrain);
                        AddObject(path, new SceneExtractor.MeshInstance(0xFFFF000000000000u | ((ulong)terrid << 32) | ((ulong)(long)entry.MeshId), Matrix4x3.Identity, entry.Bounds, (ulong)0, 0), default);
                    }
                }
            }
        }

        foreach (var (k, v) in layout->CrcToAnalyticShapeData)
            AnalyticShapes[k.Key] = (v.Transform, v.BoundsMin, v.BoundsMax);

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

    private unsafe void BgPartsSetActiveDetour(BgPartsLayoutInstance* thisPtr, bool active)
    {
        _bgPartsSetActiveHook.Original(thisPtr, active);
        SetActive(thisPtr, active);
    }

    private unsafe void TriggerBoxSetActiveDetour(TriggerBoxLayoutInstance* thisPtr, bool active)
    {
        _triggerBoxSetActiveHook.Original(thisPtr, active);
        SetActive(thisPtr, active);
    }

    private unsafe void SetActive(BgPartsLayoutInstance* thisPtr, bool active)
    {
        var key = (ulong)thisPtr->Id.InstanceKey << 32 | thisPtr->SubId;
        if (active)
        {
            if (_allObjects.ContainsKey(key))
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
                        AddObject(_keyAnalyticBox, new(key, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask), InstanceType.BgPart);
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Sphere:
                        AddObject(_keyAnalyticSphere, new(key, resultingTransform, SceneExtractor.CalculateSphereBounds(key, ref resultingTransform), mat, matMask), InstanceType.BgPart);
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Cylinder:
                        AddObject(_keyMeshCylinder, new(key, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask), InstanceType.BgPart);
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Plane:
                        AddObject(_keyAnalyticPlaneSingle, new(key, resultingTransform, SceneExtractor.CalculatePlaneBounds(ref resultingTransform), mat, matMask), InstanceType.BgPart);
                        break;
                }
            }
            else if (thisPtr->CollisionMeshPathCrc != 0)
            {
                var path = GetCollisionMeshPathByCrc(thisPtr->CollisionMeshPathCrc, thisPtr->Layout);
                var mesh = GetMeshByPath(path, SceneExtractor.MeshType.FileMesh);

                var transform = *thisPtr->GetTransformImpl();
                var t2 = new Matrix4x3(transform.Compose());
                var bounds = SceneExtractor.CalculateMeshBounds(mesh, ref t2);

                AddObject(path, new(key, t2, bounds, mat, matMask), InstanceType.BgPart);
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
        var key = (ulong)thisPtr->Id.InstanceKey << 32 | thisPtr->SubId;

        if (active)
        {
            if ((cast->MaterialIdLow & 0x410) == 0x400)
                return;

            ulong mat = ((ulong)cast->MaterialIdHigh << 32) | cast->MaterialIdLow;
            ulong matMask = ((ulong)cast->MaterialMaskHigh << 32) | cast->MaterialMaskLow;

            if (_allObjects.ContainsKey(key))
                return;

            var transform = new Matrix4x3(cast->Transform.Compose());
            switch (cast->TriggerBoxLayoutInstance.Type)
            {
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box:
                    AddObject(_keyAnalyticBox, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere:
                    AddObject(_keyAnalyticSphere, new(key, transform, SceneExtractor.CalculateSphereBounds(key, ref transform), mat, matMask), InstanceType.CollisionBox);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder:
                    AddObject(_keyAnalyticCylinder, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane:
                    AddObject(_keyAnalyticPlaneSingle, new(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask), InstanceType.CollisionBox);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh:
                    if (cast->PcbPathCrc != 0)
                    {
                        var path = GetCollisionMeshPathByCrc(cast->PcbPathCrc, thisPtr->Layout);
                        var mesh = GetMeshByPath(path, SceneExtractor.MeshType.FileMesh);
                        AddObject(path, new(key, transform, SceneExtractor.CalculateMeshBounds(mesh, ref transform), mat, matMask), InstanceType.CollisionBox);
                    }
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided:
                    AddObject(_keyAnalyticPlaneDouble, new(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask), InstanceType.CollisionBox);
                    break;
            }
        }
        else
        {
            RemoveObject(key);
        }
    }

    private void AddObject(string path, SceneExtractor.MeshInstance part, InstanceType type)
    {
        _allObjects[part.Id] = part.WorldBounds;
        AddMeshInstance(Meshes[path], part, type);
    }

    private void RemoveObject(ulong key)
    {
        if (_allObjects.TryGetValue(key, out var part))
        {
            _allObjects.Remove(key);
            RemoveMeshInstance(key, part);
        }
    }

    private void AddMeshInstance(SceneExtractor.Mesh mesh, SceneExtractor.MeshInstance instance, InstanceType type)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(instance.WorldBounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                Tiles[i, j] ??= new(i, j);
                Tiles[i, j].Objects[instance.Id] = (mesh, instance, type);
                TileChanged.Invoke(i, j);
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
                TileChanged.Invoke(i, j);
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

