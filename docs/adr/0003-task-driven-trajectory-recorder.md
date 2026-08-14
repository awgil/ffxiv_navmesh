# 0003. Task-driven trajectory recorder

## Status

Accepted

## Context

Tuning human-like movement by eye does not converge. We need recorded human trajectories
to fit against, and recorded agent trajectories over *the same ground* to compare them to.

Free-form wandering produces unusable data. Two traversals of different routes differ
because the routes differ, which swamps the difference we actually care about. Any
comparison of human against agent needs the two to be solving an identical problem:
same territory, same start, same destination, ideally same starting facing.

That means the recorder has to be task-driven. It hands out an assignment rather than
passively logging whatever happens.

There is a second, subtler pairing problem: the human has to *arrive* at the start point
somehow, and walking there by hand leaves the player at an arbitrary position and facing
that the agent run cannot reproduce.

## Decision

Define a **route** as `(id, territory, A, B, fly)`, stored in a route book that can be
authored in-game by standing somewhere and clicking a button.

A capture session runs a four-state machine:

1. **Travelling.** The plugin uses its own pathfinding to move the player to A. This is
   the existing `AsyncMoveRequest`, unmodified. Both human and agent captures start this
   way, so both begin from the same place, reached the same way.
2. **Armed.** On arrival the plugin stops and waits. B is drawn in the world. Nothing is
   recorded yet.
3. **Recording.** Starts on the first non-zero movement input, whoever produced it. For
   human captures that is the player pressing a key; for agent captures the plugin issues
   a move to B and recording begins when the resulting input appears. Recording therefore
   starts at the moment of departure in both cases, not at some arbitrary earlier instant.
4. **Finished.** On reaching B within tolerance the capture is written to disk. Timeout
   and manual abort discard it.

Per-frame samples record position, facing, camera azimuth and altitude, the raw walk and
fly input values, derived speed, and a condition-flag bitmask.

Recording the *raw input values* matters more than recording position. Position is the
integral of what the game did with the input; the input is the control signal the agent
has to imitate, and it is what seam B actually writes.

To capture input without perturbing it, `OverrideMovement` gains a distinction between
having its hooks installed and actually writing values. The recorder asks for hooks
installed and writing off, so the detour observes genuine human input and passes it
through untouched.

Captures are written as JSON with a `columns` array and samples as arrays of numbers,
one file per capture under `captures/<territory>/<routeId>/<source>-<utc>.json`.

## Consequences

- Human and agent captures for a route are directly comparable, sample against sample,
  which is what makes the analysis in the project plan possible at all.
- The agent's own pathfinding is a dependency of collecting human data. If the mesh is
  not built for a zone, no captures happen there. Acceptable, and it fails loudly.
- Building a corpus is manual labour: someone has to walk each route. Route authoring is
  therefore in-game and low-friction by design.
- Recording start is defined by input rather than by a countdown, so a human
  pre-departure pause is *not* measured by this tool. That parameter has to come from
  elsewhere; do not read it out of `armedToFirstInputMs`, which measures how long it took
  the player to notice the prompt.
- Array-of-arrays JSON keeps files small and loads into pandas or similar in one line,
  at the cost of not being readable as-is.
- The `OverrideMovement` change is a real edit to an upstream file, and is called out in
  ADR 0002's list of touched files.
