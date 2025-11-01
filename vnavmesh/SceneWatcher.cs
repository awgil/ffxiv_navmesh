using Dalamud.Game.ClientState;
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

    public readonly Dictionary<uint, string> MeshPaths = [];
    public readonly Dictionary<string, SceneExtractor.Mesh> Meshes;

    public readonly Dictionary<ulong, BgPart> BgParts = [];
    public readonly Dictionary<ulong, CollisionBox> Colliders = [];

    public record struct BgPart(ulong Key, string Path, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask);
    public record struct CollisionBox(ulong Key, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask);

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

    // initialized by NumTilesInRow setter
    private SortedSet<ulong>[,] _cacheKeys = null!;

    public SortedSet<ulong> this[int x, int z] => _cacheKeys[x, z] ??= [];

    public unsafe SceneTracker()
    {
        Meshes = MeshesGlobal.ToDictionary();

        Service.ClientState.ZoneInit += OnZoneInit;

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsSetActiveDelegate>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxSetActiveDelegate>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();

        NumTilesInRow = 16;
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= OnZoneInit;

        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    public void OnPluginInit() => Reload(true);

    private unsafe void OnZoneInit(ZoneInitEventArgs args) => Reload(false);

    private unsafe void Reload(bool pluginInit)
    {
        RebuildCache();
        Terrains.Clear();
        BgParts.Clear();
        Colliders.Clear();

        FillFromLayout(LayoutWorld.Instance()->GlobalLayout, pluginInit);
        FillFromLayout(LayoutWorld.Instance()->ActiveLayout, pluginInit);
    }

    private void RebuildCache() => _cacheKeys = new SortedSet<ulong>[NumTilesInRow, NumTilesInRow];

    private unsafe void FillFromLayout(LayoutManager* layout, bool pluginInit)
    {
        foreach (var (k, v) in layout->Terrains)
            Terrains.Add($"{v.Value->PathString}/collision");

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

    private unsafe SceneExtractor.Mesh LoadFileMesh(uint crc, string path)
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
        mesh.MeshType = SceneExtractor.MeshType.FileMesh;
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
            if (BgParts.ContainsKey(key))
                return;

            ulong mat = ((ulong)thisPtr->CollisionMaterialIdHigh << 32) | thisPtr->CollisionMaterialIdLow;
            ulong matMask = ((ulong)thisPtr->CollisionMaterialMaskHigh << 32) | thisPtr->CollisionMaterialMaskLow;

            if (thisPtr->AnalyticShapeDataCrc != 0)
            {
                if (!AnalyticShapes.TryGetValue(thisPtr->AnalyticShapeDataCrc, out var shape))
                {
                    Service.Log.Warning($"unable to fetch analytic shape data for {thisPtr->AnalyticShapeDataCrc} - this is a bug!");
                    return;
                }

                var transform = *thisPtr->GetTransformImpl();
                var mtxBounds = Matrix4x4.CreateScale((shape.bbMax - shape.bbMin) * 0.5f);
                mtxBounds.Translation = (shape.bbMin + shape.bbMax) * 0.5f;
                var fullTransform = mtxBounds * shape.transform.Compose() * transform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);

                switch ((FileLayerGroupAnalyticCollider.Type)shape.transform.Type)
                {
                    case FileLayerGroupAnalyticCollider.Type.Box:
                        AddBgPart(key, new BgPart(key, _keyAnalyticBox, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Sphere:
                        AddBgPart(key, new BgPart(key, _keyAnalyticSphere, resultingTransform, SceneExtractor.CalculateSphereBounds(key, ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Cylinder:
                        AddBgPart(key, new BgPart(key, _keyMeshCylinder, resultingTransform, SceneExtractor.CalculateBoxBounds(ref resultingTransform), mat, matMask));
                        break;
                    case FileLayerGroupAnalyticCollider.Type.Plane:
                        AddBgPart(key, new BgPart(key, _keyAnalyticPlaneSingle, resultingTransform, SceneExtractor.CalculatePlaneBounds(ref resultingTransform), mat, matMask));
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
                    mesh = LoadFileMesh(thisPtr->CollisionMeshPathCrc, path);

                var transform = *thisPtr->GetTransformImpl();
                var t2 = new Matrix4x3(transform.Compose());
                var bounds = SceneExtractor.CalculateMeshBounds(mesh, ref t2);

                AddBgPart(key, new BgPart(key, path, t2, bounds, mat, matMask));
            }
        }
        else
        {
            RemoveBgPart(key);
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
            if (Colliders.ContainsKey(key))
                return;

            SceneExtractor.Mesh? mesh = null;

            if (cast->PcbPathCrc != 0 && !MeshPaths.ContainsKey(cast->PcbPathCrc))
            {
                var path = MeshPaths[cast->PcbPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref thisPtr->Layout->CrcToPath, cast->PcbPathCrc));
                if (!Meshes.TryGetValue(path, out mesh))
                    mesh = LoadFileMesh(cast->PcbPathCrc, path);
            }

            var transform = new Matrix4x3(cast->Transform.Compose());
            switch (cast->TriggerBoxLayoutInstance.Type)
            {
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box:
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere:
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculateSphereBounds(key, ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder:
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane:
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh:
                    if (mesh == null)
                    {
                        Service.Log.Warning($"Mesh-type collider found with path CRC {cast->PcbPathCrc}, but no matching mesh exists! This is a bug");
                        return;
                    }
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculateMeshBounds(mesh!, ref transform), mat, matMask));
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided:
                    AddCollider(key, new CollisionBox(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask));
                    break;
            }
        }
        else
        {
            RemoveCollider(key);
        }
    }

    private void AddBgPart(ulong key, BgPart part)
    {
        BgParts[key] = part;
        AddKey(key, part.Bounds);
    }

    private void RemoveBgPart(ulong key)
    {
        if (BgParts.TryGetValue(key, out var part))
        {
            BgParts.Remove(key);
            RemoveKey(key, part.Bounds);
        }
    }

    private void AddCollider(ulong key, CollisionBox box)
    {
        Colliders[key] = box;
        AddKey(key, box.Bounds);
    }

    private void RemoveCollider(ulong key)
    {
        if (Colliders.TryGetValue(key, out var box))
        {
            Colliders.Remove(key);
            RemoveKey(key, box.Bounds);
        }
    }

    private void AddKey(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                this[i, j].Add(key);
            }
        }
    }

    private void RemoveKey(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                this[i, j].Remove(key);
            }
        }
    }

    private ((int, int) Min, (int, int) Max) GetTiles(AABB box)
    {
        var min = box.Min - new Vector3(-1024);
        var max = box.Max - new Vector3(-1024);

        return (((int)min.X / TileLength, (int)min.Z / TileLength), ((int)max.X / TileLength, (int)max.Z / TileLength));
    }
}

