# 0008. Volume pathfinding is tuned, not rewritten

## Status

Accepted.

## Context

Pathfinding in large zones is slow enough to be felt. Splitting "pathfinding" in two
shows the cost is not spread evenly. Measured on cached zones, 30 random pairs of
walkable points 150 to 500 yalms apart, off the game thread:

| | mean | median | p90 | worst |
|---|---|---|---|---|
| walking, Detour over the mesh | 86 ms | 3.9 ms | 349 ms | 598 ms |
| flying, `VoxelPathfind` over the volume | 1619 ms | 259 ms | 10362 ms | 10895 ms |

Walking is fine. The volume search is the whole problem, and its tail runs past ten
seconds. Reading it against those numbers turns one complaint into three causes.

**The heuristic is too weak for the graph it runs on.** `HeuristicDistance` is the
straight-line distance to the goal, scaled by 0.999. Moves are axis-only, so the real
cost of reaching a node can exceed that estimate by up to a factor of sqrt(3). An
underestimate that large is what makes A* stop behaving like A* and start behaving like
Dijkstra: the search spreads sideways across the zone instead of running at the goal.

**The line of sight check during search has no range limit.** With raycasts on, every
neighbour visit walks the line from the grandparent to see whether the two can be
collapsed into one hop. `_raycastLimitSq` is initialised to `float.MaxValue` and never
set anywhere, so the walk is unbounded, and it costs one octree descent per voxel it
crosses. Across 30 paths in one zone that is 13.9 million checks and 450 million voxel
steps.

**A goal that cannot be reached costs a full flood.** The search runs to its 1000000
step cap, taking ten to thirteen seconds, and then returns a partial path that does not
go where it was asked. Every failing pair in the measurements above turned out to have
its ends in different connected components of the empty space, which is a question the
search cannot answer cheaply and never gets told. That one stays open; see
`docs/backlog.md` item 3.

What is *not* a cause: allocation. `EnumerateNeighbours` and `EnumerateBorder` are
iterators, so a node expansion allocates, and `VisitNeighbour` walks the octree for an
entry point before checking whether the node is already closed. Rewriting both, verified
to produce byte-identical paths on 30 cases, moved the mean from 1612 ms to 1597 ms.
The obvious optimisation is not where the time is, and that rewrite is not in this
change.

## Decision

Keep upstream's search and put named dials on it. `VoxelPathfindTuning` holds
`HeuristicWeight`, `RaycastRange`, `SearchRaycast` and `MaxSteps`, and
`VoxelPathfindTuning.Upstream` reproduces upstream exactly. `VoxelPathfind` reads its
`Settings` field, defaulting to `Upstream`, so the search is unchanged unless something
sets it. `PathfindVolume` sets it from config on each query.

Upstream's `RandomnessMultiplier` moves into the same struct rather than being read from
`Service.Config` in the inner loop. That keeps every input to the search in one place,
and it lets a test make the search deterministic, which node-count assertions need.

Following ADR 0002, the values live in `HumanizerConfig` behind a master switch that
defaults off, and the edits to `VoxelPathfind.cs` are six short hunks.

**Defaults when the switch is on** are the fastest combination that still finds every
route upstream finds: weight 1.5, raycast range 25 yalms, raycast kept on, budget 200000.

| zone | | mean | median | p90 | worst | routes found |
|---|---|---|---|---|---|---|
| Yak T'el | upstream | 1619 ms | 259 ms | 10362 ms | 10895 ms | 23/30 |
| | tuned | 94 ms | 1.7 ms | 575 ms | 610 ms | 23/30 |
| Gyr Abania | upstream | 3991 ms | 449 ms | 11478 ms | 12601 ms | 18/30 |
| | tuned | 225 ms | 5.3 ms | 628 ms | 747 ms | 19/30 |
| Dravanian Forelands | upstream | 3195 ms | 474 ms | 11293 ms | 12347 ms | 22/30 |
| | tuned | 165 ms | 4.7 ms | 589 ms | 601 ms | 22/30 |

Straightened path length goes up 2 to 3 percent. Upstream's randomness is live on both
sides, which is why Gyr Abania comes out one route ahead rather than level; with randomness
off both find 19.

Each knob was also measured alone, which is how the defaults were chosen:

- **Weight 1.5** is where nearly all of the median gain comes from, and it costs nothing
  in routes found. Weighted A* gives up optimality in principle; in practice the greedy
  straightening from ADR 0007 runs afterwards and takes most of the length back.
- **Range 25** cuts the mean roughly in half on its own, also with no routes lost.
- **Raycast off** is about three times faster again, but it loses roughly one route in
  twenty: without the grandparent collapse a node advances one voxel rather than one hop,
  so a long twisting route exhausts the budget where upstream would have found it. That
  is why it is a switch and not the default.
- **Budget 200000** is the lowest that still found every route upstream found across the
  three zones. 100000 lost one in Yak T'el.

## Consequences

- A fresh install still behaves exactly like upstream. One checkbox turns on the tuned
  search; nothing about walking paths changes either way.
- The four values are a genuine speed against fidelity trade and they are exposed as
  such, rather than being picked once and buried. The tooltips say which end is upstream.
- The tail is now bounded by the budget rather than by how big the zone is, so a
  destination that cannot be reached costs a fraction of a second instead of thirteen.
  It still reports a partial path rather than a failure, which is the part left open.
- Weighted A* means the route is no longer the shortest one available. On flight this is
  hard to see, because straightening reshapes it anyway, but it is a real change and any
  future comparison of flight path quality has to record which weight produced it.
- `VoxelPathfind` is now testable outside the game: the only thing it still reaches for
  through `Service` is logging, so `tests/Navmesh.Tests` compiles it in against a stub
  and a three-level synthetic voxel world. Every knob has a test that the faster search
  still reaches the goal and that no hop it returns passes through geometry.
- Two upstream files join the merge checklist ADR 0002 keeps: `NavVolume/VoxelPathfind.cs`
  (a `Settings` field and an `ApplyTuning` helper, two assignments replaced by a call to
  it, and three one-line reads of the settings) and `NavmeshQuery.cs` (one line setting `Settings` per query,
  alongside the string pull call ADR 0007 already put there).
