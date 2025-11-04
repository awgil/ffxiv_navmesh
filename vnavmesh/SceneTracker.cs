using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<ulong, AABB> _allObjects = [];

    public record struct Instance(ulong Key, string Path, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask, InstanceType Type);

    private readonly Hook<BgPartsLayoutInstance.Delegates.SetActive> _bgPartsSetActiveHook;
    private readonly Hook<TriggerBoxLayoutInstance.Delegates.SetActive> _triggerBoxSetActiveHook;
    private readonly Hook<LayoutManager.Delegates.Initialize> _layoutManagerInitHook;

    public Tile?[,] Tiles { get; private set; } = new Tile?[0, 0];
    public int RowLength
    {
        get;
        set
        {
            field = value;
            _allObjects.Clear();
            Tiles = new Tile?[value, value];
        }
    } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint Territory { get; private set; }
    public List<uint> FestivalLayers = [];

    public class Tile(int x, int Z)
    {
        public int X = x;
        public int Z = Z;
        public ConcurrentDictionary<ulong, (SceneExtractor.Mesh Mesh, SceneExtractor.MeshInstance Instance, InstanceType Type)> Objects = [];
        public bool Changed;
    }

    private bool _anyChanged;

    public unsafe SceneTracker()
    {
        Meshes = MeshesGlobal.ToDictionary();

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsLayoutInstance.Delegates.SetActive>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxLayoutInstance.Delegates.SetActive>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _layoutManagerInitHook = Service.Hook.HookFromSignature<LayoutManager.Delegates.Initialize>("41 54 48 83 EC 50 48 89 5C 24 ?? ", LayoutManagerInitDetour);
    }

    public unsafe void Initialize()
    {
        if (RowLength == 0)
            throw new InvalidOperationException("Initialize() called before setting a valid tile count");

        var world = LayoutWorld.Instance();
        if (world->GlobalLayout != null)
            InitLayout(world->GlobalLayout);
        if (world->ActiveLayout != null)
            InitLayout(world->ActiveLayout);

        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();
        _layoutManagerInitHook.Enable();
    }

    public void Dispose()
    {
        _layoutManagerInitHook.Dispose();
        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    public IEnumerable<(int X, int Z)> GetTileChanges()
    {
        if (!_anyChanged)
            yield break;

        for (var i = 0; i < Tiles.GetLength(0); i++)
            for (var j = 0; j < Tiles.GetLength(1); j++)
            {
                var t = Tiles[i, j];
                if (t?.Changed == true)
                {
                    t.Changed = false;
                    yield return (t.X, t.Z);
                }
            }
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
        Territory = layout->TerritoryTypeId;

        FestivalLayers.Clear();
        foreach (var (k, v) in layout->Layers)
            if (v.Value->FestivalId != 0)
                FestivalLayers.Add(((uint)v.Value->FestivalSubId << 16) | v.Value->FestivalId);

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
                        AddObject(path, new SceneExtractor.MeshInstance(0xFFFF000000000000u | ((ulong)Territory << 32) | ((ulong)(long)entry.MeshId), Matrix4x3.Identity, entry.Bounds, (ulong)0, 0), default);
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
        if (_allObjects.TryAdd(part.Id, part.WorldBounds))
            AddMeshInstance(Meshes[path], part, type);
    }

    private void RemoveObject(ulong key)
    {
        if (_allObjects.Remove(key, out var part))
            RemoveMeshInstance(key, part);
    }

    private void AddMeshInstance(SceneExtractor.Mesh mesh, SceneExtractor.MeshInstance instance, InstanceType type)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(instance.WorldBounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= RowLength) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= RowLength) continue;
                var tile = Tiles[i, j] ??= new(i, j);
                if (tile.Objects.TryAdd(instance.Id, (mesh, instance, type)))
                    tile.Changed = _anyChanged = true;
            }
        }
    }

    private void RemoveMeshInstance(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= RowLength) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= RowLength) continue;
                var tile = Tiles[i, j] ??= new(i, j);
                if (tile.Objects.Remove(key, out _))
                    tile.Changed = _anyChanged = true;
            }
        }
    }

    private ((int, int) Min, (int, int) Max) GetTiles(AABB box)
    {
        var min = box.Min - new Vector3(-1024);
        var max = box.Max - new Vector3(-1024);

        return (((int)min.X / TileUnits, (int)min.Z / TileUnits), ((int)max.X / TileUnits, (int)max.Z / TileUnits));
    }

    public static string GetHashFromKeys(SortedSet<ulong> keys)
    {
        var span = keys.ToArray();
        var bytes = new byte[span.Length * sizeof(ulong)];
        Buffer.BlockCopy(span, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(MD5.HashData(bytes));
    }
}

