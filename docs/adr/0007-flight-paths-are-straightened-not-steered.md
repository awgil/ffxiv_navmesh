# 0007. Flight paths are straightened, not steered

## Status

Accepted.

## Context

Flying routes look obviously synthetic, and runs frequently end on the ground rather than
staying in the air. Reading the code turns that one complaint into three unrelated causes
(`docs/backlog.md` item 2), of which two are addressed here.

**Volume paths are never straightened.** Walking paths come from Detour and go through
`FindStraightPath`, which reduces a polygon corridor to corner points. Flying paths come
from `VoxelPathfind`, an A* over the voxel octree, and `PathfindVolume` turns its output
straight into waypoints, one per voxel entered. Following that exactly is a stair-step
through voxel space. Upstream lists it as a TODO ("use same string-pulling idea for
navvolumes") and there is an abandoned attempt at the exact funnel in `VoxelStraighten.cs`,
commented out at the point where the funnel becomes an arbitrary polygon.

The search already has a raycast option that collapses a node into its grandparent when
`VoxelSearch.LineOfSight` clears the line between them. That is the same idea, applied one
hop deep during search, and it barely straightens anything.

**Humanized steering runs on flying paths.** `FollowPath.SteeringTarget` executes for every
path. Inside it, `SteeringClampToMesh` calls `WalkableFraction` and the wall drift calls
`NearestWall`. Both are navmesh queries. On a flying path they answer about the ground
somewhere below rather than the air being flown through, so the clamp shortens the aim for
walls that are not in the way and the drift pushes away from geometry that is not there.
Every measurement backing the steering parameters (ADR 0005, ADR 0006) was taken on
mounted overworld walking; none of it was ever claimed to transfer to flight.

## Decision

**Straighten volume paths greedily by line of sight.** `VoxelStringPull.Simplify` walks
forward from an anchor while `LineOfSight` to the candidate holds, keeps the last candidate
that held, and repeats from there. It runs in `PathfindVolume` under the existing
`UseStringPulling` flag, which previously did nothing for volume paths.

The greedy form is chosen over the exact funnel deliberately. It reuses the primitive the
search already trusts, so it can only drop points the pathfind would have been willing to
skip; and it is a hundred lines against an unsolved geometry problem.

It stops at the first blocked candidate rather than scanning past it. Line of sight is not
monotone along a path, so a gap that reopens further ahead could otherwise pull the route
through a place the search deliberately went around.

**Scope humanized steering to walking paths.** `SteeringTarget` returns the upstream target
when the path is a flying one. The test is `IgnoreDeltaY`, which is set from `!fly` at
`Move` time, so it describes the path rather than the current state and steering cannot
flicker on and off as the character takes off and lands.

## Consequences

- Flying paths get shorter and less stepped, and the waypoints that survive are ones the
  volume says are directly reachable from each other. `UseStringPulling` becomes a real A/B
  switch for flight, where before it only affected mesh paths.
- Arm 3 of the three-arm evaluation (ADR 0006) is now identical to arm 2 on flying routes.
  Flight comparisons measure path quality alone until flight gets its own control work.
- Straightening can remove the initial climb out of a path whose destination is not higher
  than its start, and the takeoff in `FollowPath.Update` only fires while the desired
  position is above the character. A flying route across flat ground may therefore stay on
  the ground longer than before. This is worth watching in captures.
- The greedy pass is conservative by construction, so it will leave corners the exact funnel
  would have cut. Upstream's TODO stays open.
- The third cause, the fixed 100 ms jump spam at the walk to fly transition, is untouched
  and stays in the backlog. It is an input timing signature, not a path quality problem.
- Correctness here cannot be checked by flying around, so `tests/Navmesh.Tests` exists now:
  a synthetic voxel grid, and assertions that no hop the simplification invents passes
  through geometry. The plugin assembly is x64 only and needs Dalamud at runtime, so the
  tests compile the geometry sources in rather than referencing it.
