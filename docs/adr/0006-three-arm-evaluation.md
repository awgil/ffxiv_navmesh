# 0006. Three-arm evaluation

## Status

Accepted

## Context

The first capture set compared two arms, human against agent, and that was enough to
find differences but not enough to judge a change. Knowing the agent turns six times
more than a human tells us nothing about whether a smoothing change helped, hurt, or
merely moved the number somewhere else.

There is also a trap in two-arm comparison: it is easy to make the agent *different*
from baseline and call that progress, without checking the difference went toward the
human rather than sideways.

## Decision

Every evaluation runs three arms over the same routes:

1. **Human.** The player walks it.
2. **Baseline agent.** Current upstream movement behaviour.
3. **Humanized agent.** Whatever the fork currently does.

Arm 2 is not a separate plugin install. ADR 0002 requires every fork-added behaviour to
default to off and to be identical to upstream in that state, so arm 2 is this same build
with humanization disabled. One build, one recorder, one config flag between arms 2 and 3.

Success for any given change is arm 3 sitting closer to arm 1 than arm 2 does, on a
metric chosen before the change was written. A change that only moves arm 3 away from
arm 2 has not been shown to do anything.

## Consequences

- No plugin swapping between arms, so the three captures differ only in the behaviour
  under test. Recorder version, mesh, and route are identical by construction.
- Arm 2 has to be re-captured whenever upstream movement changes, since it is a moving
  baseline rather than a fixed recording.
- Metrics have to be named up front, or the comparison degrades into picking whichever
  number looks good afterwards. The first capture set already produced two findings that
  evaporated under a corrected trim, which is exactly the failure mode this guards
  against.
- The recorder defects that contaminated the first capture set have to be fixed before
  any arm is collected in anger, otherwise all three arms inherit the same artifact and
  the comparison silently inherits it too.
