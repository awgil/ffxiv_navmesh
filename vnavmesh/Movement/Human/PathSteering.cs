using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement.Human;

// Aims at a point projected ahead along the path rather than at the next waypoint.
//
// Upstream aims straight at Waypoints[0]. Waypoints are string-pulled corner points, so on
// reaching a corner the target direction jumps discontinuously and the character snaps to it at
// its maximum turn rate. Measured against human captures on the same route: the agent made one
// 125 degree turn at ~400 deg/s, a human made 5-22 corrections of about a degree at 22-42 deg/s.
//
// Projecting ahead makes the aim point slide around a corner continuously as the player
// approaches, so a corner becomes a spread of small heading changes instead of one jump.
public static class PathSteering
{
    // Point at `lookahead` yalms along the remaining path, measured from the player.
    // Falls back to the final waypoint when less path than that remains, so arrival still works.
    public static Vector3 LookaheadTarget(List<Waypoint> waypoints, Vector3 from, float lookahead)
    {
        if (waypoints.Count == 0)
            return from;

        var remaining = lookahead;
        var prev = from;
        for (var i = 0; i < waypoints.Count; ++i)
        {
            var next = waypoints[i].Position;
            var seg = Vector3.Distance(prev, next);
            if (seg >= remaining)
                return seg > 1e-4f ? Vector3.Lerp(prev, next, remaining / seg) : next;
            remaining -= seg;
            prev = next;
        }
        return waypoints[^1].Position;
    }

    // Rotates `current` toward `target` by at most maxRate * dt, as a backstop for the cases
    // lookahead alone does not smooth (a fresh path whose first corner is already behind us).
    public static Angle SlewHeading(Angle current, Angle target, Angle maxRate, float dt)
    {
        var delta = (target - current).Normalized();
        var limit = maxRate.Rad * dt;
        if (limit <= 0 || delta.Rad >= -limit && delta.Rad <= limit)
            return target;
        return current + (delta.Rad > 0 ? limit : -limit).Radians();
    }
}
