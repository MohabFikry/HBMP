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
    # 2026-08-10 table & button audit. Listed for the reason the three above are: what it guards fails
    # SILENTLY. The six design guards run inside the ordinary web suite, so a violation is loud — but
    # DELETING one leaves the suite green with one fewer file, which is indistinguishable from a good day.
    # This gate names them; if it stopped running, nothing would notice that nothing was being checked.
    "design-guards",
    # 2026-08-09 audit — dev-only auth relaxations. Listed for the same reason as the two above: what it
    # guards fails SILENTLY and in the safe-looking direction. A stack running with MFA disabled serves
    # every request successfully; the only sign is the one nobody looks for. If this gate stopped running,
    # the flag would be free to travel into the first non-dev environment anybody writes.
    "dev-auth-flags",
    # 31.6 — response-schema coverage. Same argument as openapi-drift above, one level down: that gate
    # compares the specs it is given, and the specs described no response bodies at all, so it passed while
    # three fields were added to a prescription line. This one ratchets the share of endpoints that say what
    # they return, and its failure mode if it stopped running would be the number quietly sliding back.
    "response-schemas",
    # 2026-08-09 audit — the live SPA bundle carries no fixture backend. Listed for the reason every gate
    # above it is: what it guards is invisible from outside. A bundle with a demo sign-in compiled into it
    # serves every page correctly, and the only way to know is to read the built JavaScript, which nobody
    # does by hand. This one lives in frontend-ci, so its heartbeat arrives in a second file — see load().
    "live-bundle",
    # 2026-08-09 audit — the documented service inventory. Listed for the same reason as the rest: a document
    # that has drifted looks exactly like one that has not, and the three copies of the service count sat at
    # 14, 21 and 17 against a real 22 for as long as it took someone to audit them by hand.
    "service-inventory",
    # 2026-08-10 — the two security-ci gates, added after `sca-sast-image` was found never to have executed
    # AT ALL. Its action was pinned to `aquasecurity/trivy-action@0.24.0`, a tag that does not exist (every
    # tag this action publishes is `v`-prefixed), so GitHub failed the job while resolving actions, before
    # any step ran. The scoreboard showed a red X that read as flake, and no gate here noticed the silence.
    # That is precisely the case this file exists for, and these two were outside its coverage — which is
    # the argument for listing every gate rather than only the ones in tools/ci/.
    "secret-scan",
    "sca-sast-image",
]


DEFAULT_FILE = "gate-heartbeats.json"


def load(state_dir: str) -> dict[str, str]:
    """Every `gate-heartbeats*.json` in the directory, merged, NEWEST WINS per gate.

    More than one file because the gates do not all run in one workflow: backend-ci writes
    `gate-heartbeats.json`, frontend-ci writes `gate-heartbeats.frontend.json`, and the watchdog downloads
    both artifacts into the same directory. Merging here rather than making the watchdog do it keeps the
    "which pipeline owns which gate" question out of a YAML file — adding a gate means adding a line to
    REQUIRED_GATES and recording a heartbeat from wherever it runs, and nothing else has to know.

    Newest-wins rather than last-file-wins because a gate that moves between pipelines would otherwise be
    aged by whichever pipeline stopped running it."""
    merged: dict[str, str] = {}
    if not os.path.isdir(state_dir):
        return merged
    for name in sorted(os.listdir(state_dir)):
        if not (name.startswith("gate-heartbeats") and name.endswith(".json")):
            continue
        try:
            data = json.load(open(os.path.join(state_dir, name), encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        if not isinstance(data, dict):
            continue
        for gate, stamp in data.items():
            if gate not in merged or str(stamp) > str(merged[gate]):
                merged[gate] = stamp
    return merged


def record(state_dir: str, gate: str, now: datetime, filename: str = DEFAULT_FILE) -> None:
    """Write into ONE file, read back from that same file only. Recording through `load()` would fold every
    other pipeline's heartbeats into this artifact, so each upload would carry a stale copy of the others'
    and a gate that stopped running would keep looking fresh."""
    os.makedirs(state_dir, exist_ok=True)
    path = os.path.join(state_dir, filename)
    data: dict[str, str] = {}
    if os.path.exists(path):
        try:
            data = json.load(open(path, encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            data = {}
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

    # 5. HEARTBEATS SPLIT ACROSS PIPELINES. backend-ci writes gate-heartbeats.json, frontend-ci writes
    #    gate-heartbeats.frontend.json, and the watchdog downloads both into one directory. A gate whose
    #    heartbeat lives only in the second file must count as having run — and recording it there must not
    #    rewrite the first, or each artifact would carry a stale copy of the other's gates and a pipeline
    #    that stopped running would keep looking fresh.
    with tempfile.TemporaryDirectory() as tmp:
        for g in REQUIRED_GATES:
            if g != "live-bundle":
                record(tmp, g, now - timedelta(days=1))
        problems = check(tmp, 7, now)
        if len(problems) != 1 or "live-bundle" not in problems[0]:
            print(f"FAIL: the frontend gate should be the only one missing; got {problems}"); ok = False

        record(tmp, "live-bundle", now - timedelta(days=1), filename="gate-heartbeats.frontend.json")
        if check(tmp, 7, now):
            print("FAIL: a heartbeat in a second file should be read"); ok = False
        backend = json.load(open(os.path.join(tmp, DEFAULT_FILE), encoding="utf-8"))
        if "live-bundle" in backend:
            print("FAIL: recording into one pipeline's file must not write into another's"); ok = False

    print("selftest: PASS — never-run, stale, boundary and split-pipeline cases all behave"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--state-dir", default=os.path.join(REPO, ".ci-state"))
    ap.add_argument("--max-age-days", type=int, default=7)
    ap.add_argument("--record", help="record a heartbeat for this gate and exit")
    ap.add_argument("--state-file", default=DEFAULT_FILE,
                    help="which heartbeat file to write (one per pipeline; all are read back)")
    ap.add_argument("--now", help="ISO timestamp override, for testing")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    now = datetime.fromisoformat(a.now) if a.now else datetime.now(timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)

    if a.record:
        record(a.state_dir, a.record, now, a.state_file)
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
