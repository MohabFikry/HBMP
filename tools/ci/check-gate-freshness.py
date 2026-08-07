#!/usr/bin/env python3
"""A gate that has not run in a week is a failing gate (phase 24, Gate 1.2).

THE FAILURE THIS EXISTS FOR. Around 27 July backend-ci began dying at the migration-compat gate. Every
gate after it — Kong route coverage, SPA scope drift, apply-migrations, the tenant-isolation fuzzer, the
whole test suite, the coverage gate — simply never executed. For roughly a month the build was red for one
reason while five other controls reported nothing at all, and the coverage number that was supposed to
catch a regression went unseen because nothing ever asked it.

Nobody was negligent. The failure is structural: a skipped gate and a passing gate look identical from
outside, because both are silent. This turns silence into a signal.

Each gate writes a heartbeat when it EXECUTES (pass or fail — executing is the thing being measured, not
succeeding). This guard reads them and fails when any gate's newest heartbeat is older than --max-age-days.
Wire it into the daily scheduled workflow, where it fails loudly on a repo nobody has pushed to.

    check-gate-freshness.py --state-dir .ci-state [--max-age-days 7] [--record GATE] [--selftest]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from datetime import datetime, timedelta, timezone

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# The gates whose silence must be an alarm. Adding a gate to the pipeline means adding it here, and the
# selftest proves an unknown-but-required gate fails rather than being quietly ignored.
REQUIRED_GATES = [
    "migration-compat",
    "kong-route-coverage",
    "spa-scopes",
    "integration-dpia",
    "apply-migrations",
    "tenant-isolation",
    "tests",
    "coverage",
    "coverage-exclusions",
    "floor-monotonicity",
    "invariant-registry",
    # 25.9 — added after openapi-drift sat red for a day without anyone noticing. It was in the CI
    # scoreboard but not here, so if it had stopped RUNNING instead of failing, this watchdog would have
    # said nothing. A gate that reports the API contract is worth exactly as much silence as one that
    # reports coverage.
    "openapi-generate",
    "openapi-drift",
    # 28.2 — one origin for the app, the API and the issuer. Listed here for the same reason openapi-drift is:
    # what it guards fails SILENTLY. A cross-origin login has its SameSite=Strict session cookie dropped by
    # the browser, so the sign-in reports success and the next authorize reports login_required — the user is
    # told their password is wrong. If this gate stopped running, nothing else would notice.
    "login-origin",
]


def load(state_dir: str) -> dict[str, str]:
    path = os.path.join(state_dir, "gate-heartbeats.json")
    if not os.path.exists(path):
        return {}
    try:
        return json.load(open(path, encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}


def record(state_dir: str, gate: str, now: datetime) -> None:
    os.makedirs(state_dir, exist_ok=True)
    path = os.path.join(state_dir, "gate-heartbeats.json")
    data = load(state_dir)
    data[gate] = now.isoformat()
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2, sort_keys=True)
        fh.write("\n")


def check(state_dir: str, max_age_days: int, now: datetime) -> list[str]:
    data = load(state_dir)
    cutoff = now - timedelta(days=max_age_days)
    problems = []
    for gate in REQUIRED_GATES:
        stamp = data.get(gate)
        if stamp is None:
            problems.append(f"gate '{gate}' has NEVER recorded an execution. A gate nobody has run is not "
                            "a gate; it is a file.")
            continue
        try:
            when = datetime.fromisoformat(stamp)
        except ValueError:
            problems.append(f"gate '{gate}' has an unparseable heartbeat {stamp!r}.")
            continue
        if when.tzinfo is None:
            when = when.replace(tzinfo=timezone.utc)
        if when < cutoff:
            age = (now - when).days
            problems.append(f"gate '{gate}' last executed {age} days ago ({when.date()}), over the "
                            f"{max_age_days}-day limit. It is not passing — it is not running.")
    return problems


def selftest() -> int:
    ok = True
    now = datetime(2026, 7, 30, tzinfo=timezone.utc)
    with tempfile.TemporaryDirectory() as tmp:
        # 1. nothing recorded -> every required gate is reported, not silently tolerated
        problems = check(tmp, 7, now)
        if len(problems) != len(REQUIRED_GATES):
            print(f"FAIL: expected all {len(REQUIRED_GATES)} gates flagged, got {len(problems)}"); ok = False

        # 2. all fresh -> passes
        for g in REQUIRED_GATES:
            record(tmp, g, now - timedelta(days=1))
        if check(tmp, 7, now):
            print("FAIL: fresh heartbeats should pass"); ok = False

        # 3. ONE gate goes stale -> fails, and names that gate. This is the July scenario exactly.
        record(tmp, "coverage", now - timedelta(days=30))
        problems = check(tmp, 7, now)
        if len(problems) != 1 or "coverage" not in problems[0]:
            print(f"FAIL: a single stale gate must fail and be named; got {problems}"); ok = False

        # 4. exactly at the boundary is still fresh; a day past is not
        record(tmp, "coverage", now - timedelta(days=7) + timedelta(minutes=1))
        if check(tmp, 7, now):
            print("FAIL: a heartbeat inside the window should pass"); ok = False
        record(tmp, "coverage", now - timedelta(days=7, minutes=1))
        if not check(tmp, 7, now):
            print("FAIL: a heartbeat outside the window should fail"); ok = False

    print("selftest: PASS — never-run, stale and boundary cases all behave"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--state-dir", default=os.path.join(REPO, ".ci-state"))
    ap.add_argument("--max-age-days", type=int, default=7)
    ap.add_argument("--record", help="record a heartbeat for this gate and exit")
    ap.add_argument("--now", help="ISO timestamp override, for testing")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    now = datetime.fromisoformat(a.now) if a.now else datetime.now(timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)

    if a.record:
        record(a.state_dir, a.record, now)
        print(f"gate-freshness: recorded '{a.record}' at {now.isoformat()}")
        return 0

    problems = check(a.state_dir, a.max_age_days, now)
    if problems:
        print("::error::stale or never-executed gates:")
        for p in problems:
            print(f"  - {p}")
        return 1
    print(f"gate-freshness: OK — all {len(REQUIRED_GATES)} gates executed within {a.max_age_days} days.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
