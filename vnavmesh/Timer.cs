using System;

namespace Navmesh;

// very simple stopwatch timer
public struct Timer
{
    public DateTime Start;

    public static Timer Create() => new() { Start = DateTime.Now };

    public TimeSpan Value()
    {
        var now = DateTime.Now;
        var delta = now - Start;
        Start = now;
        return delta;
    }
}
