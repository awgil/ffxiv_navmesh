using System;

namespace Navmesh;

// usage: using var x = new OnDispose(action);
public readonly record struct OnDispose(Action a) : IDisposable
{
    public void Dispose() => a();
}
