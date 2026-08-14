# Backlog

Known problems worth fixing, with enough context to pick up cold. Written down
rather than carried in anyone's head. Decisions belong in `adr/`; this is a list
of things observed to be wrong.

---

## 1. Wedged against geometry instead of routing around it

**Symptom.** Standing between two stones with something solid ahead, the
character keeps commanding forward into the collision rather than going around.
Observed repeatedly on the obstacle route (territory 956, route001), where it
needed manual `A`/`D` nudges to free itself.

**Why it happens.** The navmesh is eroded by the agent radius, so a path across
it is assumed passable. Game-side collision is finer than the mesh: small props
and stones can block a route the mesh believes is clear. Nothing in the follower
notices the difference. `FollowPath` writes the same input every frame regardless
of whether the character is actually moving, so a blocked path becomes a stall
rather than a re-route.

Humanized steering makes this worse in one respect: cutting corners aims closer
to geometry than upstream does. The mesh clamp (`SteeringClampToMesh`) shortens
the lookahead until the line is walkable, which handles the *mesh* saying no, but
not collision the mesh does not model.

**What already exists.** `Config.StopOnStuck` with `StuckTolerance` and
`StuckTimeoutMs` detects no-progress, and `FollowPath.OnStuck` fires an event
that `AsyncMoveRequest` uses to re-path when `RetryOnStuck` is set. Both default
off, and re-pathing from a spot the mesh thinks is fine will usually produce the
same path.

**Directions worth trying.**

- Detect no-progress locally (speed near zero while input is being written) and
  sidestep perpendicular to the commanded heading before resuming, which is what
  a player does by hand.
- Re-path with the blocked area temporarily penalised, rather than re-running the
  identical query. `NavmeshQuery.AvoidRadiusFilter` already rejects polygons near
  a point and could seed that.
- Treat repeated stalls at one spot as a mesh defect and record it, since the
  same coordinates recurred across runs, which points at specific geometry rather
  than a general failure.

**How to measure.** Captures record `speed` and the written input, so a stall is
frames where input is being written and speed stays under about 1 y/s. Note the
detector must not require input to be present: the failure mode where the aim
collapses onto the character writes no input at all, and an earlier version of
this check missed 44% of stalls because of it.

---

## 2. Flight is artificial, and often lands rather than flying

**Symptom.** Flying routes look obviously synthetic, and runs frequently end up
on the ground instead of staying airborne.

**Three separate causes, worth separating before fixing.**

**a. Flight paths are never smoothed.** *Addressed by ADR 0007, unverified in
game.* Walking paths come from Detour and are string-pulled into corner points.
Flight paths come from `VoxelPathfind`, an A* over the voxel map, and were
returned as raw voxel centres. `VoxelStringPull.Simplify` now collapses them
greedily by line of sight, under the existing `UseStringPulling` flag. This is
the cheap version, not the exact funnel upstream's `TODO` asks for, so it leaves
corners uncut and that TODO stays open.

**b. Humanized steering is applied to flight, and its queries do not mean
anything there.** *Fixed by ADR 0007.* `FollowPath.SteeringTarget` ran for every
path, walking or flying. Inside it, `WalkableFraction` and `NearestWall` are
*navmesh* queries: on a flight path they describe the ground far below, not the
air the character is moving through. It is now gated to walking paths, which
means arm 3 equals arm 2 in the air until flight gets its own control work.

**c. The walk to fly transition is crude.** *Open.* `FollowPath.Update` spams the jump
action on a fixed 100 ms interval whenever the desired position is above the
character and it is mounted. Fixed-period input is trivially distinguishable from
a human, and the condition can retrigger mid-flight.

**Measurement already available.** Captures carry `flyUp` (the vertical channel,
live only when the fly detour ran) and the `InFlight` flag. An earlier comparison
on the one flying route showed a human pulsing the vertical channel 8 times with
a median of about 1.4 s per pulse, against the agent holding a single 7.6 s
block, so there is a measurable difference to aim at.

**Scope note.** ADR 0005 scoped all conclusions to mounted, keyboard-driven
walking. Nothing measured about walking transfers to flight without its own
captures, and there is currently one flying route (territory 958, route003) with
two captures. That is not enough for anything.

**Watch for.** Straightening can remove the initial climb from a route whose
destination is no higher than its start, and takeoff only fires while the desired
position is above the character. A flying route over flat ground may now stay on
the ground longer. If that shows up, the fix belongs with (c).
