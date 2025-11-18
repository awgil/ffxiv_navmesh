using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

public sealed partial class ColliderSet : IDisposable
{
    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyAnalyticPlaneSingle = "<plane one-sided>";
    private const string _keyAnalyticPlaneDouble = "<plane two-sided>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    public static readonly Dictionary<string, SceneExtractor.Mesh> MeshesGlobal = new()
    {
        [_keyAnalyticBox] = new() { Path = _keyAnalyticBox, Parts = SceneExtractor.MeshBox, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticSphere] = new() { Path = _keyAnalyticSphere, Parts = SceneExtractor.MeshSphere, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticCylinder] = new() { Path = _keyAnalyticCylinder, Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticPlaneSingle] = new() { Path = _keyAnalyticPlaneSingle, Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyAnalyticPlaneDouble] = new() { Path = _keyAnalyticPlaneDouble, Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyMeshCylinder] = new() { Path = _keyMeshCylinder, Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.CylinderMesh }
    };

    private readonly ConcurrentDictionary<ulong, AABB> _allObjects = [];

    private readonly Hook<BgPartsLayoutInstance.Delegates.SetActive> _bgPartsSetActiveHook;
    private readonly Hook<TriggerBoxLayoutInstance.Delegates.SetActive> _triggerBoxSetActiveHook;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<ColliderSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;

    private bool _anyChanged;

    public unsafe ColliderSet()
    {
        Meshes = MeshesGlobal.ToDictionary();

        Service.ClientState.ZoneInit += OnZoneInit;

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsLayoutInstance.Delegates.SetActive>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxLayoutInstance.Delegates.SetActive>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
    }

