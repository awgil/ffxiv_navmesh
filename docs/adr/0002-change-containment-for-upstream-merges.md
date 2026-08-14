# 0002. Change containment for upstream merges

## Status

Accepted

## Context

Upstream vnavmesh is actively developed: mesh format versions, per-zone customizations
and game-patch signature fixes land regularly. A fork that diverges structurally stops
being able to absorb those, and a navmesh plugin that cannot absorb signature fixes is
dead the first time a patch ships.

At the same time, human-like movement is not a small feature. It touches path geometry,
per-frame control output, timing and camera. Left unmanaged it would end up smeared
across most of the movement code.

## Decision

All fork-specific code lives in `vnavmesh/Movement/Human/`. Existing upstream files are
edited only where a seam genuinely has to be opened, and each such edit is kept to a few
lines.

Two seams are recognised, and behaviour attaches only to those:

- **Seam A, path shaping.** The `List<Waypoint>` returned from `NavmeshManager.QueryPath`
  (`NavmeshManager.cs:156`). Runs once per path, off the game thread, may consult the
  mesh to validate what it produces.
- **Seam B, control signal.** The values `OverrideMovement` writes into the RMI walk and
  fly detours (`OverrideMovement.cs:96`). Runs per frame on the game thread, must be
  allocation-free and must never block.

Fork configuration lives in a single nested `HumanizerConfig` object hung off `Config`,
not as loose fields on `Config`. `Config.Load` reflects over fields and deserializes each
by type, so a nested POCO round-trips without extra plumbing, and the fork owns exactly
one line of `Config.cs`.

Every fork-added behaviour defaults to **off**. A fresh install behaves identically to
upstream until a switch is flipped.

## Consequences

- `git merge upstream/master` conflicts are confined to a handful of short, well-known
  hunks rather than spread through the movement code.
- The set of upstream files this fork touches is small enough to enumerate, and that
  enumeration is itself a review checklist after every merge:
  - `Config.cs` (one field, one draw call)
  - `Plugin.cs` (construct, update, dispose)
  - `MainWindow.cs` (one tab)
  - `Movement/OverrideMovement.cs` (observe-without-override, see ADR 0003)
  - `Movement/FollowPath.cs` (expose the `OverrideMovement` instance)
- Defaulting everything off means the fork is always shippable, and bisecting a
  behaviour regression is a matter of toggling rather than rebuilding.
- The cost is indirection: some behaviour reads less directly than it would if written
  inline. Accepted deliberately.
