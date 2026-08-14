"""Metrics for comparing arms, named up front as ADR 0006 requires.

The headline number is the tail of the turn rate, not its mean. A mean averages
the snaps across all the straight frames and hides them: measured on route001 the
baseline agent and a human came out at 6.5 against 4.7 deg/s mean, while their
p99 differed by a factor of ten.

The departure is measured separately and never folded into the body metric. How
hard a run starts is set by which way the character happened to be facing on
arrival at A, which the recorder does not control and which has been observed to
vary by 92 degrees between runs of the same route. Mixing it into the body metric
compares starting conditions rather than steering.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from .model import STEER_RAN, STEER_RECOVERING, Capture

# a run is considered under way after this many seconds; before it, the character is
# still turning out of the standstill it arrived in
DEPARTURE_WINDOW_S = 2.0
# a frame turning faster than this counts as a snap rather than a correction
SNAP_DEG_S = 60.0


def _wrap(a: float) -> float:
    while a > math.pi:
        a -= 2 * math.pi
    while a < -math.pi:
        a += 2 * math.pi
    return a


def _quantile(sorted_vals: list[float], q: float) -> float:
    if not sorted_vals:
        return 0.0
    return sorted_vals[min(len(sorted_vals) - 1, int(q * len(sorted_vals)))]


def turn_rates(cap: Capture, column: str = "facing", since: float = 0.0) -> list[float]:
    """Per-frame rate of change of an angle column, in degrees/sec."""
    t, a = cap.col("t"), cap.col(column)
    out = []
    for i in range(1, len(t)):
        dt = t[i] - t[i - 1]
        if dt <= 0 or t[i] < since:
            continue
        rate = abs(math.degrees(_wrap(a[i] - a[i - 1]))) / dt
        # a rate above the character's own top turn speed is a sampling artifact
        if rate < 720:
            out.append(rate)
    return out


@dataclass
class Body:
    """Steering quality over the run proper, excluding the departure."""

    p50: float
    p90: float
    p99: float
    peak: float
    snap_frames: int
    total_frames: int

    @property
    def snap_pct(self) -> float:
        return 100 * self.snap_frames / self.total_frames if self.total_frames else 0.0


@dataclass
class Departure:
    """How the run leaves a standstill, kept apart from the body metric."""

    turned_deg: float
    seconds: float
    peak: float

    @property
    def mean_rate(self) -> float:
        return self.turned_deg / self.seconds if self.seconds > 0 else 0.0


def body(cap: Capture, since: float = DEPARTURE_WINDOW_S) -> Body:
    rates = turn_rates(cap, since=since)
    s = sorted(rates)
    return Body(
        p50=_quantile(s, 0.50),
        p90=_quantile(s, 0.90),
        p99=_quantile(s, 0.99),
        peak=max(rates) if rates else 0.0,
        snap_frames=sum(1 for r in rates if r > SNAP_DEG_S),
        total_frames=len(rates),
    )


def departure(cap: Capture, window: float = DEPARTURE_WINDOW_S) -> Departure:
    t, fac = cap.col("t"), cap.col("facing")
    idx = [i for i, tt in enumerate(t) if tt <= window]
    if len(idx) < 2:
        return Departure(0.0, 0.0, 0.0)
    turned = abs(math.degrees(_wrap(fac[idx[-1]] - fac[idx[0]])))
    rates = turn_rates(cap)[: len(idx) - 1]
    return Departure(
        turned_deg=turned,
        seconds=t[idx[-1]] - t[idx[0]],
        peak=max(rates) if rates else 0.0,
    )


def commanded(cap: Capture, since: float = DEPARTURE_WINDOW_S) -> Body | None:
    """Same measure over the heading steering asked for, when the capture records it.

    Divergence between this and the body figure means the character is not tracking
    what it was told, which is a different fault from the controller commanding
    something harsh.
    """
    if not cap.has("steerCmd"):
        return None
    flags = cap.col("flags")
    if not any(int(f) & STEER_RAN for f in flags):
        return None
    rates = turn_rates(cap, column="steerCmd", since=since)
    s = sorted(rates)
    return Body(
        p50=_quantile(s, 0.50),
        p90=_quantile(s, 0.90),
        p99=_quantile(s, 0.99),
        peak=max(rates) if rates else 0.0,
        snap_frames=sum(1 for r in rates if r > SNAP_DEG_S),
        total_frames=len(rates),
    )


def recovering_pct(cap: Capture) -> float | None:
    if not cap.has("flags") or cap.schema < 4:
        return None
    flags = [int(f) for f in cap.col("flags")]
    if not any(f & STEER_RAN for f in flags):
        return None
    return 100 * sum(1 for f in flags if f & STEER_RECOVERING) / len(flags)
