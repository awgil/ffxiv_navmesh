using System;
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
    // Target is where to aim; Consumed is how many leading waypoints now lie behind the player and
    // should be dropped. ProjectedDistToEnd is path distance left, measured from the projection.
    public readonly record struct Result(Vector3 Target, int Consumed, float ProjectedDistToEnd);

    // Progress along the path is measured by projecting onto it, not by proximity to waypoints.
    // Upstream can use proximity because it drives through every waypoint; steering cuts corners,
    // so a corner waypoint would never be reached and would sit at the head of the list forever,
    // dragging the aim point backwards.
    public static Result Follow(List<Waypoint> waypoints, Vector3 from, float lookahead)
    {
        if (waypoints.Count == 0)
            return new(from, 0, 0);
        if (waypoints.Count == 1)
            return new(waypoints[0].Position, 0, Vector3.Distance(from, waypoints[0].Position));

        // nearest point on the polyline, and which segment it fell on
        var bestSeg = 0;
        var bestPoint = waypoints[0].Position;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i + 1 < waypoints.Count; ++i)
        {
            var a = waypoints[i].Position;
            var ab = waypoints[i + 1].Position - a;
            var len2 = ab.LengthSquared();
            var t = len2 > 1e-6f ? Math.Clamp(Vector3.Dot(from - a, ab) / len2, 0f, 1f) : 0f;
            var p = a + ab * t;
            var d = (from - p).LengthSquared();
            if (d < bestDistSq)
            {
                bestDistSq = d;
                bestSeg = i;
                bestPoint = p;
            }
        }

        // walk forward from the projection, so a corner already behind us cannot pull the aim back
        var remaining = lookahead;
        var travelled = 0f;
        var prev = bestPoint;
        var target = waypoints[^1].Position;
        var found = false;
        for (var i = bestSeg + 1; i < waypoints.Count; ++i)
        {
            var next = waypoints[i].Position;
            var seg = Vector3.Distance(prev, next);
            if (!found && seg >= remaining)
            {
                target = seg > 1e-4f ? Vector3.Lerp(prev, next, remaining / seg) : next;
                found = true;
            }
            if (!found)
                remaining -= seg;
            travelled += seg;
            prev = next;
        }

        return new(target, bestSeg, travelled);
    }

    // Rotates `current` toward `target` by at most maxRate * dt.
    public static Angle SlewHeading(Angle current, Angle target, Angle maxRate, float dt)
    {
        var delta = (target - current).Normalized();
        var limit = maxRate.Rad * dt;
        if (limit <= 0 || delta.Rad >= -limit && delta.Rad <= limit)
            return target;
        return current + (delta.Rad > 0 ? limit : -limit).Radians();
    }
}
