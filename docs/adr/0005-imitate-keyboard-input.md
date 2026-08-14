# 0005. Imitate keyboard input

## Status

Accepted. Supersedes [0004](0004-analog-movement-magnitude.md).

## Context

ADR 0004 proposed making seam B a continuous velocity, on the reasoning that humans
modulate speed and the agent's constant full-throttle output does not. The first
capture set says otherwise.

Plugin-driven play here is keyboard only. Measured over three routes, walk input takes
exactly three values:

```
(0, +1)   1834 samples     magnitude 1.0
(0,  0)    185 samples     magnitude 0      (all of it one artifact, see below)
(-1, +1)    11 samples     magnitude 1.414  (diagonal)
```

There is no continuous magnitude to imitate, because a keyboard cannot produce one.
Median speed came out at 12.35 yalms/s human against 12.42 agent on the same route:
the same number. Whatever separates human from agent movement, it is not speed.

Two things that looked like differences in the first pass were not. Human captures
appeared to have 9-12% zero-input frames, suggesting key releases the agent never makes.
That was one contiguous dead segment at the head of each capture, caused by the recorder
starting on arrival rather than departure, and once trimmed both human and agent hold
forward continuously with zero releases. Diagonal input survives at 0.0-0.6%, too rare
to matter per capture.

What does separate them, on the same trimmed data:

- **Heading change rate.** 7.4 deg/s agent against 1.2 human on one route, 6.5 against
  4.7 on another. The agent re-aims at its waypoint every frame with no smoothing and
  sits in constant micro-correction; the human walks straight and turns deliberately.
- **Camera pitch.** One distinct value across an entire agent capture, against 109-169
  for the human. The agent never touches pitch at all.
- **Vertical control in flight.** The human pulsed the channel 8 times, median 1.4s each;
  the agent held a single 7.6s block.

## Decision

The imitation target is keyboard-shaped control.

Seam B emits axis values drawn from `{-1, 0, +1}` on each axis, so magnitude is one of
`{0, 1, sqrt(2)}` and nothing else. Speed is not a control channel for walking.

Realism work goes to where the measured differences actually are: heading dynamics,
camera behaviour, and the timing and shape of control episodes.

## Consequences

- ADR 0004 is superseded. The analog-magnitude experiment is not worth running, because
  even if the game honoured a fractional magnitude, emitting one would move the agent
  *away* from the target rather than toward it.
- The lattice constraint is close to free. The agent already emits pure forward input
  98% of the time, because it steers by turning the character and the direction relative
  to the character therefore converges on forward. Quantizing is a rounding step, not a
  redesign.
- This ADR mostly rules work out. Curvature-dependent speed, arrival deceleration and
  hesitation-as-slowdown are all off the table in their planned form. Any equivalent has
  to be expressed as key release patterns, and the captures show a human does not release
  keys on a straight run either.
- Conclusions are scoped to mounted, keyboard-driven overworld movement, which is what
  the corpus contains. A gamepad player, or on-foot movement, would need its own
  measurements before any of this transfers.
