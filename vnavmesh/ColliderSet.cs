using Dalamud.Game.ClientState;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Generic;

namespace Navmesh;

public record struct CollidersChangedEventArgs(Pointer<ILayoutInstance> Instance, bool Active, ulong CacheKey);

public sealed unsafe class ColliderSet : IDisposable
{
    private readonly SortedSet<ulong> _colliders = [];

    public event EventHandler<CollidersChangedEventArgs> Changed = delegate { };

    private unsafe delegate void BgPartsSetActiveDelegate(BgPartsLayoutInstance* thisPtr, byte active);
    private readonly Hook<BgPartsSetActiveDelegate> _bgPartsSetActiveHook;
    private unsafe delegate void TriggerBoxSetActiveDelegate(TriggerBoxLayoutInstance* thisPtr, byte active);
    private readonly Hook<TriggerBoxSetActiveDelegate> _triggerBoxSetActiveHook;

    public bool Active { get; private set; }

    public int On => _colliders.Count;

    public ColliderSet()
    {
        Service.ClientState.ZoneInit += OnZoneInit;

        _bgPartsSetActiveHook = Service.Hook.HookFromSignature<BgPartsSetActiveDelegate>("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 0F B6 C2 ", BgPartsSetActiveDetour);
        _triggerBoxSetActiveHook = Service.Hook.HookFromSignature<TriggerBoxSetActiveDelegate>("80 61 2B EF C0 E2 04 ", TriggerBoxSetActiveDetour);
        _bgPartsSetActiveHook.Enable();
        _triggerBoxSetActiveHook.Enable();
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
        _colliders.Clear();
    }

    private unsafe void Set(ILayoutInstance* inst, bool active)
    {
        var key = ((ulong)inst->Id.InstanceKey << 32) | (ulong)inst->SubId;

        if (active)
        {
            if (_colliders.Add(key))
                Changed.Invoke(this, new(inst, active, key));
        }
        else
        {
            if (_colliders.Remove(key))
                Changed.Invoke(this, new(inst, active, key));
        }
    }

    private unsafe void BgPartsSetActiveDetour(BgPartsLayoutInstance* thisPtr, byte active)
    {
        _bgPartsSetActiveHook.Original(thisPtr, active);

        // TODO: BgPartsLayoutInstance.CreateSecondary (responsible for loading collider) exits early if (Flags2 & 1) == 0, figure out why?
        if (Active)
        {
            // only fire activation event if a collider is expected
            if (active == 1 && (thisPtr->CollisionMeshPathCrc != 0 || thisPtr->AnalyticShapeDataCrc != 0))
                Set(&thisPtr->ILayoutInstance, true);

            // always fire deactivation event, it will be a no-op if the key is not present
            if (active == 0)
                Set(&thisPtr->ILayoutInstance, false);
        }
    }

    private unsafe void TriggerBoxSetActiveDetour(TriggerBoxLayoutInstance* thisPtr, byte active)
    {
        _triggerBoxSetActiveHook.Original(thisPtr, active);

        if (Active && thisPtr->Id.Type is InstanceType.CollisionBox)
            Set(&thisPtr->ILayoutInstance, active == 1);
    }
}
