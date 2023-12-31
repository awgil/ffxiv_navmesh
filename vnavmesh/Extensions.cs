﻿using DotRecast.Core.Numerics;
using System.Numerics;

namespace Navmesh;

public static class Extensions
{
    public static RcVec3f SystemToRecast(this Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 RecastToSystem(this RcVec3f v) => new(v.X, v.Y, v.Z);
}
