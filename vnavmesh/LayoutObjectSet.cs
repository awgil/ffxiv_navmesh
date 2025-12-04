using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// tracks "enabled" flag, material flags, and world transforms of all objects in the current zone that have an attached collider
public sealed unsafe partial class LayoutObjectSet : Subscribable<LayoutObjectSet.InstanceChangeArgs>
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

    // random boat in Western Thanalan with oscillating movement; member of SharedGroup that has a timeline with autoplay and loop enabled
    private const ulong BOAT = 0x003BDC3D_03000000;

    // floating aetheryte-like crystals in Bestways Burrow which have collision; movement is controlled by SGActionController
    private const ulong RABBIT_AETHERYTE = 0x00885333_0401000;

    // example of a SharedGroup that has multiple animation timelines attached to it (easily visible and has a long duration, so helps with debugging)
    private const ulong ARF_ELEVATOR = 0x0058BE03_3A250000;

    // out of bounds background object in Coerthas Central Highlands near the Xelphatol entrance; has collision but some timeline thing adds the 0x400 flag to it shortly after it's created (example of SetProperties/CreatePrimary not being a reliable source of truth)
    private const ulong IXAL_BLIMP = 0x0031D5D9_0B000000;

    // part of the Camp Dragonhead castle thing that is placed exactly at whole-number coordinates, but the imprecision incurred by recalculating the rotation quaternion during setup causes its world bounds to expand by 1 unit on the Z axis
    private const ulong DRAGONHEAD_WALL = 0x003F8983_0B000000;

    private const ulong HERITAGE_POLE = 0x00A4DD65_01090100;

    // in solution 9
    // - 0x00A1DECE_00000000: toplevel prefab with action controller in slot 1
    //   - 0x00A1DECE_09000000: regular bgpart, no movement
    //   - 0x00A1DECE_0B000000: nested prefab, no action controller; transform is controlled by parent
    //     - 0x00A1DECE_0B040000: nested bgpart, transform is calculated relative to parent
    private const ulong S9_CAFE = 0x00A1DECE_00000000;

    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _objects = [];
    private readonly ConcurrentDictionary<ulong, Transform> _transformOverride = [];
    private readonly ConcurrentDictionary<ulong, bool> _hiddenObjects = [];
    private readonly ConcurrentDictionary<Pointer<ILayoutInstance>, MaybeEnabled> _dirtyObjects = [];

    // TODO: replace vtables instead
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

    private delegate void SGLoadUnknown(SharedGroupLayoutInstance* thisPtr);
    private readonly HookAddress<SGLoadUnknown> _sgLoad;

    public DateTime LastUpdate { get; private set; }
    public NavmeshCustomization Customization { get; private set; } = new();

    public Action<LayoutObjectSet> ZoneChanged = delegate { };

    public int RowLength { get; private set; } = 0;
    public int NumTiles => RowLength * RowLength;
    public int TileUnits => 2048 / RowLength;

    public uint LastLoadedZone;
    public uint LastCFC;

    public readonly record struct InstanceChangeArgs(uint Zone, ulong Key, InstanceWithMesh? Instance);

    public unsafe LayoutObjectSet()
    {
        // TODO: we have to use ZoneInit instead of TerritoryChanged because switching instances doesn't trigger the latter (for obvious reasons)
        // but Hyperborea doesn't trigger ZoneInit, so we need another way
        Service.ClientState.ZoneInit += OnZoneInit;

        Service.Condition.ConditionChange += OnConditionChange;

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

        _sgLoad = new("40 55 57 41 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B F9", SgLoadDetour, false);
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

        _sgLoad.Enabled = false;
    }

    private unsafe void Initialize()
    {
        InitZone(Service.ClientState.TerritoryType, GameMain.Instance()->CurrentContentFinderConditionId);

        var world = LayoutWorld.Instance();
        Utils.Time("global", () => ActivateExistingLayout(world->GlobalLayout));
        Utils.Time("active", () => ActivateExistingLayout(world->ActiveLayout));

        Utils.Time("hooks", () =>
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

            _sgLoad.Enabled = true;
        });

        Utils.Time("ZoneChange", () =>
        {
            ZoneChanged.Invoke(this);
        });
    }

    public override void Dispose(bool disposing)
    {
        Service.ClientState.ZoneInit -= OnZoneInit;

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

        _sgLoad.Dispose();
    }

    private void OnZoneInit(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        var terr = (ushort)obj.TerritoryType.RowId;
        Slog.LogGeneric("territory-change", (int)LastLoadedZone, terr);
        InitZone(terr, GameMain.Instance()->CurrentContentFinderConditionId);
        ZoneChanged.Invoke(this);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        Slog.LogGeneric("condition", (int)flag, value ? 1 : 0, extra: flag.ToString());
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
                Slog.TraceDelete(ptr, LastLoadedZone, "is-destroyed");
                if (_objects.Remove(ptr, out var destroyed) && destroyed.Instance is { } destroyedObject)
                {
                    NotifyObject(destroyedObject, false);
                }
                else
                {
                    Slog.TraceDelete(ptr, LastLoadedZone, "destroyed-not-found");
                }
            }
            else if (obj.Instance != null)
            {
                var enabled = obj.Enabled && obj.ColliderEnabled;
                Trace(ptr, "notify", enabled ? "enabled" : "disabled");
                NotifyObject(obj.Instance, enabled);
            }
        }
        _dirtyObjects.Clear();
    }

    private void NotifyObject(InstanceWithMesh inst, bool enabled)
    {
        enabled &= !inst.Instance.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.Transparent);
        enabled &= !_hiddenObjects.ContainsKey(inst.Instance.Id);

        if (LastCFC > 0)
            enabled &= !inst.Instance.ForceSetPrimFlags.HasFlag(SceneExtractor.PrimitiveFlags.HasTemporaryFlag);

        LastUpdate = DateTime.Now;
        Notify(new(LastLoadedZone, inst.Instance.Id, enabled ? inst : null));
    }

    private unsafe void InitZone(ushort zone, ushort cfcid)
    {
        var prevZone = LastLoadedZone;
        LastLoadedZone = zone;
        LastCFC = cfcid;
        Customization = NavmeshCustomizationRegistry.ForTerritory(zone);
        // TODO: support different tile sizes
        RowLength = 16; //  Customization.Settings.NumTiles[0];

        foreach (var obj in _objects)
            Slog.TraceDelete(obj.Key, prevZone, "gc");
        _objects.Clear();
        foreach (var obj in _dirtyObjects)
            Slog.TraceDelete(obj.Key, prevZone, "gc-dirty");
        _dirtyObjects.Clear();
        _transformOverride.Clear();
        _hiddenObjects.Clear();
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
        Trace(thisPtr, "destroy");
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            Trace(thisPtr, "destroy-hit");
            obj.Destroyed = true;
            _dirtyObjects[&thisPtr->ILayoutInstance] = obj;
        }
        else
        {
            Trace(thisPtr, "destroy-miss");
        }
    }

    private unsafe void BgToggleDetour(BgPartsLayoutInstance* thisPtr, bool active)
    {
        _bgToggle.Original(thisPtr, active);
        Trace(thisPtr, $"toggle({active})");
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            if (obj.Enabled != active)
            {
                Trace(thisPtr, $"toggle-set({active})");
                obj.Enabled = active;
                _dirtyObjects[&thisPtr->ILayoutInstance] = obj;
            }
            else
                Trace(thisPtr, "toggle-noop");
        }
        else
        {
            Trace(thisPtr, "toggle-miss");
        }
    }

    // note that all BgParts transforms update the entire matrix
    // the methods other than SetTransformImpl still call SetTransform on the graphics object and collider, which can result in a SetTranslation call causing miniscule changes to the rotation quaternion, which can result in different world bounds (when truncated to int) on the same mesh
    private unsafe void BgTransFDetour(BgPartsLayoutInstance* thisPtr, Transform* t)
    {
        _bgTransF.Original(thisPtr, t);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
        {
            Trace(thisPtr, "transform-frozen");
            return;
        }

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgTransDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgTrans.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
        {
            Trace(thisPtr, "transform-frozen");
            return;
        }

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgRotDetour(BgPartsLayoutInstance* thisPtr, Quaternion* value)
    {
        _bgRot.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
        {
            Trace(thisPtr, "transform-frozen");
            return;
        }

        UpdateBgTransform(thisPtr);
    }

    private unsafe void BgScaleDetour(BgPartsLayoutInstance* thisPtr, Vector3* value)
    {
        _bgScale.Original(thisPtr, value);

        if (_transformOverride.ContainsKey(SceneTool.GetKey(&thisPtr->ILayoutInstance)))
        {
            Trace(thisPtr, "transform-frozen");
            return;
        }

        UpdateBgTransform(thisPtr);
    }

    private unsafe void UpdateBgTransform(BgPartsLayoutInstance* thisPtr)
    {
        Trace(thisPtr, "pre-update-transform");
        var tx = thisPtr->GetTransformImpl();

        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst))
        {
            if (tx->Translation != inst.Transform.Translation || tx->Rotation != inst.Transform.Rotation || tx->Scale != inst.Transform.Scale)
            {
                inst.Recreate(&thisPtr->ILayoutInstance, *tx);
                if (inst.Instance != null)
                    Trace(thisPtr, "update-transform", $"{inst.Instance.Instance.WorldBounds.Display()}, stored={inst.Transform.Display()}");
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
            }
        }
    }

    // some scheduler thing sets material flags on some BgParts directly, which sucks because i really don't feel like reversing more of the scheduler
    // this function incidentally calls GetCollider2 to try to update the collider flags (though in this case, there is no attached collider, so hooking Collider::SetMaterial would do nothing)
    // GetCollider2 seems to only be called during zone transition/setup, as opposed to GetCollider, which is called dozens of times per frame during player movement thanks to streaming manager
    private unsafe Collider* BgCollHackDetour(BgPartsLayoutInstance* thisPtr)
    {
        Trace(thisPtr, "pre-collider");
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var inst) && inst.Instance != null)
        {
            var oldFlags = inst.Instance.Instance.ForceSetPrimFlags;
            var newFlags = SceneExtractor.ExtractMaterialFlags(thisPtr->CollisionMaterialIdHigh << 32 | thisPtr->CollisionMaterialIdLow);
            if (oldFlags != newFlags)
            {
                Trace(thisPtr, "set-material", $"{oldFlags} => {newFlags}");
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
        Trace(thisPtr, "destroy");
        if (_objects.TryGetValue(&thisPtr->ILayoutInstance, out var obj))
        {
            Trace(thisPtr, "destroy-hit");
            obj.Destroyed = true;
            _dirtyObjects[&thisPtr->ILayoutInstance] = obj;
        }
        else
        {
            Trace(thisPtr, "destroy-miss");
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
                var prev = inst.Transform.Translation;
                var tnew = inst.Transform with { Translation = *value };
                Trace(thisPtr, "translation(box)", $"{prev} => {inst.Transform.Translation}");
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
                var prev = inst.Transform.Rotation;
                var tnew = inst.Transform with { Rotation = *value };
                Trace(thisPtr, "rotation(box)", $"{prev} => {inst.Transform.Rotation}");
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
                var prev = inst.Transform.Scale;
                var tnew = inst.Transform with { Scale = *value };
                Trace(thisPtr, "rotation(scale)", $"{prev} => {inst.Transform.Scale}");
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
                Trace(thisPtr, $"collider({active})");
                inst.ColliderEnabled = active;
                _dirtyObjects[&thisPtr->ILayoutInstance] = inst;
            }
        }
    }

    private unsafe void SgLoadDetour(SharedGroupLayoutInstance* thisPtr)
    {
        _sgLoad.Original(thisPtr);
        DetectAnimations(thisPtr);
    }

    private unsafe void DetectAnimations(SharedGroupLayoutInstance* thisPtr)
    {
        var key = SceneTool.GetKey(&thisPtr->ILayoutInstance);
        Trace(thisPtr, "detect-animations");

        // parent was initialized before child and has override, update children and skip rest
        // i'm not sure if action controllers can be nested, but it doesn't matter in either case, we treat the children as frozen
        if (_transformOverride.TryGetValue(key, out var existing))
        {
            Trace(thisPtr, "set-transform-group", existing.Display());
            OverrideTransform(&thisPtr->ILayoutInstance, existing);
            return;
        }

        var parentTransform = *thisPtr->GetTransformImpl();

        ProcessActionController(thisPtr->ActionController1, parentTransform);
        ProcessActionController(thisPtr->ActionController2, parentTransform);
        ProcessTimeline(thisPtr, parentTransform);
    }

    private unsafe void ProcessActionController(SGActionController* controller, in Transform parentTransform)
    {
        if (controller == null)
            return;

        var actionType = controller->AnimationType;
        switch (actionType)
        {
            case 1:
            case 2:
            case 3:
                var door = (SGDoorActionController*)controller;
                if (door->Door1 != null)
                    _hiddenObjects[SceneTool.GetKey(&door->Door1->ILayoutInstance)] = true;
                if (door->Door2 != null)
                    _hiddenObjects[SceneTool.GetKey(&door->Door2->ILayoutInstance)] = true;
                if (door->Collision != null)
                    _hiddenObjects[SceneTool.GetKey(&door->Collision->TriggerBoxLayoutInstance.ILayoutInstance)] = true;
                break;
            case 4:
            case 5:
                var rot = (SGRotationActionController*)controller;
                if (rot->ChildGroup != null) // TODO: ???
                    OverrideTransform(&rot->ChildGroup->ILayoutInstance, parentTransform);
                if (rot->Child1 != null)
                    OverrideTransform(rot->Child1, parentTransform);
                if (rot->Child2 != null)
                    OverrideTransform(rot->Child2, parentTransform);
                break;
            case 8:
            case 9:
                var path = (SGMovePathActionController*)controller;
                if (path->Owner != null)
                {
                    var @base = path->TransformBase;
                    // this controller is responsible for moving the entire owning prefab along the path
                    OverrideTransform(&path->Owner->ILayoutInstance, @base);
                }
                break;
            case 12:
            case 13:
                var cont = (SGTransformActionController*)controller;
                foreach (var inst in cont->Objects[..cont->NumObjects])
                    OverrideTransform(inst.Instance, parentTransform);
                break;
            default:
                Trace(controller->Owner, "unknown-animation", actionType.ToString());
                break;
        }
    }

    private unsafe void ProcessTimeline(SharedGroupLayoutInstance* group, in Transform parentTransform)
    {
        var subShift = 8 * (4 - (((group->Flags1 >> 4) & 7) + 1));
        foreach (var inst in group->TimeLineContainer.Instances)
        {
            if (inst.Value->DataPtr->Loop == 1)
            {
                List<uint> affectedInstances = [];
                foreach (var i in inst.Value->DataPtr->Instances)
                    affectedInstances.Add((uint)i.SubId << subShift);

                foreach (var child in group->Instances.Instances)
                    if (affectedInstances.Contains(child.Value->Instance->SubId))
                        OverrideTransform(child.Value->Instance, parentTransform);
            }
        }
    }

    private unsafe void OverrideTransform(ILayoutInstance* thisPtr, in Transform t)
    {
        var k = SceneTool.GetKey(thisPtr);
        Trace(thisPtr, "set-transform", t.Display());
        _transformOverride[k] = t;

        switch (thisPtr->Id.Type)
        {
            case InstanceType.SharedGroup:
                foreach (var inst in ((SharedGroupLayoutInstance*)thisPtr)->Instances.Instances)
                {
                    var instTransform = inst.Value->Transform;
                    Transform transformAdj = new()
                    {
                        Translation = t.Translation + instTransform.Translation,
                        Rotation = t.Rotation * instTransform.Rotation,
                        Scale = t.Scale * instTransform.Scale
                    };
                    OverrideTransform(inst.Value->Instance, transformAdj);
                }
                break;

            case InstanceType.BgPart:
                CreateObject((BgPartsLayoutInstance*)thisPtr, "recreate(transform)");
                break;
            case InstanceType.CollisionBox:
                CreateObject((CollisionBoxLayoutInstance*)thisPtr, "recreate(transform)");
                break;
        }
    }

    private unsafe void CreateObject(BgPartsLayoutInstance* thisPtr, string source)
    {
        Trace(thisPtr, $"pre-create({source})");

        if (!thisPtr->HasCollision())
        {
            Trace(thisPtr, "skip (no collision)");
            return;
        }

        var k = SceneTool.GetKey(&thisPtr->ILayoutInstance);

        if (!Customization.FilterObject(k, &thisPtr->ILayoutInstance))
        {
            Trace(thisPtr, "skip (filtered)");
            return;
        }

        if (!_transformOverride.TryGetValue(k, out var trans))
            trans = *thisPtr->GetTransformImpl();

        var obj = new MaybeEnabled()
        {
            Instance = SceneTool.Get().CreateInstance(thisPtr, trans),
            Enabled = thisPtr->HasEnabledFlag(),
            IsBgPart = true,
            Transform = trans,
        };
        Trace(thisPtr, $"create({source})", $"{obj.Instance!.Instance.WorldBounds.Display()}, stored={obj.Transform.Display()}");
        _dirtyObjects[&thisPtr->ILayoutInstance] = _objects[&thisPtr->ILayoutInstance] = obj;
    }

    private unsafe void CreateObject(CollisionBoxLayoutInstance* thisPtr, string source)
    {
        Trace(thisPtr, $"pre-create({source})");

        var k = SceneTool.GetKey(&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance);

        if (!Customization.FilterObject(k, &thisPtr->TriggerBoxLayoutInstance.ILayoutInstance))
        {
            Trace(thisPtr, "skip (filtered)");
            return;
        }

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
        Trace(thisPtr, $"create({source})", $"{obj.Instance!.Instance.WorldBounds.Display()}, stored={obj.Transform.Display()}");
        _dirtyObjects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance]
            = _objects[&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance]
            = obj;
    }

    private void Trace(ILayoutInstance* thisPtr, string eventName, string extra = "")
    {
        Slog.Trace(thisPtr, LastLoadedZone, eventName, extra);
    }

    private void Trace(BgPartsLayoutInstance* thisPtr, string eventName, string extra = "") => Trace(&thisPtr->ILayoutInstance, eventName, extra);
    private void Trace(SharedGroupLayoutInstance* thisPtr, string eventName, string extra = "") => Trace(&thisPtr->ILayoutInstance, eventName, extra);
    private void Trace(TriggerBoxLayoutInstance* thisPtr, string eventName, string extra = "") => Trace(&thisPtr->ILayoutInstance, eventName, extra);
    private void Trace(CollisionBoxLayoutInstance* thisPtr, string eventName, string extra = "") => Trace(&thisPtr->TriggerBoxLayoutInstance.ILayoutInstance, eventName, extra);
}
