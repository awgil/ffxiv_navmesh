using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using FFXIVClientStructs.Interop;
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

    class MaybeEnabled<T> where T : class
    {
        public required T? Value;
        public bool Enabled;
        public AABB Bounds;

        public T? ValueIfEnabled => Enabled ? Value : null;
    }

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled<SceneExtractor.MeshInstance>> _objects = [];

    private readonly HookAddress<BgPartsLayoutInstance.Delegates.CreatePrimary> _bgCreate;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.DestroyPrimary> _bgDestroy;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.CreatePrimary> _boxCreate;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.DestroyPrimary> _boxDestroy;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<ColliderSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;

    public readonly record struct TileChangeArgs(int X, int Z, ulong Key, SceneExtractor.MeshInstance? Instance);
    public event Action<TileChangeArgs> OnTileChanged = delegate { };

    public unsafe ColliderSet()
    {
        Meshes = MeshesGlobal.ToDictionary();

        Service.ClientState.ZoneInit += OnZoneInit;

        _bgCreate = new("48 89 5C 24 ?? 57 48 83 EC 20 8B 41 24 4D 8B C8 45 33 C0 48 8B FA ", BgCreateDetour, false);
        _bgDestroy = new("40 53 48 83 EC 20 48 8B D9 48 8B 49 30 48 85 C9 74 27 48 8B 01 FF 50 08 83 7B 24 FF 75 13 48 8B 4B 30 48 85 C9 74 12 48 8B 01 BA ?? ?? ?? ?? FF 10 48 C7 43 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 83 79 40 00 ", BgDestroyDetour, false);
        _boxCreate = new("E8 ?? ?? ?? ?? 8B 43 3C 48 8B 5C 24 ?? ", BoxCreateDetour, false);
        _boxDestroy = new("40 53 48 83 EC 20 48 8B D9 48 8B 49 30 48 85 C9 74 31 ", BoxDestroyDetour, false);
    }

    public unsafe void Initialize(bool emitChangeEvents = true)
    {
        InitZone(Service.ClientState.TerritoryType);

        var world = LayoutWorld.Instance();
        ActivateExistingLayout(world->GlobalLayout);
        ActivateExistingLayout(world->ActiveLayout);

        _bgCreate.Enabled = true;
        _bgDestroy.Enabled = true;
        _boxCreate.Enabled = true;
        _boxDestroy.Enabled = true;

        ZoneChanged.Invoke(this);
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= OnZoneInit;
        _bgCreate.Dispose();
        _bgDestroy.Dispose();
        _boxCreate.Dispose();
        _boxDestroy.Dispose();
    }

    public void Tick()
    {
        foreach (var (ptr, inst) in _objects)
            UpdateObject(ptr, inst);
    }

    private unsafe void UpdateObject(Pointer<ILayoutInstance> pointer, MaybeEnabled<SceneExtractor.MeshInstance> obj)
    {
        // this layout instance doesn't have a mesh/collider associated with it, so it doesn't actually have collision - ignore it
        if (obj.Value == null)
            return;

        var modified = false;

        var wasEnabled = obj.Enabled;
        obj.Enabled = (pointer.Value->Flags3 & 0x10) != 0;
        modified |= obj.Enabled != wasEnabled;

        var matPrev = obj.Value.ForceSetPrimFlags;
        var matCur = GetMaterialFlags(pointer.Value);
        if (matPrev != matCur)
        {
            obj.Value.ForceSetPrimFlags = matCur;
            modified = true;
        }

        if (modified)
        {
            var key = (ulong)pointer.Value->Id.InstanceKey << 32 | pointer.Value->SubId;

            var bounds = obj.Value.WorldBounds;
            var min = bounds.Min - new Vector3(-1024);
            var max = bounds.Max - new Vector3(-1024);

            var imin = (int)min.X / TileUnits;
            var imax = (int)max.X / TileUnits;
            var jmin = (int)min.Z / TileUnits;
            var jmax = (int)max.Z / TileUnits;
            for (var i = Math.Max(0, imin); i <= Math.Min(imax, RowLength - 1); i++)
                for (var j = Math.Max(0, jmin); j <= Math.Min(jmax, RowLength - 1); j++)
                    OnTileChanged.Invoke(new(i, j, key, obj.Enabled ? obj.Value : null));
        }
    }

    private unsafe SceneExtractor.PrimitiveFlags GetMaterialFlags(ILayoutInstance* val)
    {
        switch (val->Id.Type)
        {
            case InstanceType.BgPart:
                var v = (BgPartsLayoutInstance*)val;
                return SceneExtractor.ExtractMaterialFlags((ulong)v->CollisionMaterialIdHigh << 32 | v->CollisionMaterialIdLow);
            case InstanceType.CollisionBox:
                var c = (CollisionBoxLayoutInstance*)val;
                return SceneExtractor.ExtractMaterialFlags((ulong)c->MaterialIdHigh << 32 | c->MaterialIdLow);
            default:
                return default;
        }
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
    }

    private unsafe void BgCreateDetour(BgPartsLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _bgCreate.Original(thisPtr, transform, pathOrType);
        _objects.TryAdd(&thisPtr->ILayoutInstance, new() { Value = LoadBgPart(thisPtr) });
    }

    private unsafe void BgDestroyDetour(BgPartsLayoutInstance* thisPtr)
    {
        _bgDestroy.Original(thisPtr);
        _objects.Remove(&thisPtr->ILayoutInstance, out _);
    }

    private unsafe void BoxCreateDetour(TriggerBoxLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _boxCreate.Original(thisPtr, transform, pathOrType);
        if (thisPtr->Id.Type == InstanceType.CollisionBox)
            _objects.TryAdd(&thisPtr->ILayoutInstance, new() { Value = LoadBox((CollisionBoxLayoutInstance*)thisPtr) });
    }

    private unsafe void BoxDestroyDetour(TriggerBoxLayoutInstance* thisPtr)
    {
        _boxDestroy.Original(thisPtr);
        _objects.Remove(&thisPtr->ILayoutInstance, out _);
    }

    private unsafe SceneExtractor.MeshInstance? LoadBgPart(BgPartsLayoutInstance* inst)
    {
        if (inst->AnalyticShapeDataCrc != 0)
        {

        }
    }

    private unsafe SceneExtractor.MeshInstance? LoadBox(CollisionBoxLayoutInstance* inst)
    {

    }
}

