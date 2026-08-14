"""Load trajectory captures written by the recorder (ADR 0003).

Handles every schema the recorder has emitted:

  1  original columns
  2  InputFresh split into WalkFresh/FlyFresh, stale channels zeroed
  3  adds the steering settings the capture was recorded under
  4  adds the commanded heading and the steering flags

Schema 1 captures carry an artifact: the recorder started on arrival at A rather
than on departure, so they open with a stale full-throttle sample followed by
however long the player stood still. `trim_to_departure` removes it, and must be
applied to schema 1 before any comparison. Later schemas start at departure and
are unaffected by it.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

# SampleFlags, mirrored from Movement/Human/TrajectorySample.cs
MOUNTED = 1 << 0
IN_FLIGHT = 1 << 1
DIVING = 1 << 2
JUMPING = 1 << 3
OVERRIDDEN = 1 << 4
WALK_FRESH = 1 << 5
FLY_FRESH = 1 << 6
STEER_RAN = 1 << 7
STEER_RECOVERING = 1 << 8


@dataclass(frozen=True)
class Steering:
    enabled: bool
    lookahead: float
    max_turn_rate: float
    recovery_turn_rate: float
    wall_clearance: float | None = None
    wall_avoidance: float | None = None
    wall_smoothing: float | None = None

    @property
    def label(self) -> str:
        if not self.enabled:
            return "baseline"
        base = f"steer L{self.lookahead:.0f} T{self.max_turn_rate:.0f} R{self.recovery_turn_rate:.0f}"
        if self.wall_avoidance is not None:
            base += f" W{self.wall_clearance:.0f}/{self.wall_avoidance:.2f}/{self.wall_smoothing:.2f}"
        return base


@dataclass
class Capture:
    path: Path
    schema: int
    source: str  # "human" or "agent"
    territory: int
    route: str
    start_facing: float | None
    start_pos: tuple[float, float, float] | None
    steering: Steering | None
    columns: list[str]
    rows: list[list[float]]

    @property
    def arm(self) -> str:
        """Which arm of the three-arm evaluation this is (ADR 0006)."""
        if self.source == "human":
            return "human"
        if self.steering is None:
            return "agent (unknown, schema<3)"
        return f"agent {self.steering.label}"

    def col(self, name: str) -> list[float]:
        i = self.columns.index(name)
        return [r[i] for r in self.rows]

    def has(self, name: str) -> bool:
        return name in self.columns

    @property
    def duration(self) -> float:
        t = self.col("t")
        return t[-1] - t[0] if t else 0.0

    @property
    def path_length(self) -> float:
        xs, ys, zs = self.col("x"), self.col("y"), self.col("z")
        return sum(
            math.dist((xs[i], ys[i], zs[i]), (xs[i - 1], ys[i - 1], zs[i - 1]))
            for i in range(1, len(xs))
        )


def load(path: Path) -> Capture:
    doc = json.loads(path.read_text())
    steering = None
    if (s := doc.get("steering")) is not None:
        steering = Steering(
            enabled=bool(s.get("enabled")),
            lookahead=float(s.get("lookahead", 0)),
            max_turn_rate=float(s.get("maxTurnRate", 0)),
            recovery_turn_rate=float(s.get("recoveryTurnRate", 0)),
            wall_clearance=None if "wallClearance" not in s else float(s["wallClearance"]),
            wall_avoidance=None if "wallAvoidance" not in s else float(s["wallAvoidance"]),
            wall_smoothing=None if "wallSmoothing" not in s else float(s["wallSmoothing"]),
        )
    sp = doc.get("startPos")
    return Capture(
        path=path,
        schema=int(doc.get("schema", 1)),
        source=str(doc.get("source", "unknown")),
        territory=int(doc.get("territory", 0)),
        route=str((doc.get("route") or {}).get("Id", path.parent.name)),
        start_facing=doc.get("startFacing"),
        start_pos=(sp[0], sp[1], sp[2]) if sp else None,
        steering=steering,
        columns=list(doc["columns"]),
        rows=[list(map(float, r)) for r in doc["samples"]],
    )


def load_all(
    root: Path, route: str | None = None, territory: int | None = None
) -> list[Capture]:
    """Route ids are only unique within a territory, so both are needed to select one."""
    caps = [load(p) for p in sorted(root.rglob("*.json"))]
    if route:
        caps = [c for c in caps if c.route == route]
    if territory:
        caps = [c for c in caps if c.territory == territory]
    return sorted(caps, key=lambda c: c.path.stat().st_mtime)


def territories(root: Path) -> dict[tuple[int, str], int]:
    """Every (territory, route) present, with how many captures each has."""
    out: dict[tuple[int, str], int] = {}
    for p in sorted(root.rglob("*.json")):
        c = load(p)
        out[(c.territory, c.route)] = out.get((c.territory, c.route), 0) + 1
    return out


def trim_to_departure(cap: Capture, threshold: float = 0.05) -> Capture:
    """Drop the dead head that schema 1 captures open with.

    The first sample holds the travelling phase's leftover full-throttle value, so
    it is skipped outright; the run then really begins at the first live input.
    """
    if not cap.rows:
        return cap
    rows = cap.rows[1:]
    il, inf = cap.columns.index("inLeft"), cap.columns.index("inFwd")
    fu = cap.columns.index("flyUp")
    ti = cap.columns.index("t")

    def mag(r: list[float]) -> float:
        return max(math.hypot(r[il], r[inf]), abs(r[fu]))

    start = next((i for i, r in enumerate(rows) if mag(r) > threshold), None)
    if start is None:
        return cap
    rows = rows[start:]
    t0 = rows[0][ti]
    rebased = [list(r) for r in rows]
    for r in rebased:
        r[ti] -= t0
    return Capture(**{**cap.__dict__, "rows": rebased})
