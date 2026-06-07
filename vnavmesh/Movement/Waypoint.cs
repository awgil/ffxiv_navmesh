using System.Numerics;

namespace Navmesh.Movement;

public readonly record struct Waypoint(Vector3 Position, Navmesh.AreaId Type)
{
    public Waypoint(Vector3 Position) : this(Position, Navmesh.AreaId.Default) { }
}
