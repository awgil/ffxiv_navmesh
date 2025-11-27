using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
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
        foreach (var s in _subscribers.ToList())
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

// tracks "enabled" flag, material flags, and world transforms of all objects in the current zone that have an attached collider
public sealed partial class LayoutObjectSet : Subscribable<LayoutObjectSet.InstanceChangeArgs>
{
    unsafe record class MaybeEnabled
    {
        public InstanceWithMesh? Instance { get; set; }
        public bool Enabled { get; set; }
        public bool IsBgPart { get; set; }
        public Transform Transform { get; set; }
        public bool ColliderEnabled
        {
            get => IsBgPart || field;
            set;
        }
        public bool Destroyed { get; set; }
        public bool Dirty { get; set; }

        public void Recreate(ILayoutInstance* ptr, Transform transform)
        {
            Transform = transform;
            if (IsBgPart)
                Instance = SceneTool.Get().CreateInstance((BgPartsLayoutInstance*)ptr, transform);
            else
                Instance = SceneTool.Get().CreateInstance((CollisionBoxLayoutInstance*)ptr, transform);

            // this is probably impossible
            if (Instance == null)
                Destroyed = true;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct TraceObjectKey
    {
        [FieldOffset(0)] public uint SubId;
        [FieldOffset(4)] public uint InstanceKey;

        [FieldOffset(0)] public ulong Full;

        public readonly bool IsSet => Full > 0;
        public readonly bool IsAll => Full == ulong.MaxValue;
    }

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _objects = [];
    private readonly ConcurrentDictionary<ulong, Transform> _transformOverride = [];

    private readonly HookAddress<BgPartsLayoutInstance.Delegates.CreatePrimary> _bgCreate;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.DestroyPrimary> _bgDestroy;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetActive> _bgToggle;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetTransformImpl> _bgTransF;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetTranslationImpl> _bgTrans;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetRotationImpl> _bgRot;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetScaleImpl> _bgScale;

    private readonly HookAddress<CollisionBoxLayoutInstance.Delegates.CreatePrimary> _boxCreate;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.DestroyPrimary> _boxDestroy;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetActive> _boxToggle;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetTranslationImpl> _boxTrans;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetRotationImpl> _boxRot;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetScaleImpl> _boxScale;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetColliderActive> _boxColl;

    private readonly HookAddress<SharedGroupLayoutInstance.Delegates.InitTimelines> _sgInit;

    private bool _watchColliders;

    // western thanalan boat
    //private static readonly TraceObjectKey TRACE_OBJECT = new() { Full = 0x003BDC3D_03000000 };
    // moon crystal
    //private static readonly TraceObjectKey TRACE_OBJECT = new() { Full = 0x00885333_04010000 };
    // ARF elevator background
    //private static readonly TraceObjectKey TRACE_OBJECT = new() { Full = 0x0058BE03_3A250000 };
    private static readonly TraceObjectKey TRACE_OBJECT = new() { Full = 0x0031D5C7_0B000000 };
    // trace everything
    //private static readonly TraceObjectKey TRACE_OBJECT = new() { Full = ulong.MaxValue };
    // nothing
    //private static readonly TraceObjectKey TRACE_OBJECT = default;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<LayoutObjectSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;

    public readonly record struct InstanceChangeArgs(uint Zone, ulong Key, InstanceWithMesh? Instance);

    public unsafe LayoutObjectSet()
    {
        Service.ClientState.ZoneInit += OnZoneInit;

        var bgVt = (BgPartsLayoutInstance.BgPartsLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? 90 48 89 50 F8 ");
        var boxVt = (CollisionBoxLayoutInstance.CollisionBoxLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 85 FF 74 20 48 8B CF E8 ?? ?? ?? ?? 4C 8B F0 48 8D 55 C7 45 33 C0 49 8B CE 48 8B 00 FF 50 08 E9 ?? ?? ?? ?? 66 44 39 73 ?? 75 45 44 38 73 1F 74 0A 48 8B 43 08 83 78 1C 06 75 35 48 8B 4B 08 4C 39 B1 ?? ?? ?? ?? 74 28 85 F6 75 24 45 84 FF 74 1F B2 3A ", 0x17);

        _bgCreate = new((nint)bgVt->CreatePrimary, BgCreateDetour, false);
        _bgDestroy = new((nint)bgVt->DestroyPrimary, BgDestroyDetour, false);
        _bgToggle = new((nint)bgVt->SetActive, BgToggleDetour, false);
        _bgTransF = new((nint)bgVt->SetTransformImpl, BgTransFDetour, false);
        _bgTrans = new((nint)bgVt->SetTranslationImpl, BgTransDetour, false);
        _bgRot = new((nint)bgVt->SetRotationImpl, BgRotDetour, false);
        _bgScale = new((nint)bgVt->SetScaleImpl, BgScaleDetour, false);

        _boxCreate = new((nint)boxVt->CreatePrimary, BoxCreateDetour, false);
        _boxDestroy = new((nint)boxVt->DestroyPrimary, BoxDestroyDetour, false);
        _boxToggle = new((nint)boxVt->SetActive, BoxToggleDetour, false);
        _boxTrans = new((nint)boxVt->SetTranslationImpl, BoxTransDetour, false);
        _boxRot = new((nint)boxVt->SetRotationImpl, BoxRotDetour, false);
        _boxScale = new((nint)boxVt->SetScaleImpl, BoxScaleDetour, false);
        _boxColl = new((nint)boxVt->SetColliderActive, BoxCollDetour, false);

        _sgInit = new(SharedGroupLayoutInstance.Addresses.InitTimelines, SgInitDetour, false);
    }

    public unsafe void Initialize()
    {
        InitZone(Service.ClientState.TerritoryType);

        var world = LayoutWorld.Instance();
        ActivateExistingLayout(world->GlobalLayout);
        ActivateExistingLayout(world->ActiveLayout);

        _bgCreate.Enabled = true;
        _bgDestroy.Enabled = true;
        _bgToggle.Enabled = true;
        _bgTransF.Enabled = true;
        _bgTrans.Enabled = true;
        _bgRot.Enabled = true;
        _bgScale.Enabled = true;

        _boxCreate.Enabled = true;
        _boxDestroy.Enabled = true;
        _boxToggle.Enabled = true;
        _boxTrans.Enabled = true;
        _boxRot.Enabled = true;
        _boxScale.Enabled = true;
        _boxColl.Enabled = true;

        _sgInit.Enabled = true;

        ZoneChanged.Invoke(this);
    }

    public override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Service.ClientState.ZoneInit -= OnZoneInit;
            _bgCreate.Dispose();
            _bgDestroy.Dispose();
            _bgToggle.Dispose();
            _bgTransF.Dispose();
            _bgTrans.Dispose();
            _bgRot.Dispose();
            _bgScale.Dispose();

            _boxCreate.Dispose();
            _boxDestroy.Dispose();
            _boxToggle.Dispose();
            _boxTrans.Dispose();
            _boxRot.Dispose();
            _boxScale.Dispose();
            _boxColl.Dispose();

            _sgInit.Dispose();
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
        var isTrace = TRACE_OBJECT.IsSet && pointer != null && pointer.Value->Id.InstanceKey == TRACE_OBJECT.InstanceKey && pointer.Value->SubId == TRACE_OBJECT.SubId;

        // this layout instance doesn't have a collider associated with it for some reason (i.e. broken crc)
        if (obj.Instance == null)
        {
            if (isTrace)
                Service.Log.Verbose("skipping traced object, it has no instance");
            return;
        }

        var modified = obj.Dirty;
        obj.Dirty = false;
        var k = obj.Instance.Instance.Id;
        if (modified && isTrace)
            Service.Log.Verbose($"object {k:X16} was dirty");

        // material flags can get modified "at random" by other sources
        var matPrev = obj.Instance.Instance.ForceSetPrimFlags;
        var matCur = GetMaterialFlags(pointer.Value);
        if (matPrev != matCur)
        {
            if (isTrace)
                Service.Log.Verbose($"object {k:X16} material changed from {matPrev} to {matCur}");
            obj.Instance.Instance.ForceSetPrimFlags = matCur;
            modified = true;
        }

        if (modified)
        {
            TraceObject(pointer.Value, "trigger-modify " + obj.Transform.Display());
            NotifyObject(obj.Instance, obj.Enabled && obj.ColliderEnabled);
        }
    }

    private void NotifyObject(InstanceWithMesh inst, bool enabled)
    {
        enabled &= !inst.Instance.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.Transparent);

        Notify(new(LastLoadedZone, inst.Instance.Id, enabled ? inst : null));
    }

    private unsafe SceneExtractor.PrimitiveFlags GetMaterialFlags(ILayoutInstance* val)
    {
        switch (val->Id.Type)
        {
            case InstanceType.BgPart:
                var v = (BgPartsLayoutInstance*)val;
                var id = (ulong)v->CollisionMaterialIdHigh << 32 | v->CollisionMaterialIdLow;
                var mask = (ulong)v->CollisionMaterialMaskHigh << 32 | v->CollisionMaterialMaskLow;
                // note about collision mask: steel doors in front of amal'jaa encampment (in southern thanalan) consist of two objects: a large box collider covering the entire door, and a small box collider with material 206400 but no mask at all
                // the smaller collider moves up when the player approaches, and i have no idea why it's missing a mask
                // TODO: to ensure correctness, could we instead skip objects that are inside a DoorRange?
                return SceneExtractor.ExtractMaterialFlags(mask > 0 ? id & mask : id);

            case InstanceType.CollisionBox:
                var c = (CollisionBoxLayoutInstance*)val;
                var id2 = (ulong)c->MaterialIdHigh << 32 | c->MaterialIdLow;
                var mask2 = (ulong)c->MaterialMaskHigh << 32 | c->MaterialMaskLow; ;
                return SceneExtractor.ExtractMaterialFlags(mask2 > 0 ? id2 & mask2 : id2);
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
        _transformOverride.Clear();
    }

    private unsafe void ActivateExistingLayout(LayoutManager* layout)
    {
        if (layout == null)
            return;

        var bgParts = layout->InstancesByType.FindPtr(InstanceType.BgPart);
        if (bgParts != null)
            foreach (var (k, v) in *bgParts)
                CreateObject((BgPartsLayoutInstance*)v.Value, "init");

        var boxes = layout->InstancesByType.FindPtr(InstanceType.CollisionBox);
        if (boxes != null)
            foreach (var (k, v) in *boxes)
                CreateObject((CollisionBoxLayoutInstance*)v.Value, "init");

        var sgs = layout->InstancesByType.FindPtr(InstanceType.SharedGroup);
        if (sgs != null)
            foreach (var (k, v) in *sgs)
                DetectAnimations((SharedGroupLayoutInstance*)v.Value);
    }

    private unsafe void BgCreateDetour(BgPartsLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _bgCreate.Original(thisPtr, transform, pathOrType);
        CreateObject(thisPtr, "primary");
    }

    private unsafe void BgDestroyDetour(BgPartsLayoutInstance* thisPtr)
    {
        _bgDestroy.Original(thisPtr);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
            Service.Log.Verbose($"DestroyPrimary(bgpart) called on {k:X}");
            obj.Destroyed = true;
        }
    }

    private unsafe void BgToggleDetour(BgPartsLayoutInstance* thisPtr, bool active)
    {
        _bgToggle.Original(thisPtr, active);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            if (obj.Enabled != active)
            {
                obj.Enabled = active;
                obj.Dirty = true;
            }
        }
    }

