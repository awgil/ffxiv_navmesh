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

## Amendment: the metrics, named

This ADR required metrics to be named up front but did not name them. First
contact with a clean three-arm capture set settled it, and ruled out the
statistic that had been in informal use.

**Turn rate peak, and the share of frames above 60 deg/s, over the body of the
run.** Not the p99, and never the mean.

The mean averages snaps across the straight frames that surround them and hides
everything: a baseline agent and a human measured 6.5 against 4.7 deg/s on the
same route while differing by a factor of ten elsewhere.

The p99 fails for a subtler reason. The baseline's harshness lives in well under
one percent of frames, so the 99th percentile sits at exactly 0.0 deg/s for every
baseline run. The statistic cannot see the behaviour it was chosen to measure.
Peak and snap share do see it: baseline 42-431 deg/s peak, humanized 16-58, human
53-94.

**The body of the run means everything after the first two seconds.** The
departure is excluded and reported separately, because it is dominated by which
way the character happened to be facing on arrival at A, and the arms do not
arrive alike. Measured against the bearing from A to B, a human is already
aligned at departure, within 2 to 9 degrees, because they turn the camera while
standing still and the character snaps to it on the first movement frame. The
agent arrives 77 to 175 degrees off and turns while moving. Folding that into the
headline compares departure protocol, not steering.

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
