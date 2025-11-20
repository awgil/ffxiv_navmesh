using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Threading;

namespace Navmesh;

public abstract class Subscribable<TVal> : IObservable<TVal>, IDisposable
{
    protected readonly List<Subscription<TVal>> _subscribers = [];

    public IDisposable Subscribe(IObserver<TVal> observer)
    {
        var sub = new Subscription<TVal>(this, observer);
        _subscribers.Add(sub);
        return sub;
    }

    public void Unsubscribe(Subscription<TVal> subscriber)
    {
        _subscribers.Remove(subscriber);
    }

    protected void Notify(TVal val)
    {
        foreach (var s in _subscribers)
            s.Observer.OnNext(val);
    }

    public virtual void Dispose(bool disposing) { }
    public void Dispose()
    {
        _subscribers.Clear();
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

public sealed class Subscription<TVal>(Subscribable<TVal>? parent, IObserver<TVal> observer) : IDisposable
{
    private Subscribable<TVal>? parent = parent;
    public IObserver<TVal> Observer { get; } = observer;

    public void Dispose()
    {
        parent?.Unsubscribe(this);
        parent = null;
    }
}

public sealed partial class ColliderSet : Subscribable<ColliderSet.InstanceChangeArgs>
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

    record class MaybeEnabled
    {
        public InstanceWithMesh? Instance;
        public bool Enabled;
        public bool Destroyed;
        public bool Dirty;
    }

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _objects = [];
    public int NumObjects => _objects.Where(o => o.Value.Instance != null).Count();

    private readonly HookAddress<BgPartsLayoutInstance.Delegates.CreatePrimary> _bgCreate;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.DestroyPrimary> _bgDestroy;
    private readonly HookAddress<CollisionBoxLayoutInstance.Delegates.CreatePrimary> _boxCreate;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.DestroyPrimary> _boxDestroy;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<ColliderSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;

    public readonly record struct InstanceChangeArgs(int X, int Z, ulong Key, InstanceWithMesh? Instance);

    public unsafe ColliderSet()
    {
        Service.ClientState.ZoneInit += OnZoneInit;

        _bgCreate = new("48 89 5C 24 ?? 57 48 83 EC 20 8B 41 24 4D 8B C8 45 33 C0 48 8B FA ", BgCreateDetour, false);
        _bgDestroy = new("40 53 48 83 EC 20 48 8B D9 48 8B 49 30 48 85 C9 74 27 48 8B 01 FF 50 08 83 7B 24 FF 75 13 48 8B 4B 30 48 85 C9 74 12 48 8B 01 BA ?? ?? ?? ?? FF 10 48 C7 43 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 83 79 40 00 ", BgDestroyDetour, false);
        _boxCreate = new("48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 55 48 8B EC 48 83 EC 60 48 8B 05 ?? ?? ?? ?? 49 8B D8 48 8B F9 4C 8B CA 48 8D 55 E0 48 8B B0 ?? ?? ?? ?? 8B 41 74 41 33 00 4C 8D 45 D0 83 E0 0F 48 C7 45 ?? ?? ?? ?? ?? 31 41 74 48 8D 4D F0 C7 45 ?? ?? ?? ?? ?? 48 C7 45 ?? ?? ?? ?? ?? C7 45 ?? ?? ?? ?? ?? 48 C7 45 ?? ?? ?? ?? ?? C7 45 ?? ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 03 ", BoxCreateDetour, false);
        _boxDestroy = new("40 53 48 83 EC 20 48 8B D9 48 8B 49 30 48 85 C9 74 31 ", BoxDestroyDetour, false);
    }

    public unsafe void Initialize(bool emitChangeEvents = true)
    {
        foreach (var obj in _objects.Values)
            if (obj.Instance != null)
                NotifyObject(obj.Instance, false);
        _objects.Clear();

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

    public override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Service.ClientState.ZoneInit -= OnZoneInit;
            _bgCreate.Dispose();
            _bgDestroy.Dispose();
            _boxCreate.Dispose();
            _boxDestroy.Dispose();
        }
    }

    private readonly List<Pointer<ILayoutInstance>> _destroyed = [];

    public unsafe void Tick()
    {
        _destroyed.Clear();
        foreach (var (ptr, inst) in _objects)
        {
            if (inst.Destroyed)
                _destroyed.Add(ptr);
            else
                UpdateObject(ptr, inst);
        }

        foreach (var d in _destroyed)
        {
            if (_objects.Remove(d, out var destroyed))
                if (destroyed.Instance is { } destroyedObject)
                    NotifyObject(destroyedObject, false);
        }
    }