    private unsafe void BgTransFDetour(BgPartsLayoutInstance* thisPtr, Transform* t)
    {
        _bgTransF.Original(thisPtr, t);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            var cur = inst.Transform;
            if (t->Translation != cur.Translation || t->Rotation != cur.Rotation || t->Scale != cur.Scale)
            {
                TraceObject(&thisPtr->ILayoutInstance, "transform(bg)");
                inst.Recreate(&thisPtr->ILayoutInstance, cur);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BgTransDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgTrans.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Translation)
            {
                TraceObject(&thisPtr->ILayoutInstance, "translation(bg)");
                var tnew = inst.Transform with { Translation = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BgRotDetour(BgPartsLayoutInstance* thisPtr, Quaternion* value)
    {
        _bgRot.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Rotation)
            {
                TraceObject(&thisPtr->ILayoutInstance, "rotation(bg)");
                var tnew = inst.Transform with { Rotation = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BgScaleDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgScale.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Scale)
            {
                TraceObject(&thisPtr->ILayoutInstance, "scale(bg)");
                var tnew = inst.Transform with { Scale = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BoxCreateDetour(CollisionBoxLayoutInstance* thisPtr, Transform* transform, void* pathOrType)
    {
        _boxCreate.Original(thisPtr, transform, pathOrType);
        CreateObject(thisPtr, "primary");
    }

    private unsafe void BoxDestroyDetour(TriggerBoxLayoutInstance* thisPtr)
    {
        _boxDestroy.Original(thisPtr);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            Service.Log.Verbose($"DestroyPrimary(box) called on {obj.Instance?.Instance.Id ?? 0:X}");
            obj.Destroyed = true;
        }
    }

    private unsafe void BoxToggleDetour(TriggerBoxLayoutInstance* thisPtr, bool active)
    {
        _boxToggle.Original(thisPtr, active);
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            if (obj.Enabled != active)
            {
                obj.Enabled = active;
                obj.Dirty = true;
            }
        }
    }

    private unsafe void BoxTransDetour(TriggerBoxLayoutInstance* thisPtr, Vector3* value)
    {
        _boxTrans.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Translation)
            {
                TraceObject(&thisPtr->ILayoutInstance, "translation(box)");
                var tnew = inst.Transform with { Translation = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BoxRotDetour(TriggerBoxLayoutInstance* thisPtr, Quaternion* value)
    {
        _boxRot.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Rotation)
            {
                TraceObject(&thisPtr->ILayoutInstance, "rotation(box)");
                var tnew = inst.Transform with { Rotation = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BoxScaleDetour(TriggerBoxLayoutInstance* thisPtr, Vector3* value)
    {
        _boxScale.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (*value != inst.Transform.Scale)
            {
                TraceObject(&thisPtr->ILayoutInstance, "scale(box)");
                var tnew = inst.Transform with { Scale = *value };
                inst.Recreate(&thisPtr->ILayoutInstance, tnew);
                inst.Dirty = true;
            }
        }
    }

    private unsafe void BoxCollDetour(TriggerBoxLayoutInstance* thisPtr, bool active)
    {
        _boxColl.Original(thisPtr, active);

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (inst.ColliderEnabled != active)
            {
                inst.ColliderEnabled = active;
                inst.Dirty = true;
            }
        }
    }

    private unsafe void SgInitDetour(SharedGroupLayoutInstance* thisPtr, void* data)
    {
        _sgInit.Original(thisPtr, data);
        if (thisPtr != null)
        {
            Service.Log.Verbose($"[SgInit] DetectAnimations");
            DetectAnimations(thisPtr);
        }
    }

    private unsafe void DetectAnimations(SharedGroupLayoutInstance* thisPtr)
    {
        var thisGrp = SceneTool.GetKey(&thisPtr->ILayoutInstance);

        string? animationSrc = null;
        if (thisPtr->ActionController1 != null)
            animationSrc = "motion1";
        else if (thisPtr->ActionController1 != null)
            animationSrc = "motion2";

        Service.Log.Verbose($"[SgInit] {thisGrp:X} number of instances in timeline: {thisPtr->TimeLineContainer.Instances.Count}");
        foreach (var inst in thisPtr->TimeLineContainer.Instances)
        {
            Service.Log.Verbose($"[SgInit] {thisGrp:X} flags auto={inst.Value->DataPtr->AutoPlay} loop={inst.Value->DataPtr->Loop}");
            if (inst.Value->DataPtr->Loop == 1)
            {
                animationSrc = "timeline-loop";
                break;
            }
        }

        if (animationSrc == null)
        {
            Service.Log.Verbose($"[SgInit] {thisGrp:X} no motion and nothing interesting in timeline");
            return;
        }

        foreach (var inst in thisPtr->Instances.Instances)
        {
            switch (inst.Value->Instance->Id.Type)
            {
                // SharedGroup children are skipped since they will be iterated over separately
                case InstanceType.BgPart:
                case InstanceType.CollisionBox:
                    var key = SceneTool.GetKey(inst.Value->Instance);
                    Service.Log.Verbose($"[SgInit] ignoring {inst.Value->Instance->Id.Type} {key:X}, which has animation type={animationSrc}");
                    var t = *thisPtr->GetTransformImpl();
                    _transformOverride[key] = t;
                    if (_objects.TryGetValue(inst.Value->Instance, out var m))
                    {
                        Service.Log.Verbose($"correcting transform of {key:X} to {t.Display()}");
                        m.Recreate(inst.Value->Instance, t);
                        m.Dirty = true;
                    }
                    break;
            }
        }
    }

    private unsafe void CreateObject(BgPartsLayoutInstance* thisPtr, string source)
    {
        var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
        if (!thisPtr->HasCollision())
        {
            Service.Log.Verbose($"create(bgpart): skip (no collision): {k:X16}");
            return;
        }

        if (!_transformOverride.TryGetValue(k, out var trans))
            trans = *(Transform*)&thisPtr->GraphicsObject->Position;

        Service.Log.Verbose($"create(bgpart): source={source}: {k:X16}; enabled={thisPtr->HasEnabledFlag()}");
        _objects[&thisPtr->ILayoutInstance] = new()
        {
            Instance = SceneTool.Get().CreateInstance(thisPtr, trans),
            Enabled = thisPtr->HasEnabledFlag(),
            IsBgPart = true,
            Transform = trans,
            Dirty = true
        };
    }

    private unsafe void CreateObject(CollisionBoxLayoutInstance* thisPtr, string source)
    {
        var k = SceneTool.GetKey(&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance);
        Service.Log.Verbose($"create(box): source={source}: {k:X16}; enabled={thisPtr->HasEnabledFlag()}");

        if (!_transformOverride.TryGetValue(k, out var trans))
            trans = thisPtr->Transform;

        _objects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance] = new()
        {
            Instance = SceneTool.Get().CreateInstance(thisPtr, trans),
            Enabled = thisPtr->HasEnabledFlag(),
            IsBgPart = false,
            Transform = trans,
            ColliderEnabled = thisPtr->IsColliderActive(),
            Dirty = true
        };
    }

    private unsafe void TraceObject(ILayoutInstance* inst, string label)
    {
        if (TRACE_OBJECT.IsAll || inst->Id.InstanceKey == TRACE_OBJECT.InstanceKey && inst->SubId == TRACE_OBJECT.SubId)
        {
            Service.Log.Verbose($"traced object modification: {inst->Id.InstanceKey:X8}.{inst->SubId:X8} {label}");
        }
    }
}

// sorts and groups change events from LayoutObjectSet
public sealed class TileSet : Subscribable<TileSet.TileChangeArgs>
{
    public record struct TileChangeArgs(uint Zone, int X, int Z, SortedDictionary<ulong, InstanceWithMesh> Objects);

    private readonly SortedDictionary<ulong, InstanceWithMesh>[,] _tiles = new SortedDictionary<ulong, InstanceWithMesh>[16, 16];

    private readonly Lock _lock = new();

    private NavmeshCustomization Customization = new();

    public IObservable<IList<TileChangeArgs>> Debounced(TimeSpan delay, TimeSpan timeout) => this
        .Window(() => this.Throttle(delay)/*.Timeout(timeout)*/)
        .SelectMany(result => result.Distinct(t => (t.X, t.Z)).ToList())
        .Catch<IList<TileChangeArgs>, TimeoutException>(ex =>
        {
            Service.Log.Warning(ex, $"Waited too long for scene modifications to finish. Last event was: {LastEvent}");
            return Observable.Empty<IList<TileChangeArgs>>();
        });

    public void Watch(LayoutObjectSet set, CancellationToken token)
    {
        set.ForEachAsync(Apply, token);
        set.ZoneChanged += Clear;
    }

    private const int TileUnits = 128;
    private const int RowLength = 16;

    public LayoutObjectSet.InstanceChangeArgs? LastEvent;

    private void Apply(LayoutObjectSet.InstanceChangeArgs change)
    {
        HashSet<(int, int)> modified = [];

        lock (_lock)
        {
            LastEvent = change;

            //Service.Log.Verbose($"applying change {change.Key:X}");
            // remove all existing copies of instance, in case previous transform occupied different set of tiles
            modified.UnionWith(Remove(change.Key));

            if (change.Instance != null && Customization.FilterObject(change.Instance))
            {
                var min = change.Instance.Instance.WorldBounds.Min - new Vector3(-1024);
                var max = change.Instance.Instance.WorldBounds.Max - new Vector3(-1024);

                var imin = (int)min.X / TileUnits;
                var imax = (int)max.X / TileUnits;
                var jmin = (int)min.Z / TileUnits;
                var jmax = (int)max.Z / TileUnits;

                for (var i = Math.Max(0, imin); i <= Math.Min(imax, RowLength - 1); i++)
                    for (var j = Math.Max(0, jmin); j <= Math.Min(jmax, RowLength - 1); j++)
                    {
                        _tiles[i, j] ??= [];
                        _tiles[i, j][change.Instance.Instance.Id] = change.Instance;
                        modified.Add((i, j));
                    }
            }
        }

        foreach (var (x, z) in modified)
            Notify(new(change.Zone, x, z, _tiles[x, z]));
    }

    private HashSet<(int, int)> Remove(ulong key)
    {
        var changes = new HashSet<(int, int)>();

        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (_tiles[i, j]?.Remove(key) == true)
                    changes.Add((i, j));
        return changes;
    }

    private void Clear(LayoutObjectSet cs)
    {
        lock (_lock)
        {
            Customization = NavmeshCustomizationRegistry.ForTerritory(cs.LastLoadedZone);
            foreach (var t in _tiles)
                t?.Clear();
        }
    }
}
