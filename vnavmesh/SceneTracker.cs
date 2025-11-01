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

    public const double DebounceMS = 500.0;

    // initialized by NumTilesInRow setter
    private SortedSet<ulong>[,] _cacheKeys = null!;
    public double[,] Timers { get; private set; } = null!;

    public SortedSet<ulong> this[int x, int z] => _cacheKeys[x, z] ??= [];

    public event Action<List<ChangedTile>> TileChanged = delegate { };

    public record struct ChangedTile(int X, int Z, List<ulong> SortedIds);

    public unsafe SceneTracker()
    {
        Meshes = MeshesGlobal.ToDictionary();

        Service.ClientState.ZoneInit += OnZoneInit;
        Service.Framework.Update += Tick;

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsSetActiveDelegate>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxSetActiveDelegate>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();

        NumTilesInRow = 16;
    }

    public void Dispose()
    {
        Service.Framework.Update -= Tick;
        Service.ClientState.ZoneInit -= OnZoneInit;

        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    private void Tick(Dalamud.Plugin.Services.IFramework framework)
    {
        var tick = framework.UpdateDelta.TotalMilliseconds;

        List<ChangedTile> tilesChanged = [];
        for (var i = 0; i < Timers.GetLength(0); i++)
        {
            for (var j = 0; j < Timers.GetLength(1); j++)
            {
                ref var cnt = ref Timers[i, j];
                var pos = cnt > 0;
                cnt -= tick;

                if (pos && cnt <= 0)
                    tilesChanged.Add(new(i, j, [.. _cacheKeys[i, j]]));
            }
        }

        if (tilesChanged.Count > 0)
            TileChanged.Invoke(tilesChanged);
    }

    public void OnPluginInit() => Reload(true);

    private unsafe void OnZoneInit(ZoneInitEventArgs args) => Reload(false);

    private void RebuildCache()
    {
        _cacheKeys = new SortedSet<ulong>[NumTilesInRow, NumTilesInRow];
        Timers = new double[NumTilesInRow, NumTilesInRow];
    }

    private unsafe void Reload(bool pluginInit)
    {
        RebuildCache();
        Terrains.Clear();
        BgParts.Clear();
        Colliders.Clear();

        FillFromLayout(LayoutWorld.Instance()->GlobalLayout, pluginInit);
        FillFromLayout(LayoutWorld.Instance()->ActiveLayout, pluginInit);
    }

    private unsafe void FillFromLayout(LayoutManager* layout, bool pluginInit)
    {
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
            if (BgParts.ContainsKey(key))
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
                    mesh = LoadFileMesh(path, SceneExtractor.MeshType.FileMesh);

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
                    mesh = LoadFileMesh(path, SceneExtractor.MeshType.FileMesh);
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
        AddKeyWithBounds(key, part.Bounds);
    }

    private void RemoveBgPart(ulong key)
    {
        if (BgParts.TryGetValue(key, out var part))
        {
            BgParts.Remove(key);
            RemoveKeyWithBounds(key, part.Bounds);
        }
    }

    private void AddCollider(ulong key, CollisionBox box)
    {
        Colliders[key] = box;
        AddKeyWithBounds(key, box.Bounds);
    }

    private void RemoveCollider(ulong key)
    {
        if (Colliders.TryGetValue(key, out var box))
        {
            Colliders.Remove(key);
            RemoveKeyWithBounds(key, box.Bounds);
        }
    }

    private void AddKeyWithBounds(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                this[i, j].Add(key);
                Timers[i, j] = DebounceMS;
            }
        }
    }

    private void RemoveKeyWithBounds(ulong key, AABB bounds)
    {
        var (tileBoundsMin, tileBoundsMax) = GetTiles(bounds);
        for (var i = tileBoundsMin.Item1; i <= tileBoundsMax.Item1; i++)
        {
            if (i < 0 || i >= NumTilesInRow) continue;
            for (var j = tileBoundsMin.Item2; j <= tileBoundsMax.Item2; j++)
            {
                if (j < 0 || j >= NumTilesInRow) continue;
                this[i, j].Remove(key);
                Timers[i, j] = DebounceMS;
            }
        }
    }

    private ((int, int) Min, (int, int) Max) GetTiles(AABB box)
    {
        var min = box.Min - new Vector3(-1024);
        var max = box.Max - new Vector3(-1024);

        return (((int)min.X / TileLength, (int)min.Z / TileLength), ((int)max.X / TileLength, (int)max.Z / TileLength));
    }

    public static string HashKeys(IList<ulong> keys)
    {
        var span = keys.ToArray();
        var bytes = new byte[span.Length * sizeof(ulong)];
        Buffer.BlockCopy(span, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(MD5.HashData(bytes));
    }
}