    private unsafe void UpdateObject(Pointer<ILayoutInstance> pointer, MaybeEnabled obj)
    {
        // this layout instance doesn't have a mesh/collider associated with it, so it doesn't actually have collision - ignore it
        if (obj.Instance == null)
            return;

        var modified = obj.Dirty;
        obj.Dirty = false;

        var wasEnabled = obj.Enabled;
        obj.Enabled = (pointer.Value->Flags3 & 0x10) != 0;
        modified |= obj.Enabled != wasEnabled;

        var matPrev = obj.Instance.Instance.ForceSetPrimFlags;
        var matCur = GetMaterialFlags(pointer.Value);
        if (matPrev != matCur)
        {
            obj.Instance.Instance.ForceSetPrimFlags = matCur;
            modified = true;
        }

        if (modified)
            NotifyObject(obj.Instance, obj.Enabled);
    }

    private void NotifyObject(InstanceWithMesh inst, bool enabled)
    {
        var key = inst.Instance.Id;
        var bounds = inst.Instance.WorldBounds;
        var min = bounds.Min - new Vector3(-1024);
        var max = bounds.Max - new Vector3(-1024);

        var imin = (int)min.X / TileUnits;
        var imax = (int)max.X / TileUnits;
        var jmin = (int)min.Z / TileUnits;
        var jmax = (int)max.Z / TileUnits;
        for (var i = Math.Max(0, imin); i <= Math.Min(imax, RowLength - 1); i++)
            for (var j = Math.Max(0, jmin); j <= Math.Min(jmax, RowLength - 1); j++)
                Notify(new(i, j, key, enabled ? inst : null));
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
    }

    private unsafe void ActivateExistingLayout(LayoutManager* layout)
    {
        if (layout == null)
            return;

        var bgParts = layout->InstancesByType.FindPtr(InstanceType.BgPart);
        if (bgParts != null)
            foreach (var (k, v) in *bgParts)
                CreateObject((BgPartsLayoutInstance*)v.Value);

        var boxes = layout->InstancesByType.FindPtr(InstanceType.CollisionBox);
        if (boxes != null)
            foreach (var (k, v) in *boxes)
                CreateObject((CollisionBoxLayoutInstance*)v.Value);
    }

    private unsafe void BgCreateDetour(BgPartsLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _bgCreate.Original(thisPtr, transform, pathOrType);
        CreateObject(thisPtr);
    }

    private unsafe void BgDestroyDetour(BgPartsLayoutInstance* thisPtr)
    {
        _bgDestroy.Original(thisPtr);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
            obj.Destroyed = true;
    }

    private unsafe void BoxCreateDetour(CollisionBoxLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _boxCreate.Original(thisPtr, transform, pathOrType);
        CreateObject(thisPtr);
    }

    private unsafe void BoxDestroyDetour(TriggerBoxLayoutInstance* thisPtr)
    {
        _boxDestroy.Original(thisPtr);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
            obj.Destroyed = true;
    }

    private unsafe void CreateObject(BgPartsLayoutInstance* thisPtr)
    {
        _objects[&thisPtr->ILayoutInstance] = new() { Instance = SceneTool.Get().CreateInstance(thisPtr), Dirty = true };
    }

    private unsafe void CreateObject(CollisionBoxLayoutInstance* thisPtr)
    {
        _objects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance] = new() { Instance = SceneTool.Get().CreateInstance(thisPtr), Dirty = true };
    }
}

public sealed class Grid : Subscribable<Grid.TileChangeArgs>
{
    public record struct TileChangeArgs(int X, int Z, SortedDictionary<ulong, InstanceWithMesh> Objects);

    private readonly SortedDictionary<ulong, InstanceWithMesh>[,] _tiles = new SortedDictionary<ulong, InstanceWithMesh>[16, 16];

    private readonly Lock _lock = new();

    public void Apply(ColliderSet.InstanceChangeArgs change)
    {
        HashSet<(int, int)> modified = [];
        lock (_lock)
        {
            _tiles[change.X, change.Z] ??= [];
            if (change.Instance == null)
            {
                if (_tiles[change.X, change.Z].Remove(change.Key))
                {
                    //Service.Log.Debug($"Removed object {change.Key:X16} from tile {change.X}x{change.Z}, count is now {_tiles[change.X, change.Z].Count}");
                    modified.Add((change.X, change.Z));
                }
            }
            else
            {
                _tiles[change.X, change.Z][change.Key] = change.Instance;
                //Service.Log.Debug($"Added object {change.Key:X16} to tile {change.X}x{change.Z}, count is now {_tiles[change.X, change.Z].Count}");
                modified.Add((change.X, change.Z));
            }
            foreach (var (x, z) in modified)
                Notify(new(x, z, _tiles[x, z]));
        }
    }
}