    public unsafe void Initialize(bool emitChangeEvents = true)
    {
        InitZone(Service.ClientState.TerritoryType);

        var world = LayoutWorld.Instance();
        ActivateExistingLayout(world->GlobalLayout);
        ActivateExistingLayout(world->ActiveLayout);

        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();

        ZoneChanged.Invoke(this);
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= OnZoneInit;
        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    private unsafe void OnZoneInit(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        InitZone(obj.TerritoryType.RowId);
        ZoneChanged.Invoke(this);
    }

    private unsafe void InitZone(uint zoneId)
    {
        LastLoadedZone = zoneId;
        Customization = NavmeshCustomizationRegistry.ForTerritory(zoneId);
        RowLength = Customization.Settings.NumTiles[0];
        _allObjects.Clear();
        _tiles = new TileInternal?[RowLength, RowLength];

        var tt = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(zoneId);
        if (tt == null)
        {
            Service.Log.Warning($"not building mesh for {zoneId}");
            return;
        }

        LoadTerrain(zoneId, tt.Value);
    }

    private unsafe void ActivateExistingLayout(LayoutManager* layout)
    {
        if (layout == null)
            return;

        // trigger add events for existing colliders
        var bgParts = layout->InstancesByType.FindPtr(InstanceType.BgPart);
        if (bgParts != null)
            foreach (var (k, v) in *bgParts)
                if ((v.Value->Flags3 & 0x10) != 0)
                    SetActive((BgPartsLayoutInstance*)v.Value, true);

        var colliders = layout->InstancesByType.FindPtr(InstanceType.CollisionBox);
        if (colliders != null)
            foreach (var (k, v) in *colliders)
                if ((v.Value->Flags3 & 0x10) != 0)
                    SetActive((TriggerBoxLayoutInstance*)v.Value, true);
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

    private unsafe void SetColliderMaterial(FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Collider* thisPtr, ulong id, ulong mask)
    {
        var objId = (thisPtr->LayoutObjectId << 32) | (thisPtr->LayoutObjectId >> 32);
        if (_allObjects.TryGetValue(thisPtr->LayoutObjectId, out var bounds))
        {
            if (RemoveObject(thisPtr->LayoutObjectId, out var obj))
            {
                obj.Instance.ForceSetPrimFlags = SceneExtractor.ExtractMaterialFlags(thisPtr->ObjectMaterialValue);
                AddObject(obj.Mesh.Path, obj.Instance, obj.Type);
            }
        }
    }

    private unsafe void SetActive(BgPartsLayoutInstance* thisPtr, bool active)
    {
        var key = (ulong)thisPtr->Id.InstanceKey << 32 | thisPtr->SubId;
        if (active)
        {
            if (_allObjects.ContainsKey(key))
            {
                //Service.Log.Verbose($"[Col] BgPart {key:X16} is already in layout, doing nothing");
                return;
            }

            ulong mat = (ulong)thisPtr->CollisionMaterialIdHigh << 32 | thisPtr->CollisionMaterialIdLow;
            ulong matMask = (ulong)thisPtr->CollisionMaterialMaskHigh << 32 | thisPtr->CollisionMaterialMaskLow;

            if (thisPtr->AnalyticShapeDataCrc != 0)
            {
                if (!TryGetAnalyticShapeData(thisPtr->AnalyticShapeDataCrc, thisPtr->Layout, out var shape))
                {
                    Service.Log.Verbose($"[Col] no analytic shape for {thisPtr->AnalyticShapeDataCrc}, skipping");
                    return;
                }

                var transform = *thisPtr->GetTransformImpl();
                var mtxBounds = Matrix4x4.CreateScale((shape.BoundsMax - shape.BoundsMin) * 0.5f);
                mtxBounds.Translation = (shape.BoundsMin + shape.BoundsMax) * 0.5f;
                var fullTransform = mtxBounds * shape.Transform.Compose() * transform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);

                switch ((FileLayerGroupAnalyticCollider.Type)shape.Transform.Type)
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
            ulong mat = (ulong)cast->MaterialIdHigh << 32 | cast->MaterialIdLow;
            ulong matMask = (ulong)cast->MaterialMaskHigh << 32 | cast->MaterialMaskLow;

            if (_allObjects.ContainsKey(key))
                return;

            var zone = LastLoadedZone;
            bool isBoundaryObject = _boundaryLayers.TryGetValue(zone, out var l) && l.Contains(thisPtr->Layer->Id);

            var transform = new Matrix4x3(cast->Transform.Compose());
            switch (cast->TriggerBoxLayoutInstance.Type)
            {
                case ColliderType.Box:
                    AddObject(_keyAnalyticBox, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case ColliderType.Sphere:
                    AddObject(_keyAnalyticSphere, new(key, transform, SceneExtractor.CalculateSphereBounds(key, ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case ColliderType.Cylinder:
                    AddObject(_keyAnalyticCylinder, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case ColliderType.Plane:
                    AddObject(_keyAnalyticPlaneSingle, new(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case ColliderType.Mesh:
                    if (cast->PcbPathCrc != 0)
                    {
                        var path = GetCollisionMeshPathByCrc(cast->PcbPathCrc, thisPtr->Layout);
                        var mesh = GetMeshByPath(path, SceneExtractor.MeshType.FileMesh);
                        AddObject(path, new(key, transform, SceneExtractor.CalculateMeshBounds(mesh, ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    }
                    break;
                case ColliderType.PlaneTwoSided:
                    AddObject(_keyAnalyticPlaneDouble, new(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
            }
        }
        else
        {
            RemoveObject(key);
        }
    }

    private void AddObject(string path, SceneExtractor.MeshInstance part, InstanceType type, bool isBoundaryObject = false)
    {
        if (_allObjects.TryAdd(part.Id, part.WorldBounds))
        {
            if (isBoundaryObject)
            {
                part.ForceSetPrimFlags &= ~SceneExtractor.PrimitiveFlags.Transparent;
                part.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.Unlandable;
            }
            // exclude from cache to save cpu cycles
            else if (part.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.Transparent))
                return;

            AddMeshInstance(Meshes[path], part, type);
        }
    }

    private bool RemoveObject(ulong key, out TileObject obj)
    {
        if (_allObjects.Remove(key, out var part))
            return RemoveMeshInstance(key, part, out obj);
        obj = default;
        return false;
    }

    private void RemoveObject(ulong key) => RemoveObject(key, out _);

    private void AddMeshInstance(SceneExtractor.Mesh mesh, SceneExtractor.MeshInstance instance, InstanceType type)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(instance.WorldBounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= RowLength) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= RowLength) continue;
                var tile = _tiles[i, j] ??= new(i, j);
                if (tile.Objects.TryAdd(instance.Id, new(mesh, instance, type)))
                {
                    Service.Log.Verbose($"[Col] adding object {instance.Id:X16} to tile {i}x{j}");
                    tile.Changed = _anyChanged = true;
                }
            }
        }
    }

    private bool RemoveMeshInstance(ulong key, AABB bounds, out TileObject obj)
    {
        obj = default;
        var removed = false;
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= RowLength) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= RowLength) continue;
                var tile = _tiles[i, j] ??= new(i, j);
                if (tile.Objects.Remove(key, out obj))
                {
                    Service.Log.Verbose($"[Col] removing object {key:X16} from tile {i}x{j}");
                    tile.Changed = _anyChanged = removed = true;
                }
            }
        }
        return removed;
    }

    private ((int, int) Min, (int, int) Max) GetTiles(AABB box)
    {
        var min = box.Min - new Vector3(-1024);
        var max = box.Max - new Vector3(-1024);

        return (((int)min.X / TileUnits, (int)min.Z / TileUnits), ((int)max.X / TileUnits, (int)max.Z / TileUnits));
    }
}

