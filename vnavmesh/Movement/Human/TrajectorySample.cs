using System;

namespace Navmesh.Movement.Human;

[Flags]
public enum SampleFlags
{
    None = 0,
    Mounted = 1 << 0,
    InFlight = 1 << 1,
    Diving = 1 << 2,
    Jumping = 1 << 3,
    Overridden = 1 << 4, // plugin wrote the input this frame rather than passing player input through
    WalkFresh = 1 << 5, // the walk detour ran this frame, so inLeft/inFwd are live rather than stale
    FlyFresh = 1 << 6, // the fly detour ran this frame, so flyUp is live rather than stale
}

// one frame of a capture; see ADR 0003
// laid out to serialize as a flat array of numbers, so Columns and Write must stay in lockstep
public struct TrajectorySample
{
    public static readonly string[] Columns =
    [
        "t",        // seconds since recording started
        "x", "y", "z",
        "facing",   // player rotation, radians
        "camH",     // camera azimuth, radians, 0 = north increasing clockwise
        "camV",     // camera altitude, radians, positive = looking up
        "inLeft",   // raw walk input, left component
        "inFwd",    // raw walk input, forward component
        "flyUp",    // raw fly input, vertical component
        "speed",    // world-space speed derived from position delta, yalms/second
        "flags",    // SampleFlags bitmask
    ];

    public float T;
    public float X, Y, Z;
    public float Facing;
    public float CamH, CamV;
    public float InLeft, InFwd;
    public float FlyUp;
    public float Speed;
    public SampleFlags Flags;

    public readonly void Write(Span<float> dest)
    {
        dest[0] = T;
        dest[1] = X;
        dest[2] = Y;
        dest[3] = Z;
        dest[4] = Facing;
        dest[5] = CamH;
        dest[6] = CamV;
        dest[7] = InLeft;
        dest[8] = InFwd;
        dest[9] = FlyUp;
        dest[10] = Speed;
        dest[11] = (float)Flags;
    }
}
