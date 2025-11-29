using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public abstract class Subscribable<TVal> : IObservable<TVal>, IDisposable
{
    protected readonly List<Subscription<TVal>> _subscribers = [];

    public IDisposable Subscribe(IObserver<TVal> observer)
    {
        var sub = new Subscription<TVal>(this, observer);
        var cnt = _subscribers.Count;
        _subscribers.Add(sub);
        if (cnt == 0)
        {
            Service.Log.Verbose($"{GetType()}: OnSubscribeFirst");
            OnSubscribeFirst();
        }
        return sub;
    }

    public void Unsubscribe(Subscription<TVal> subscriber)
    {
        _subscribers.Remove(subscriber);
        if (_subscribers.Count == 0)
        {
            Service.Log.Verbose($"{GetType()}: OnUnsubscribeAll");
            OnUnsubscribeAll();
        }
    }

    protected virtual void OnSubscribeFirst() { }
    protected virtual void OnUnsubscribeAll() { }

    protected void Notify(TVal val)
    {
        foreach (var s in _subscribers.ToList())
            s.Observer.OnNext(val);
    }

    protected void NotifyError(Exception e)
    {
        foreach (var s in _subscribers.ToList())
            s.Observer.OnError(e);
    }

    public virtual void Dispose(bool disposing) { }
    public void Dispose()
    {
        _subscribers.Clear();
        OnUnsubscribeAll();
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
        //public bool Dirty { get; set; }

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

        public static readonly TraceObjectKey ALL = new() { Full = ulong.MaxValue };
        public static readonly TraceObjectKey NONE = new() { Full = 0 };

        // random boat in Western Thanalan with oscillating movement; member of SharedGroup that has a timeline with autoplay and loop enabled
        public static readonly TraceObjectKey BOAT = new() { Full = 0x003BDC3D_03000000 };

        // floating aetheryte-like crystals in Bestways Burrow which have collision; movement is controlled by SGActionController
        public static readonly TraceObjectKey RABBIT_AETHERYTE = new() { Full = 0x00885333_0401000 };

        // example of a SharedGroup that has multiple animation timelines attached to it (easily visible and has a long duration, so helps with debugging)
        public static readonly TraceObjectKey ARF_ELEVATOR = new() { Full = 0x0058BE03_3A250000 };

        // out of bounds background object in Coerthas Central Highlands near the Xelphatol entrance; has collision but some timeline thing adds the 0x400 flag to it shortly after it's created (example of SetProperties/CreatePrimary not being a reliable source of truth)
        public static readonly TraceObjectKey IXAL_BLIMP = new() { Full = 0x0031D5D9_0B000000 };

        // part of the Camp Dragonhead castle thing that is placed exactly at whole-number coordinates, but the imprecision incurred by recalculating the rotation quaternion during setup causes its world bounds to expand by 1 unit on the Z axis
        public static readonly TraceObjectKey DRAGONHEAD_WALL = new() { Full = 0x003F8983_0B000000 };
    }

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _objects = [];
    private readonly ConcurrentDictionary<ulong, Transform> _transformOverride = [];
    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _dirtyObjects = [];

    private readonly HookAddress<BgPartsLayoutInstance.Delegates.CreatePrimary> _bgCreate;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetProperties> _bgProps;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.DestroyPrimary> _bgDestroy;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetActive> _bgToggle;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetTransformImpl> _bgTransF;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetTranslationImpl> _bgTrans;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetRotationImpl> _bgRot;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.SetScaleImpl> _bgScale;
    private readonly HookAddress<BgPartsLayoutInstance.Delegates.GetCollider2> _bgCollHack;

    private readonly HookAddress<CollisionBoxLayoutInstance.Delegates.CreatePrimary> _boxCreate;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.DestroyPrimary> _boxDestroy;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetActive> _boxToggle;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetTranslationImpl> _boxTrans;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetRotationImpl> _boxRot;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetScaleImpl> _boxScale;
    private readonly HookAddress<TriggerBoxLayoutInstance.Delegates.SetColliderActive> _boxColl;

    private readonly HookAddress<SharedGroupLayoutInstance.Delegates.InitTimelines> _sgInit;

    private bool _watchColliders;

    private static readonly TraceObjectKey TRACE_OBJECT = TraceObjectKey.NONE;

    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<LayoutObjectSet> TerritoryChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedTerritory;

    public readonly record struct InstanceChangeArgs(uint Zone, ulong Key, InstanceWithMesh? Instance);

    public unsafe LayoutObjectSet()
    {
        Service.ClientState.TerritoryChanged += OnTerritoryChanged;

        var bgVt = (BgPartsLayoutInstance.BgPartsLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? 90 48 89 50 F8 ");
        var boxVt = (CollisionBoxLayoutInstance.CollisionBoxLayoutInstanceVirtualTable*)Service.SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 85 FF 74 20 48 8B CF E8 ?? ?? ?? ?? 4C 8B F0 48 8D 55 C7 45 33 C0 49 8B CE 48 8B 00 FF 50 08 E9 ?? ?? ?? ?? 66 44 39 73 ?? 75 45 44 38 73 1F 74 0A 48 8B 43 08 83 78 1C 06 75 35 48 8B 4B 08 4C 39 B1 ?? ?? ?? ?? 74 28 85 F6 75 24 45 84 FF 74 1F B2 3A ", 0x17);

        _bgCreate = new((nint)bgVt->CreatePrimary, BgCreateDetour, false);
        _bgProps = new((nint)bgVt->SetProperties, BgPropsDetour, false);
        _bgDestroy = new((nint)bgVt->DestroyPrimary, BgDestroyDetour, false);
        _bgToggle = new((nint)bgVt->SetActive, BgToggleDetour, false);
        _bgTransF = new((nint)bgVt->SetTransformImpl, BgTransFDetour, false);
        _bgTrans = new((nint)bgVt->SetTranslationImpl, BgTransDetour, false);
        _bgRot = new((nint)bgVt->SetRotationImpl, BgRotDetour, false);
        _bgScale = new((nint)bgVt->SetScaleImpl, BgScaleDetour, false);
        _bgCollHack = new((nint)bgVt->GetCollider2, BgCollHackDetour, false);

        _boxCreate = new((nint)boxVt->CreatePrimary, BoxCreateDetour, false);
        _boxDestroy = new((nint)boxVt->DestroyPrimary, BoxDestroyDetour, false);
        _boxToggle = new((nint)boxVt->SetActive, BoxToggleDetour, false);
        _boxTrans = new((nint)boxVt->SetTranslationImpl, BoxTransDetour, false);
        _boxRot = new((nint)boxVt->SetRotationImpl, BoxRotDetour, false);
        _boxScale = new((nint)boxVt->SetScaleImpl, BoxScaleDetour, false);
        _boxColl = new((nint)boxVt->SetColliderActive, BoxCollDetour, false);

        _sgInit = new(SharedGroupLayoutInstance.Addresses.InitTimelines, SgInitDetour, false);
    }

    private CancellationTokenSource? _initTaskSrc;
    private Task? _initTask;

    protected override void OnSubscribeFirst()
    {
        if (_initTask != null)
            throw new InvalidOperationException("_initTask has already been set even though we had no subscribers, this is a bug");

        var src = _initTaskSrc = new();
        _initTask = Service.Framework.Run(() =>
        {
            Initialize();
            _initTask = null;
            Service.Framework.Update += Tick;
        }, src.Token);
    }

    protected override void OnUnsubscribeAll()
    {
        Service.Framework.Update -= Tick;

        if (_initTaskSrc != null)
        {
            _initTaskSrc.Cancel();
            _initTaskSrc = null;
            _initTask = null;
        }

        _bgCreate.Enabled = false;
        _bgProps.Enabled = false;
        _bgDestroy.Enabled = false;
        _bgToggle.Enabled = false;
        _bgTransF.Enabled = false;
        _bgTrans.Enabled = false;
        _bgRot.Enabled = false;
        _bgScale.Enabled = false;
        _bgCollHack.Enabled = false;

        _boxCreate.Enabled = false;
        _boxDestroy.Enabled = false;
        _boxToggle.Enabled = false;
        _boxTrans.Enabled = false;
        _boxRot.Enabled = false;
        _boxScale.Enabled = false;
        _boxColl.Enabled = false;

        _sgInit.Enabled = false;
    }

    private unsafe void Initialize()
    {
        InitTerritory(Service.ClientState.TerritoryType);

        var world = LayoutWorld.Instance();
        Time("global", () => ActivateExistingLayout(world->GlobalLayout));
        Time("active", () => ActivateExistingLayout(world->ActiveLayout));

        Time("hooks", () =>
        {
            _bgCreate.Enabled = true;
            _bgProps.Enabled = true;
            _bgDestroy.Enabled = true;
            _bgToggle.Enabled = true;
            _bgTransF.Enabled = true;
            _bgTrans.Enabled = true;
            _bgRot.Enabled = true;
            _bgScale.Enabled = true;
            _bgCollHack.Enabled = true;

            _boxCreate.Enabled = true;
            _boxDestroy.Enabled = true;
            _boxToggle.Enabled = true;
            _boxTrans.Enabled = true;
            _boxRot.Enabled = true;
            _boxScale.Enabled = true;
            _boxColl.Enabled = true;

            _sgInit.Enabled = true;
        });

        Time("ZoneChange", () =>
        {
            TerritoryChanged.Invoke(this);
        });
    }

    private static void Time(string label, Action t)
    {
        var s = Stopwatch.StartNew();
        t();
        s.Stop();
        Service.Log.Debug($"[LayoutObjectSet] [init] {label} took {s.ElapsedMilliseconds:f3}ms");
    }

    public override void Dispose(bool disposing)
    {
        Service.ClientState.TerritoryChanged -= OnTerritoryChanged;

        _bgCreate.Dispose();
        _bgProps.Dispose();
        _bgDestroy.Dispose();
        _bgToggle.Dispose();
        _bgTransF.Dispose();
        _bgTrans.Dispose();
        _bgRot.Dispose();
        _bgScale.Dispose();
        _bgCollHack.Dispose();

        _boxCreate.Dispose();
        _boxDestroy.Dispose();
        _boxToggle.Dispose();
        _boxTrans.Dispose();
        _boxRot.Dispose();
        _boxScale.Dispose();
        _boxColl.Dispose();

        _sgInit.Dispose();
    }

    private unsafe void Tick(IFramework fwk)
    {
        if (_initTask != null)
        {
            Service.Log.Warning("not calling Tick() while init task is running");
            return;
        }

        if (Service.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51))
            return;

        foreach (var (ptr, obj) in _dirtyObjects)
        {
            if (obj.Destroyed)
            {
                if (_objects.Remove(ptr, out var destroyed) && destroyed.Instance is { } destroyedObject)
                    NotifyObject(destroyedObject, false);
            }
            else if (obj.Instance != null)
                NotifyObject(obj.Instance, obj.Enabled && obj.ColliderEnabled);
        }
        _dirtyObjects.Clear();
    }

    public DateTime LastUpdate { get; private set; }

    private void NotifyObject(InstanceWithMesh inst, bool enabled)
    {
        enabled &= !inst.Instance.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.Transparent);

        LastUpdate = DateTime.Now;
        Notify(new(LastLoadedTerritory, inst.Instance.Id, enabled ? inst : null));
    }

    private unsafe void OnTerritoryChanged(ushort terr)
    {
        InitTerritory(terr);
        TerritoryChanged.Invoke(this);
    }

    private unsafe void InitTerritory(ushort terr)
    {
        LastLoadedTerritory = terr;
        Customization = NavmeshCustomizationRegistry.ForTerritory(terr);
        // TODO: support different tile sizes, maybe?
        RowLength = 16; //  Customization.Settings.NumTiles[0];

        _objects.Clear();
        _dirtyObjects.Clear();
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

    private unsafe void BgPropsDetour(BgPartsLayoutInstance* thisPtr, FileLayerGroupInstance* data)
    {
        _bgProps.Original(thisPtr, data);
        CreateObject(thisPtr, "props");
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = obj;
            }
        }
    }

    // note that all BgParts transforms update the entire matrix
    // the methods other than SetTransformImpl still call SetTransform on the graphics object and collider, which can result in a SetTranslation call causing miniscule changes to the rotation quaternion, which can result in different world bounds (when truncated to int) on the same mesh
    private unsafe void BgTransFDetour(BgPartsLayoutInstance* thisPtr, Transform* t)
    {
        _bgTransF.Original(thisPtr, t);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgTransDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgTrans.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgRotDetour(BgPartsLayoutInstance* thisPtr, Quaternion* value)
    {
        _bgRot.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgScaleDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgScale.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
            return;

        UpdateBgTransform(thisPtr);
    }

    private unsafe void UpdateBgTransform(BgPartsLayoutInstance* thisPtr)
    {
        var tx = thisPtr->GetTransformImpl();

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (tx->Translation != inst.Transform.Translation || tx->Rotation != inst.Transform.Rotation || tx->Scale != inst.Transform.Scale)
            {
                inst.Recreate(&thisPtr->ILayoutInstance, *tx);
                if (inst.Instance != null)
                    TraceObject(&thisPtr->ILayoutInstance, $"transform(bg): {inst.Instance.Instance.WorldBounds.Display()}, stored={inst.Transform.Display()}");
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
            }
        }
    }

    // some scheduler thing sets material flags on some BgParts directly, which sucks because i really don't feel like reversing more of the scheduler
    // this function incidentally calls GetCollider2 to try to update the collider flags (though in this case, there is no attached collider, so hooking Collider::SetMaterial would do nothing)
    // GetCollider2 seems to only be called during zone transition/setup, as opposed to GetCollider, which is called dozens of times per frame during player movement thanks to streaming manager
    private unsafe Collider* BgCollHackDetour(BgPartsLayoutInstance* thisPtr)
    {
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst) && inst.Instance != null)
        {
            var oldFlags = inst.Instance.Instance.ForceSetPrimFlags;
            var newFlags = SceneExtractor.ExtractMaterialFlags(thisPtr->CollisionMaterialIdHigh << 32 | thisPtr->CollisionMaterialIdLow);
            if (oldFlags != newFlags)
            {
                TraceObject(&thisPtr->ILayoutInstance, "coll2(material)");
                inst.Instance.Instance.ForceSetPrimFlags = newFlags;
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
            }
        }

        return _bgCollHack.Original(thisPtr);
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = obj;
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
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
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
            }
        }
    }

    private unsafe void SgInitDetour(SharedGroupLayoutInstance* thisPtr, void* data)
    {
        _sgInit.Original(thisPtr, data);
        if (thisPtr != null)
            DetectAnimations(thisPtr);
    }

    private unsafe void DetectAnimations(SharedGroupLayoutInstance* thisPtr)
    {
        var thisGrp = SceneTool.GetKey(&thisPtr->ILayoutInstance);

        string? animationSrc = thisPtr->ActionController1 != null ? "motion1"
            : thisPtr->ActionController2 != null ? "motion2"
            : thisPtr->TimeLineContainer.Instances.Any(i => i.Value->DataPtr->Loop == 1) ? "loop"
            : null;

        if (animationSrc != null)
            MarkIgnored(thisPtr, *thisPtr->GetTransformImpl(), animationSrc);
    }

    private unsafe void MarkIgnored(SharedGroupLayoutInstance* thisPtr, Transform t, string animType)
    {
        var thisKey = SceneTool.GetKey(&thisPtr->ILayoutInstance);
        if (_transformOverride.ContainsKey(thisKey))
        {
            Service.Log.Verbose($"Group {thisKey:X} is already ignored, doing nothing");
            return;
        }

        foreach (var inst in thisPtr->Instances.Instances)
        {
            switch (inst.Value->Instance->Id.Type)
            {
                case InstanceType.BgPart:
                case InstanceType.CollisionBox:
                    var key = SceneTool.GetKey(inst.Value->Instance);
                    Service.Log.Verbose($"[SgInit] ignoring {inst.Value->Instance->Id.Type} {key:X}, which has animation type={animType}");
                    _transformOverride[key] = t;
                    if (_objects.TryGetValue(inst.Value->Instance, out var m))
                    {
                        Service.Log.Verbose($"correcting transform of {key:X} to {t.Display()}");
                        m.Recreate(inst.Value->Instance, t);
                        _dirtyObjects[inst.Value->Instance] = m;
                    }
                    break;
                case InstanceType.SharedGroup:
                    MarkIgnored((SharedGroupLayoutInstance*)inst.Value->Instance, t, animType);
                    var key2 = SceneTool.GetKey(inst.Value->Instance);
                    _transformOverride[key2] = t;
                    break;
            }
        }
    }

    private unsafe void CreateObject(BgPartsLayoutInstance* thisPtr, string source)
    {
        var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);
        if (!thisPtr->HasCollision())
        {
            //Service.Log.Verbose($"create(bgpart): skip (no collision): {k:X16}");
            return;
        }

        if (!_transformOverride.TryGetValue(k, out var trans))
            trans = *thisPtr->GetTransformImpl();

        //Service.Log.Verbose($"create(bgpart): source={source}: {k:X16}; enabled={thisPtr->HasEnabledFlag()}, mat={(ulong)thisPtr->CollisionMaterialIdHigh << 32 | thisPtr->CollisionMaterialIdLow:X}");
        var obj = new MaybeEnabled()
        {
            Instance = SceneTool.Get().CreateInstance(thisPtr, trans),
            Enabled = thisPtr->HasEnabledFlag(),
            IsBgPart = true,
            Transform = trans,
        };
        TraceObject(&thisPtr->ILayoutInstance, $"create({source}): {obj.Instance!.Instance.WorldBounds.Display()}, stored={obj.Transform.Display()}");
        _dirtyObjects[&thisPtr->ILayoutInstance] = _objects[&thisPtr->ILayoutInstance] = obj;
    }

    private unsafe void CreateObject(CollisionBoxLayoutInstance* thisPtr, string source)
    {
        var k = SceneTool.GetKey(&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance);
        Service.Log.Verbose($"create(box): source={source}: {k:X16}; enabled={thisPtr->HasEnabledFlag()}");

        if (!_transformOverride.TryGetValue(k, out var trans))
            trans = thisPtr->Transform;

        var obj = new MaybeEnabled()
        {
            Instance = SceneTool.Get().CreateInstance(thisPtr, trans),
            Enabled = thisPtr->HasEnabledFlag(),
            IsBgPart = false,
            Transform = trans,
            ColliderEnabled = thisPtr->IsColliderActive(),
        };
        _dirtyObjects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance]
            = _objects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance]
            = obj;
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
    public record struct TileChangeArgs(uint Territory, int X, int Z, SortedDictionary<ulong, InstanceWithMesh> Objects);

    private readonly SortedDictionary<ulong, InstanceWithMesh>[,] _tiles = new SortedDictionary<ulong, InstanceWithMesh>[16, 16];

    private readonly Lock _lock = new();

    private NavmeshCustomization Customization = new();

    public IObservable<IList<TileChangeArgs>> Debounced(TimeSpan delay, TimeSpan timeout) => this
        .GroupByUntil(_ => 1, g => g.Throttle(delay).Timeout(timeout))
        .SelectMany(x => x.ToList())
        .Select(result => result.DistinctBy(t => (t.X, t.Z)).ToList());

    private IDisposable? _sceneSubscription;

    protected override void OnUnsubscribeAll()
    {
        _sceneSubscription?.Dispose();
    }

    public void Watch(LayoutObjectSet set)
    {
        _sceneSubscription = set.Subscribe(Apply);
        set.TerritoryChanged += Clear;
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

                if (imax < 0 || jmax < 0 || imin > 15 || jmin > 15)
                {
                    //Service.Log.Warning($"object {change.Instance} is outside world bounds, which will break the cache");
                    return;
                }

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
            Customization = NavmeshCustomizationRegistry.ForTerritory(cs.LastLoadedTerritory);
            foreach (var t in _tiles)
                t?.Clear();
        }
    }

    public void Reload(uint zone, int x, int z)
    {
        Notify(new(zone, x, z, _tiles[x, z]));
    }
}
