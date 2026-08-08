#!/usr/bin/env python3
"""Coverage floors ratchet UP. A decrease is a documented act or it is not permitted (phase 24, Gate 1.3).

The floors used to live as defaults inside coverage-gate.sh, where lowering one was a one-character edit
that read like a config tweak in review. That is the wrong shape for a control whose entire job is to be
hard to move: under time pressure the cheapest way to make a build green is to move the bar, and nothing
in the diff says a bar was moved.

So floors live in tools/ci/coverage-floors.json, and this guard compares the committed file against its
previous revision. Any decrease fails unless the commit message cites an ADR (ADR-NNNN) that EXISTS and
NAMES the module being lowered — the same rule the denominator guard applies to exclusions, because both
levers move the same number.

    check-floor-monotonicity.py [--base REF] [--selftest]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FLOORS = os.path.join("tools", "ci", "coverage-floors.json")
ADR_RE = re.compile(r"ADR-(\d{4})")


def flatten(doc: dict) -> dict[str, float]:
    """{'aggregates.domain': 58, 'services/policy:Domain': 84, ...} — one flat namespace to compare."""
    out: dict[str, float] = {}
    for k, v in (doc.get("aggregates") or {}).items():
        out[f"aggregates.{k}"] = float(v)
    for k, v in (doc.get("modules") or {}).items():
        out[str(k)] = float(v)
    if "target_domain" in doc:
        out["target_domain"] = float(doc["target_domain"])
    return out


def git(*args: str, cwd: str = REPO) -> str:
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True).stdout


def check(base: str, cwd: str = REPO, commit_msg: str | None = None) -> list[str]:
    path = os.path.join(cwd, FLOORS)
    if not os.path.exists(path):
        return [f"{FLOORS} is missing — the floors have no source of truth."]
    current = flatten(json.load(open(path, encoding="utf-8")))

    prev_raw = git("show", f"{base}:{FLOORS}", cwd=cwd)
    if not prev_raw.strip():
        return []  # first commit of the file: nothing to ratchet against
    previous = flatten(json.loads(prev_raw))

    lowered = {k: (previous[k], current[k]) for k in previous
               if k in current and current[k] < previous[k]}
    # Deleting a floor is lowering it to zero by another route.
    removed = [k for k in previous if k not in current]

    if not lowered and not removed:
        return []

    msg = commit_msg if commit_msg is not None else git("log", "-1", "--format=%B", cwd=cwd)
    adrs = ADR_RE.findall(msg)
    problems = []

    if not adrs:
        for k, (was, now) in sorted(lowered.items()):
            problems.append(f"floor '{k}' lowered {was:g} -> {now:g} with no ADR cited in the commit message.")
        for k in sorted(removed):
            problems.append(f"floor '{k}' was DELETED (= lowered to nothing) with no ADR cited.")
        problems.append("Floors ratchet up. Lowering one is a decision: write the ADR, cite it here, and "
                        "name the module in it.")
        return problems

    adr_dir = os.path.join(cwd, "docs", "adr")
    bodies = []
    for num in adrs:
        matches = [f for f in os.listdir(adr_dir) if f.startswith(f"{num}-")] if os.path.isdir(adr_dir) else []
        if not matches:
            problems.append(f"commit cites ADR-{num} but docs/adr/{num}-*.md does not exist.")
            continue
        bodies.append(open(os.path.join(adr_dir, matches[0]), encoding="utf-8").read())

    joined = "\n".join(bodies)
    for k, (was, now) in sorted(lowered.items()):
        needle = k.split(":")[0].replace("aggregates.", "")
        if needle not in joined:
            problems.append(f"floor '{k}' lowered {was:g} -> {now:g} but no cited ADR mentions '{needle}'. "
                            "An ADR that does not name what it lowers is not a decision about it.")
    for k in sorted(removed):
        needle = k.split(":")[0].replace("aggregates.", "")
        if needle not in joined:
            problems.append(f"floor '{k}' deleted but no cited ADR mentions '{needle}'.")
    return problems


def selftest() -> int:
    ok = True
    base_doc = {"target_domain": 80, "aggregates": {"domain": 58, "overall": 45},
                "modules": {"services/policy:Domain": 84}}

    def write(cwd, doc):
        os.makedirs(os.path.join(cwd, "tools", "ci"), exist_ok=True)
        json.dump(doc, open(os.path.join(cwd, FLOORS), "w"), indent=2)

    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["git", "init", "-q", tmp], check=True)
        subprocess.run(["git", "-C", tmp, "config", "user.email", "t@t"], check=True)
        subprocess.run(["git", "-C", tmp, "config", "user.name", "t"], check=True)
        os.makedirs(os.path.join(tmp, "docs", "adr"))
        write(tmp, base_doc)
        subprocess.run(["git", "-C", tmp, "add", "-A"], check=True)
        subprocess.run(["git", "-C", tmp, "commit", "-qm", "base"], check=True)

        # 1. unchanged -> pass
        if check("HEAD", cwd=tmp, commit_msg="chore: nothing"):
            print("FAIL: unchanged floors should pass"); ok = False

        # 2. RAISED -> pass (the ratchet's whole purpose)
        raised = json.loads(json.dumps(base_doc)); raised["aggregates"]["domain"] = 60
        write(tmp, raised)
        if check("HEAD", cwd=tmp, commit_msg="ci: raise domain floor"):
            print("FAIL: raising a floor must always be allowed"); ok = False

        # 3. LOWERED, no ADR -> fail
        low = json.loads(json.dumps(base_doc)); low["aggregates"]["domain"] = 40
        write(tmp, low)
        if not check("HEAD", cwd=tmp, commit_msg="ci: adjust floor"):
            print("FAIL: a lowered floor with no ADR must fail"); ok = False

        # 4. LOWERED, ADR cited but missing -> fail
        if not check("HEAD", cwd=tmp, commit_msg="ci: adjust floor (ADR-0099)"):
            print("FAIL: citing a nonexistent ADR must fail"); ok = False

        # 5. ADR exists but does not name the module -> fail
        open(os.path.join(tmp, "docs", "adr", "0099-x.md"), "w").write("# about something else\n")
        if not check("HEAD", cwd=tmp, commit_msg="ci: adjust floor (ADR-0099)"):
            print("FAIL: an ADR that does not name the module must fail"); ok = False

        # 6. ADR exists AND names it -> pass
        open(os.path.join(tmp, "docs", "adr", "0099-x.md"), "w").write("lowering domain because ...\n")
        if check("HEAD", cwd=tmp, commit_msg="ci: adjust floor (ADR-0099)"):
            print("FAIL: a properly documented lowering should pass"); ok = False

        # 7. DELETING a floor is lowering it by another route -> fail without an ADR
        gone = json.loads(json.dumps(base_doc)); gone["modules"] = {}
        write(tmp, gone)
        if not check("HEAD", cwd=tmp, commit_msg="ci: tidy"):
            print("FAIL: deleting a floor must be treated as lowering it"); ok = False

    print("selftest: PASS — raises allowed; lowering and deletion refused without a naming ADR"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="HEAD~1")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    problems = check(a.base)
    if problems:
        print("::error::coverage floor change rejected:")
        for p in problems:
            print(f"  - {p}")
        return 1
    print("floor-monotonicity guard: OK — no undocumented decrease.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
