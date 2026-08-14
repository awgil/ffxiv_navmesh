"""Print a per-arm comparison of captures for one route.

Peak and snap share are the headline, per the amendment to ADR 0006: p99 reads
0.0 for every baseline run because its snaps occupy well under one percent of
frames, so it cannot see the behaviour it was meant to measure.

Usage:
    uv run capture-report [--route route001] [--captures PATH]
"""

from __future__ import annotations

import argparse
import math
import statistics as st
from collections import defaultdict
from pathlib import Path

from .metrics import DEPARTURE_WINDOW_S, body, commanded, departure, recovering_pct
from .model import Capture, load_all, territories, trim_to_departure

DEFAULT_CAPTURES = (
    Path.home()
    / "Library/Application Support/XIV on Mac/pluginConfigs/vnavmesh/captures"
)


def _spread(vals: list[float]) -> str:
    if not vals:
        return "-"
    if len(vals) == 1:
        return f"{vals[0]:.1f}"
    return f"{min(vals):.1f}-{max(vals):.1f}"


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--captures", type=Path, default=DEFAULT_CAPTURES)
    ap.add_argument("--route", default="route001")
    ap.add_argument("--territory", type=int, help="required when a route id exists in more than one zone")
    ap.add_argument("--list", action="store_true", help="show every captured route and exit")
    ap.add_argument("--window", type=float, default=DEPARTURE_WINDOW_S)
    args = ap.parse_args()

    if args.list:
        for (terr, route), n in sorted(territories(args.captures).items()):
            print(f"  --territory {terr} --route {route}   ({n} captures)")
        return

    caps = load_all(args.captures, route=args.route, territory=args.territory)
    seen = {c.territory for c in caps}
    if len(seen) > 1:
        print(f"route {args.route} exists in territories {sorted(seen)}; pass --territory to pick one")
        return
    if not caps:
        print(f"no captures for {args.route} under {args.captures}")
        return

    # schema 1 opens with a dead segment; later schemas already start at departure
    caps = [trim_to_departure(c) if c.schema < 2 else c for c in caps]

    by_arm: dict[str, list[Capture]] = defaultdict(list)
    for c in caps:
        by_arm[c.arm].append(c)

    print(f"territory {caps[0].territory} route {args.route}, body metrics exclude the first {args.window:g}s\n")
    print(f"{'arm':<28} {'n':>2} {'PEAK':>10} {'SNAP%':>6} {'p99':>9} {'p90':>7} {'path':>7} {'dur':>6}")
    print("-" * 88)
    for arm in sorted(by_arm):
        group = by_arm[arm]
        bodies = [body(c, since=args.window) for c in group]
        print(
            f"{arm:<28} {len(group):>2} "
            f"{_spread([b.peak for b in bodies]):>10} "
            f"{st.mean(b.snap_pct for b in bodies):>5.1f}% "
            f"{_spread([b.p99 for b in bodies]):>9} "
            f"{_spread([b.p90 for b in bodies]):>7} "
            f"{_spread([c.path_length for c in group]):>7} "
            f"{_spread([c.duration for c in group]):>6}"
        )

    print(f"\ndeparture, first {args.window:g}s -- driven by arrival facing, which is uncontrolled")
    print(f"{'arm':<28} {'turned':>8} {'peak':>8} {'startFacing spread':>20}")
    print("-" * 88)
    for arm in sorted(by_arm):
        group = by_arm[arm]
        deps = [departure(c, window=args.window) for c in group]
        facings = [math.degrees(c.start_facing) for c in group if c.start_facing is not None]
        spread = f"{max(facings) - min(facings):.0f} deg" if len(facings) > 1 else "-"
        print(
            f"{arm:<28} {_spread([d.turned_deg for d in deps]):>8} "
            f"{_spread([d.peak for d in deps]):>8} {spread:>20}"
        )

    cmds = [(c, commanded(c, since=args.window)) for c in caps]
    cmds = [(c, m) for c, m in cmds if m is not None]
    if cmds:
        print("\ncommanded heading vs achieved facing -- a gap means the character is not tracking")
        print(f"{'capture':<28} {'cmd p99':>9} {'facing p99':>11} {'recovering':>11}")
        print("-" * 88)
        for c, m in cmds:
            b = body(c, since=args.window)
            rec = recovering_pct(c)
            print(
                f"{c.path.name[-14:]:<28} {m.p99:>9.1f} {b.p99:>11.1f} "
                f"{(f'{rec:.0f}%' if rec is not None else '-'):>11}"
            )
