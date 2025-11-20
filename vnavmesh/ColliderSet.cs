using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    record class MaybeEnabled
    {
        public InstanceWithMesh? Instance { get; set; }
        public bool Enabled { get; set; }
        public bool Destroyed { get; set; }
        public bool Dirty { get; set; }
    }

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _objects = [];

    private readonly HookAddress<BgPartsLayoutInstance.Delegates.CreatePrimary> _bgCreate;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.DestroyPrimary> _bgDestroy;

    private readonly HookAddress<CollisionBoxLayoutInstance.Delegates.CreatePrimary> _boxCreate;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.DestroyPrimary> _boxDestroy;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetTranslationImpl> _boxTrans;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetRotationImpl> _boxRot;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetScaleImpl> _boxScale;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<ColliderSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;

    public readonly record struct InstanceChangeArgs(uint Zone, int X, int Z, ulong Key, InstanceWithMesh? Instance);

    public unsafe ColliderSet()
    {
        Service.ClientState.ZoneInit += OnZoneInit;

        var bgpVtbl = (BgPartsLayoutInstance.BgPartsLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? 90 48 89 50 F8 ");
        var collVtbl = (CollisionBoxLayoutInstance.CollisionBoxLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 85 FF 74 20 48 8B CF E8 ?? ?? ?? ?? 4C 8B F0 48 8D 55 C7 45 33 C0 49 8B CE 48 8B 00 FF 50 08 E9 ?? ?? ?? ?? 66 44 39 73 ?? 75 45 44 38 73 1F 74 0A 48 8B 43 08 83 78 1C 06 75 35 48 8B 4B 08 4C 39 B1 ?? ?? ?? ?? 74 28 85 F6 75 24 45 84 FF 74 1F B2 3A ", 0x17);

        _bgCreate = new((nint)bgpVtbl->CreatePrimary, BgCreateDetour, false);
        _bgDestroy = new((nint)bgpVtbl->DestroyPrimary, BgDestroyDetour, false);

        // collisionboxes often get resized *after* being initialized instead of before, how annoying
        _boxCreate = new((nint)collVtbl->CreatePrimary, BoxCreateDetour, false);
        _boxDestroy = new((nint)collVtbl->DestroyPrimary, BoxDestroyDetour, false);
        _boxTrans = new((nint)collVtbl->SetTranslationImpl, BoxTransDetour, false);
        _boxRot = new((nint)collVtbl->SetRotationImpl, BoxRotDetour, false);
        _boxScale = new((nint)collVtbl->SetScaleImpl, BoxScaleDetour, false);
    }

    public unsafe void Initialize()
    {
        InitZone(Service.ClientState.TerritoryType);

        var world = LayoutWorld.Instance();
        ActivateExistingLayout(world->GlobalLayout);
        ActivateExistingLayout(world->ActiveLayout);

        _bgCreate.Enabled = true;
        _bgDestroy.Enabled = true;
        _boxCreate.Enabled = true;
        _boxDestroy.Enabled = true;

        _boxTrans.Enabled = true;
        _boxRot.Enabled = true;
        _boxScale.Enabled = true;

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
            _boxTrans.Dispose();
            _boxRot.Dispose();
            _boxScale.Dispose();
        }
    }

    private readonly List<Pointer<ILayoutInstance>> _destroyed = [];

    public void Poll()
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
        var k = obj.Instance.Instance.Id;
        if (modified)
            Service.Log.Verbose($"object {k:X16} was dirty");

        var wasEnabled = obj.Enabled;
        obj.Enabled = (pointer.Value->Flags3 & 0x10) != 0;
        if (obj.Enabled != wasEnabled)
            Service.Log.Verbose($"object {k:X16} was " + (wasEnabled ? "disabled" : "enabled"));
        modified |= obj.Enabled != wasEnabled;

        var matPrev = obj.Instance.Instance.ForceSetPrimFlags;
        var matCur = GetMaterialFlags(pointer.Value);
        if (matPrev != matCur)
        {
            Service.Log.Verbose($"object {k:X16} material changed from {matPrev} to {matCur}");
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

        enabled &= !inst.Instance.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.Transparent);

        var imin = (int)min.X / TileUnits;
        var imax = (int)max.X / TileUnits;
        var jmin = (int)min.Z / TileUnits;
        var jmax = (int)max.Z / TileUnits;
        for (var i = Math.Max(0, imin); i <= Math.Min(imax, RowLength - 1); i++)
            for (var j = Math.Max(0, jmin); j <= Math.Min(jmax, RowLength - 1); j++)
                Notify(new(LastLoadedZone, i, j, key, enabled ? inst : null));
    }

    private unsafe SceneExtractor.PrimitiveFlags GetMaterialFlags(ILayoutInstance* val)
    {
        switch (val->Id.Type)
        {
            case InstanceType.BgPart:
                var v = (BgPartsLayoutInstance*)val;
                var id = (ulong)v->CollisionMaterialIdHigh << 32 | v->CollisionMaterialIdLow;
                var mask = (ulong)v->CollisionMaterialMaskHigh << 32 | v->CollisionMaterialMaskLow;
                return SceneExtractor.ExtractMaterialFlags(id & mask);
            case InstanceType.CollisionBox:
                var c = (CollisionBoxLayoutInstance*)val;
                var id2 = (ulong)c->MaterialIdHigh << 32 | c->MaterialIdLow;
                var mask2 = (ulong)c->MaterialMaskHigh << 32 | c->MaterialMaskLow; ;
                return SceneExtractor.ExtractMaterialFlags(id2 & mask2);
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

        _objects.Clear();
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

    private unsafe void BoxTransDetour(TriggerBoxLayoutInstance* thisPtr, Vector3* value)
    {
        _boxTrans.Original(thisPtr, value);
        if (thisPtr->Id.Type == InstanceType.CollisionBox)
        {
            var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
            Service.Log.Verbose($"changing translation of {k:X16} to {*value}");
            CreateObject((CollisionBoxLayoutInstance*)thisPtr);
        }
    }

    private unsafe void BoxRotDetour(TriggerBoxLayoutInstance* thisPtr, Quaternion* value)
    {
        _boxRot.Original(thisPtr, value);
        if (thisPtr->Id.Type == InstanceType.CollisionBox)
        {
            var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
            Service.Log.Verbose($"changing rotation of {k:X16} to {*value}");
            CreateObject((CollisionBoxLayoutInstance*)thisPtr);
        }
    }

    private unsafe void BoxScaleDetour(TriggerBoxLayoutInstance* thisPtr, Vector3* value)
    {
        _boxScale.Original(thisPtr, value);
        if (thisPtr->Id.Type == InstanceType.CollisionBox)
        {
            var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
            Service.Log.Verbose($"changing scale of {k:X16} to {*value}");
            CreateObject((CollisionBoxLayoutInstance*)thisPtr);
        }
    }

    private unsafe void CreateObject(BgPartsLayoutInstance* thisPtr)
    {
        var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
        //Service.Log.Debug($"creating object {k:X16}");
        _objects[&thisPtr->ILayoutInstance] = new() { Instance = SceneTool.Get().CreateInstance(thisPtr), Dirty = true };
    }

    private unsafe void CreateObject(CollisionBoxLayoutInstance* thisPtr)
    {
        var k = SceneTool.GetKey(&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance);
        //Service.Log.Debug($"creating object {k:X16}");
        _objects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance] = new() { Instance = SceneTool.Get().CreateInstance(thisPtr), Dirty = true };
    }
}

public sealed class Grid : Subscribable<Grid.TileChangeArgs>
{
    public record struct TileChangeArgs(uint Zone, int X, int Z, SortedDictionary<ulong, InstanceWithMesh> Objects);

    private readonly SortedDictionary<ulong, InstanceWithMesh>[,] _tiles = new SortedDictionary<ulong, InstanceWithMesh>[16, 16];

    private readonly Lock _lock = new();

    public IObservable<IList<TileChangeArgs>> Debounced(TimeSpan delay) => this.Window(() => this.Throttle(delay)).SelectMany(result => result.Distinct(t => (t.X, t.Z)).ToList());

    public void Watch(ColliderSet set, CancellationToken token)
    {
        set.ForEachAsync(Apply, token);
        set.ZoneChanged += Clear;
    }

    private void Apply(ColliderSet.InstanceChangeArgs change)
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
                Notify(new(change.Zone, x, z, _tiles[x, z]));
        }
    }

    private void Clear(ColliderSet _)
    {
        lock (_lock)
        {
            foreach (var t in _tiles)
                t?.Clear();
        }
    }
}
