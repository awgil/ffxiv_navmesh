using Dalamud.Hooking;
using InteropGenerator.Runtime;
using System;

namespace Navmesh;

public sealed class HookAddress<T> : IDisposable where T : Delegate
{
    private readonly Hook<T> _hook;

    public nint Address => _hook.Address;
    public T Original => _hook.Original;
    public bool Enabled
    {
        get => _hook.IsEnabled;
        set
        {
            if (value)
                _hook.Enable();
            else
                _hook.Disable();
        }
    }

    public HookAddress(Address address, T detour, bool autoEnable = true) : this(address.Value, detour, autoEnable) { }
    public HookAddress(string signature, T detour, bool autoEnable = true) : this(Service.SigScanner.ScanText(signature), detour, autoEnable) { }
    public HookAddress(nint address, T detour, bool autoEnable = true)
    {
        Service.Log.Debug($"Hooking {typeof(T)} @ 0x{address:X}");
        _hook = Service.Hook.HookFromAddress(address, detour);
        if (autoEnable)
            _hook.Enable();
    }

    public void Dispose() => _hook.Dispose();
}
