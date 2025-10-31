using Dalamud.Game.ClientState;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Navmesh;

public sealed unsafe class ColliderSet : IDisposable
{
    public readonly SortedSet<ulong> Colliders = [];

    public event Action<ColliderSet> Initialized = delegate { };
    public event Action<ColliderSet, Pointer<ILayoutInstance>, bool, ulong> Changed = delegate { };
    public event Action<ColliderSet> Cleared = delegate { };

    private unsafe delegate void BgPartsSetActiveDelegate(BgPartsLayoutInstance* thisPtr, byte active);
    private readonly Hook<BgPartsSetActiveDelegate> _bgPartsSetActiveHook;
    private unsafe delegate void TriggerBoxSetActiveDelegate(TriggerBoxLayoutInstance* thisPtr, byte active);
    private readonly Hook<TriggerBoxSetActiveDelegate> _triggerBoxSetActiveHook;

    public bool Active { get; private set; }

    public int Count => Colliders.Count;

    public ColliderSet()
    {
        Service.ClientState.ZoneInit += OnZoneInit;

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsSetActiveDelegate>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxSetActiveDelegate>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();
    }

    public void Init()
    {
        Colliders.Clear();

        Active = true;
        Service.Log.Debug($"[init] collecting active colliders from level");
        var scene = new SceneDefinition();
        scene.FillFromActiveLayout();
        foreach (var part in scene.BgParts)
            Colliders.Add(part.key);
        foreach (var box in scene.Colliders)
            Colliders.Add(box.key);

        Service.Log.Debug($"[init] collected {Count} colliders");
        Initialized.Invoke(this);
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= OnZoneInit;
        _bgPartsSetActiveHook.Dispose();
        _triggerBoxSetActiveHook.Dispose();
    }

    private void OnZoneInit(ZoneInitEventArgs _)
    {
        Active = true;
        Cleared.Invoke(this);
        Colliders.Clear();
    }

    private unsafe void Set(ILayoutInstance* inst, bool active)
    {
        var key = ((ulong)inst->Id.InstanceKey << 32) | (ulong)inst->SubId;

        if (active)
        {
            if (Colliders.Add(key))
                Changed.Invoke(this, inst, active, key);
        }
        else
        {
            if (Colliders.Remove(key))
                Changed.Invoke(this, inst, active, key);
        }
    }

    private unsafe void BgPartsSetActiveDetour(BgPartsLayoutInstance* thisPtr, byte active)
    {
        _bgPartsSetActiveHook.Original(thisPtr, active);

        // TODO: BgPartsLayoutInstance.CreateSecondary (responsible for loading collider) exits early if (Flags2 & 1) == 0, figure out why?
        if (Active)
            SetBgpart(thisPtr, active == 1);
    }

    private unsafe void TriggerBoxSetActiveDetour(TriggerBoxLayoutInstance* thisPtr, byte active)
    {
        _triggerBoxSetActiveHook.Original(thisPtr, active);

        if (Active)
            SetTriggerbox(thisPtr, active == 1);
    }

    private unsafe void SetBgpart(BgPartsLayoutInstance* thisPtr, bool active)
    {
        // only fire activation event if a collider is expected
        if (active && (thisPtr->CollisionMeshPathCrc != 0 || thisPtr->AnalyticShapeDataCrc != 0))
            Set(&thisPtr->ILayoutInstance, true);

        // always fire deactivation event, it will be a no-op if the key is not present
        if (!active)
            Set(&thisPtr->ILayoutInstance, false);
    }

    private unsafe void SetTriggerbox(TriggerBoxLayoutInstance* thisPtr, bool active)
    {
        if (thisPtr->Id.Type is InstanceType.CollisionBox)
            Set(&thisPtr->ILayoutInstance, active);
    }

    public ICollection<ulong> IdsSorted => Colliders;

    public string GetCacheKey() => GetCacheKey(IdsSorted);
    public static string GetCacheKey(ICollection<ulong> ids)
    {
        var ids0 = ids.ToArray();
        var bytes = new byte[sizeof(ulong) * ids0.Length];
        Buffer.BlockCopy(ids0, 0, bytes, 0, bytes.Length);

        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder();
        foreach (var byte_ in hash)
            sb.Append(byte_.ToString("X2"));

        return sb.ToString();
    }
}
