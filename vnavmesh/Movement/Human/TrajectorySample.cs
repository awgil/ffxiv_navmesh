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
    SteerRan = 1 << 7, // humanized steering computed a heading this frame
    SteerRecovering = 1 << 8, // steering was on its raised ceiling rather than the normal one
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
        "steerCmd", // heading humanized steering commanded, radians; 0 when it did not run
        "wallDist", // distance to nearest mesh edge, yalms; -1 when none within clearance
        "wallPush", // applied wall avoidance weight; 0 when it did not fire
        "aimDist",  // how far ahead steering ended up aiming, yalms, before wall drift scales it
        "wpLeft",   // waypoints remaining; a short list leaves Follow only the destination to offer
    ];

    public float T;
    public float X, Y, Z;
    public float Facing;
    public float CamH, CamV;
    public float InLeft, InFwd;
    public float FlyUp;
    public float Speed;
    public SampleFlags Flags;
    public float SteerCmd;
    public float WallDist;
    public float WallPush;
    public float AimDist;
    public float WaypointsLeft;

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
        dest[12] = SteerCmd;
        dest[13] = WallDist;
        dest[14] = WallPush;
        dest[15] = AimDist;
        dest[16] = WaypointsLeft;
    }
}
