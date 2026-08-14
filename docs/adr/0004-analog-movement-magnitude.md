# 0004. Analog movement magnitude as the control signal

## Status

Superseded by [0005](0005-imitate-keyboard-input.md).

The premise below did not survive first contact with recorded data. Human and agent
walking speed measured the same, because keyboard input has no continuous magnitude to
imitate. The validation experiment was never run and is not worth running. Kept for the
reasoning, not as guidance.

## Context

Upstream writes a unit vector into the RMI walk detour (`OverrideMovement.cs:96`):

```csharp
var dir = relDir.Value.h.ToDirection();
*sumLeft = dir.X;
*sumForward = dir.Y;
```

Magnitude is always exactly 1. The character therefore moves at full speed at all times:
no deceleration into corners, no acceleration out of them, no slowing on approach to a
destination. Several of the planned behaviours (curvature-dependent speed from the
two-thirds power law, arrival deceleration, hesitation) are expressible only as a
magnitude below 1.

`sumLeft` and `sumForward` are the summed analog stick values the game's own input path
produces, and FFXIV supports graded walk and run speed on a gamepad. So a magnitude of
0.5 *should* produce roughly half speed. Should is not knows.

## Decision

Make the seam-B contract a **velocity** rather than a target position: `OverrideMovement`
accepts a desired input vector whose magnitude lies in `[0, 1]`, and the steering layer
owns both its direction and its length.

This is deliberately sequenced ahead of the steering work, because a large part of that
work is meaningless if magnitude turns out not to be honoured.

## Validation

Before any steering code is written, run a throwaway build that ramps the written
magnitude from 0 to 1 while logging actual world-space speed derived from position
deltas, and plot the transfer curve. Specifically look for:

- whether speed responds to magnitude at all, or saturates at full run;
- a deadzone near zero below which the character does not move;
- quantization, in particular a single walk/run step rather than a continuous response;
- whether the response differs mounted, in flight, and while diving.

The recorder from ADR 0003 already logs both input and speed, so this experiment is a
route capture plus a plot rather than new instrumentation.

## Consequences if honoured

Steering proceeds as planned; speed becomes a first-class continuous control.

## Consequences if not honoured

Speed control collapses to the walk/run toggle, a binary. The behaviours that depend on
continuous speed have to be re-expressed:

- Curvature-dependent slowing becomes path shaping instead: widen corner radii so the
  fixed-speed trajectory traces what a slowing human would trace.
- Arrival deceleration becomes a walk-toggle inside a braking radius.
- Hesitation becomes brief full stops rather than slowdowns, which is a coarser and more
  detectable approximation, and would need its own timing model to be believable.

This ADR is superseded rather than amended if the experiment forces that branch.
