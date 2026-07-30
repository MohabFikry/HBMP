#!/usr/bin/env python3
"""Tighten the coverage ratchet automatically after a green run (phase 24, Gate 1.4).

A floor that only a human remembers to raise never gets raised. CLAUDE.md has asked for >=80% on domain
logic since the beginning; the enforced floor sat at 58 because raising it was somebody's optional chore.
Meanwhile the measured value drifted upward and the guard protected less and less of what actually existed.

So: after a green run, any module measuring more than --slack points above its floor gets the floor moved
to measured-minus-1, and CI opens or updates a PR with the diff. Nobody has to notice.

  measured-minus-1, not measured: a floor pinned exactly to today's number turns the next one-line
  refactor into a red build, and a guard that cries wolf gets deleted. One point of give absorbs ordinary
  variance without giving up the ratchet.

  --slack 3 (default): below that, coverage noise alone would propose changes on every run.

Never lowers anything. Raising is unconditional; lowering is check-floor-monotonicity.py's business and
requires an ADR.

    raise-floors.py --report coverage/coverage-report.json [--write] [--selftest]
"""
from __future__ import annotations

import argparse
import json
import os
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FLOORS = os.path.join(REPO, "tools", "ci", "coverage-floors.json")


def propose(floors: dict, report: dict, slack: float) -> dict[str, tuple[float, float]]:
    """{key: (old, new)} for every floor that measured coverage has left behind."""
    out: dict[str, tuple[float, float]] = {}

    measured = {f"{m['module']}:{m['layer']}": m["pct"] for m in report["modules"] if m["layer"] != "Tests"}
    for key, floor in (floors.get("modules") or {}).items():
        pct = measured.get(key)
        if pct is None:
            continue  # module gone or renamed: not this tool's job to guess
        if pct - floor > slack:
            out[key] = (float(floor), float(int(pct) - 1))

    for agg in ("domain", "overall"):
        floor = (floors.get("aggregates") or {}).get(agg)
        pct = report["totals"].get(agg, {}).get("pct")
        if floor is None or pct is None:
            continue
        if pct - floor > slack:
            # The aggregate domain floor stops at the CLAUDE.md target: past 80 the ratchet has arrived,
            # and pinning it at 94 would make one deleted test a build failure for no further benefit.
            cap = floors.get("target_domain", 100) if agg == "domain" else 100
            new = min(float(int(pct) - 1), float(cap))
            if new > floor:
                out[f"aggregates.{agg}"] = (float(floor), new)
    return out


def apply(floors: dict, proposals: dict[str, tuple[float, float]]) -> dict:
    for key, (_old, new) in proposals.items():
        if key.startswith("aggregates."):
            floors["aggregates"][key.split(".", 1)[1]] = new
        else:
            floors["modules"][key] = new
    return floors


def selftest() -> int:
    ok = True
    floors = {"target_domain": 80, "aggregates": {"domain": 58, "overall": 45},
              "modules": {"a:Domain": 50, "b:Api": 10, "gone:Api": 30}}
    report = {
        "modules": [
            {"module": "a", "layer": "Domain", "pct": 91.0},   # +41 -> raise to 90
            {"module": "b", "layer": "Api", "pct": 12.0},      # +2, inside slack -> untouched
            {"module": "t", "layer": "Tests", "pct": 99.0},    # test code is never ratcheted
        ],
        "totals": {"domain": {"pct": 82.5}, "overall": {"pct": 50.7}},
    }
    p = propose(floors, report, slack=3)

    if p.get("a:Domain") != (50.0, 90.0):
        print(f"FAIL: expected a:Domain 50->90, got {p.get('a:Domain')}"); ok = False
    if "b:Api" in p:
        print("FAIL: a rise inside the slack must not propose a change"); ok = False
    if "gone:Api" in p:
        print("FAIL: a module absent from the report must be left alone"); ok = False
    # domain 82.5 is 24.5 above the floor, but the ratchet stops at the 80 target.
    if p.get("aggregates.domain") != (58.0, 80.0):
        print(f"FAIL: domain should ratchet to the 80 target, got {p.get('aggregates.domain')}"); ok = False
    if p.get("aggregates.overall") != (45.0, 49.0):
        print(f"FAIL: overall should ratchet 45->49, got {p.get('aggregates.overall')}"); ok = False

    # It must NEVER propose a decrease, whatever the measurement says.
    falling = {"modules": [{"module": "a", "layer": "Domain", "pct": 10.0}],
               "totals": {"domain": {"pct": 10.0}, "overall": {"pct": 10.0}}}
    if propose(floors, falling, slack=3):
        print("FAIL: falling coverage must propose nothing — that is the gate's job, not the ratchet's")
        ok = False

    print("selftest: PASS — raises past slack, respects the target cap, never lowers"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--report", default=os.path.join(REPO, "coverage", "coverage-report.json"))
    ap.add_argument("--floors", default=FLOORS)
    ap.add_argument("--slack", type=float, default=3.0)
    ap.add_argument("--write", action="store_true", help="rewrite the floors file in place")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    if not os.path.exists(a.report):
        print(f"raise-floors: no report at {a.report} — nothing to ratchet against.")
        return 0

    floors = json.load(open(a.floors, encoding="utf-8"))
    report = json.load(open(a.report, encoding="utf-8"))
    proposals = propose(floors, report, a.slack)

    if not proposals:
        print(f"raise-floors: no floor is more than {a.slack:g} points behind measured coverage.")
        return 0

    print(f"raise-floors: {len(proposals)} floor(s) can tighten:")
    for key, (old, new) in sorted(proposals.items()):
        print(f"  {key}: {old:g} -> {new:g}")

    if a.write:
        apply(floors, proposals)
        with open(a.floors, "w", encoding="utf-8") as fh:
            json.dump(floors, fh, indent=2)
            fh.write("\n")
        print(f"raise-floors: wrote {a.floors}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
