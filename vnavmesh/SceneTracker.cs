using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Lumina.Data.Files;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
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
        [_keyAnalyticBox] = new() { Path = _keyAnalyticBox, Parts = SceneExtractor.MeshBox, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticSphere] = new() { Path = _keyAnalyticSphere, Parts = SceneExtractor.MeshSphere, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticCylinder] = new() { Path = _keyAnalyticCylinder, Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.AnalyticShape },
        [_keyAnalyticPlaneSingle] = new() { Path = _keyAnalyticPlaneSingle, Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyAnalyticPlaneDouble] = new() { Path = _keyAnalyticPlaneDouble, Parts = SceneExtractor.MeshPlane, MeshType = SceneExtractor.MeshType.AnalyticPlane },
        [_keyMeshCylinder] = new() { Path = _keyMeshCylinder, Parts = SceneExtractor.MeshCylinder, MeshType = SceneExtractor.MeshType.CylinderMesh }
    };

    public readonly Dictionary<uint, AnalyticShape?> AnalyticShapes = [];
    public record struct AnalyticShape(Transform Transform, Vector3 BoundsMin, Vector3 BoundsMax);

    private readonly Dictionary<uint, string> MeshPaths = [];
    public readonly Dictionary<string, SceneExtractor.Mesh> Meshes;

    private readonly ConcurrentDictionary<ulong, AABB> _allObjects = [];

    public record struct Instance(ulong Key, string Path, Matrix4x3 Transform, AABB Bounds, ulong MaterialId, ulong MaterialMask, InstanceType Type);

    private readonly Hook<BgPartsLayoutInstance.Delegates.SetActive> _bgPartsSetActiveHook;
    private readonly Hook<TriggerBoxLayoutInstance.Delegates.SetActive> _triggerBoxSetActiveHook;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<SceneTracker> ZoneChanged = delegate { };

    internal TileChangeset?[,] _tiles = new TileChangeset?[0, 0];
    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;
    public uint[] ActiveFestivals = new uint[4];

    internal class TileChangeset(int x, int z)
    {
        public int X = x;
        public int Z = z;
        public ConcurrentDictionary<ulong, LayoutObject> Objects = [];
        public bool Changed;
    }

    public readonly record struct LayoutObject(SceneExtractor.Mesh Mesh, SceneExtractor.MeshInstance Instance, InstanceType Type);
    public readonly record struct Tile(int X, int Z, SortedDictionary<ulong, LayoutObject> Objects, ReadOnlyDictionary<string, SceneExtractor.Mesh> AllMeshes, NavmeshCustomization Customization, uint Zone)
    {
        public readonly IEnumerable<LayoutObject> ObjectsByMesh(Func<SceneExtractor.Mesh, bool> func) => Objects.Values.Where(o => func(o.Mesh));
        public readonly IEnumerable<LayoutObject> ObjectsByPath(string path) => ObjectsByMesh(m => m.Path == path);

        public readonly void RemoveObjects(Func<LayoutObject, bool> filter)
        {
            foreach (var k in Objects.Keys.ToList())
                if (filter(Objects[k]))
                    Objects.Remove(k);
        }

        public readonly string GetCacheKey()
        {
            var span = Objects.Keys.ToArray();
            var bytes = new byte[span.Length * sizeof(ulong)];
            Buffer.BlockCopy(span, 0, bytes, 0, bytes.Length);
            return Convert.ToHexString(MD5.HashData(bytes));
        }
    }

    private bool _anyChanged;

    public unsafe SceneTracker()
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

        var festivals = world->ActiveLayout->ActiveFestivals;
        for (var i = 0; i < festivals.Length; i++)
            ActiveFestivals[i] = ((uint)festivals[i].Phase << 16) | festivals[i].Id;

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

    public IEnumerable<Tile> GetTileChanges()
    {
        if (!_anyChanged)
            yield break;

        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (_tiles[i, j]?.Changed == true)
                {
                    _tiles[i, j]!.Changed = false;
                    yield return GetOneTile(i, j)!.Value;
                }
    }

    public IEnumerable<Tile> GetAllTiles()
    {
        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (GetOneTile(i, j) is { } t)
                    yield return t;
    }

    public Tile? GetOneTile(int i, int j)
    {
        var t = _tiles[i, j];
        if (t == null)
            return null;
        return new(t.X, t.Z, new SortedDictionary<ulong, LayoutObject>(t.Objects), Meshes.AsReadOnly(), Customization, LastLoadedZone);
    }

    private unsafe string GetCollisionMeshPathByCrc(uint crc, LayoutManager* layout)
    {
        if (MeshPaths.TryGetValue(crc, out var path))
            return path;

        return MeshPaths[crc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, crc));
    }

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

    private SceneExtractor.Mesh GetMeshByPath(string path, SceneExtractor.MeshType meshType)
    {
        if (Meshes.TryGetValue(path, out var mesh))
            return mesh;

        return LoadFileMesh(path, meshType);
    }

    private readonly Dictionary<uint, HashSet<uint>> BoundaryLayers = [];

    private void OnZoneInit(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        InitZone(obj.TerritoryType.RowId);
        for (var i = 0; i < obj.ActiveFestivals.Length; i++)
            ActiveFestivals[i] = ((uint)obj.ActiveFestivals[i].Unknown1 << 16) | obj.ActiveFestivals[i].Unknown0;
        ZoneChanged.Invoke(this);
    }

    private unsafe void InitZone(uint zoneId)
    {
        LastLoadedZone = zoneId;
        Customization = NavmeshCustomizationRegistry.ForTerritory(zoneId);
        RowLength = Customization.Settings.NumTiles[0];
        _allObjects.Clear();
        _tiles = new TileChangeset?[RowLength, RowLength];

        var tt = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(zoneId);
        if (tt == null)
        {
            Service.Log.Warning($"not building mesh for {zoneId}");
            return;
        }

        var ttBg = tt.Value.Bg.ToString();
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
                    AddObject(path, new SceneExtractor.MeshInstance(0xFFFF000000000000u | ((ulong)zoneId << 32) | ((ulong)(long)entry.MeshId), Matrix4x3.Identity, entry.Bounds, (ulong)0, 0), default);
                }
            }
        }

        BoundaryLayers.TryAdd(zoneId, []);
        if (Service.DataManager.GetFile<LgbFile>(bgPrefix + "level/bg.lgb") is { } bg)
            foreach (var layer in bg.Layers)
                if (layer.Name.Equals("bg_endofworld_01", StringComparison.InvariantCultureIgnoreCase))
                    BoundaryLayers[zoneId].Add(layer.LayerId & 0xFFFF);

        // TODO: CRC to analytic shape data
    }

    private unsafe void ActivateExistingLayout(LayoutManager* layout)
    {
        if (layout == null)
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
                if (!TryGetAnalyticShapeData(thisPtr->AnalyticShapeDataCrc, thisPtr->Layout, out var shape))
                    return;

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
            ulong mat = ((ulong)cast->MaterialIdHigh << 32) | cast->MaterialIdLow;
            ulong matMask = ((ulong)cast->MaterialMaskHigh << 32) | cast->MaterialMaskLow;

            if (_allObjects.ContainsKey(key))
                return;

            var zone = LastLoadedZone;
            bool isBoundaryObject = BoundaryLayers.TryGetValue(zone, out var l) && l.Contains(thisPtr->Layer->Id);

            var transform = new Matrix4x3(cast->Transform.Compose());
            switch (cast->TriggerBoxLayoutInstance.Type)
            {
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box:
                    AddObject(_keyAnalyticBox, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere:
                    AddObject(_keyAnalyticSphere, new(key, transform, SceneExtractor.CalculateSphereBounds(key, ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder:
                    AddObject(_keyAnalyticCylinder, new(key, transform, SceneExtractor.CalculateBoxBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane:
                    AddObject(_keyAnalyticPlaneSingle, new(key, transform, SceneExtractor.CalculatePlaneBounds(ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh:
                    if (cast->PcbPathCrc != 0)
                    {
                        var path = GetCollisionMeshPathByCrc(cast->PcbPathCrc, thisPtr->Layout);
                        var mesh = GetMeshByPath(path, SceneExtractor.MeshType.FileMesh);
                        AddObject(path, new(key, transform, SceneExtractor.CalculateMeshBounds(mesh, ref transform), mat, matMask), InstanceType.CollisionBox, isBoundaryObject);
                    }
                    break;
                case FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided:
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
                var tile = _tiles[i, j] ??= new(i, j);
                if (tile.Objects.TryAdd(instance.Id, new(mesh, instance, type)))
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
                var tile = _tiles[i, j] ??= new(i, j);
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
}

