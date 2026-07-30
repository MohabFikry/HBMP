#!/usr/bin/env python3
"""Gaming the denominator must be as hard as lowering the floor (phase 24, Gate 0.2).

A coverage percentage has two levers. Everyone watches the floor; nobody watches the denominator. Adding
one line to coverage-exclusions.txt can move the number further than a floor change and attracts none of
the scrutiny, so this guard applies the SAME rule to both: an exclusion may be added only by a commit
that cites an ADR which exists and names the excluded path.

It also enforces the hard prohibitions outright — no ADR makes it acceptable to exclude Domain code or a
whole service, because at that point the number stops describing the thing it claims to describe.

    check-coverage-exclusions.py [--base REF] [--selftest]
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXCLUSIONS = os.path.join("tools", "ci", "coverage-exclusions.txt")
ADR_RE = re.compile(r"ADR-(\d{4})")

# No ADR excuses these. They do not narrow the measurement, they void it.
FORBIDDEN = [
    (re.compile(r"(^|/)Domain(/|$)"), "Domain code is the layer the floor exists to protect"),
    (re.compile(r"^services/[^/]+/?$"), "excluding a whole service"),
    (re.compile(r"^(services|libs|apps|tools)/?$"), "excluding an entire source tree"),
    (re.compile(r"^\.?/?$"), "excluding the repository"),
]


def parse(text: str) -> list[str]:
    out = []
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            out.append(line.replace("\\", "/").lstrip("/"))
    return out


def git(*args: str, cwd: str = REPO) -> str:
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True).stdout


def check(base: str, cwd: str = REPO, commit_msg: str | None = None) -> list[str]:
    path = os.path.join(cwd, EXCLUSIONS)
    current = parse(open(path, encoding="utf-8").read()) if os.path.exists(path) else []

    problems = []
    for entry in current:
        for pat, why in FORBIDDEN:
            if pat.search(entry):
                problems.append(f"'{entry}' is never excludable — {why}.")

    previous = parse(git("show", f"{base}:{EXCLUSIONS}", cwd=cwd))
    added = [e for e in current if e not in previous]
    if not added:
        return problems

    msg = commit_msg if commit_msg is not None else git("log", "-1", "--format=%B", cwd=cwd)
    adrs = ADR_RE.findall(msg)
    if not adrs:
        problems.append(
            "coverage-exclusions.txt gained " + ", ".join(f"'{a}'" for a in added) +
            " but the commit message cites no ADR. An exclusion is a denominator change and needs the "
            "same paper trail as a floor change: reference ADR-NNNN in the commit message.")
        return problems

    for num in adrs:
        matches = [f for f in os.listdir(os.path.join(cwd, "docs", "adr"))
                   if f.startswith(f"{num}-")] if os.path.isdir(os.path.join(cwd, "docs", "adr")) else []
        if not matches:
            problems.append(f"commit cites ADR-{num} but docs/adr/{num}-*.md does not exist.")
            continue
        body = open(os.path.join(cwd, "docs", "adr", matches[0]), encoding="utf-8").read()
        for entry in added:
            if entry.rstrip("/") not in body:
                problems.append(
                    f"ADR-{num} does not mention '{entry}'. The ADR must name the path it is excluding, "
                    "or it is not a decision about this exclusion.")
    return problems


def selftest() -> int:
    """A guard with no failing-case test is a guard nobody has proven works."""
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["git", "init", "-q", tmp], check=True)
        subprocess.run(["git", "-C", tmp, "config", "user.email", "t@t"], check=True)
        subprocess.run(["git", "-C", tmp, "config", "user.name", "t"], check=True)
        os.makedirs(os.path.join(tmp, "tools", "ci"))
        os.makedirs(os.path.join(tmp, "docs", "adr"))
        p = os.path.join(tmp, EXCLUSIONS)

        open(p, "w").write("# base\n")
        subprocess.run(["git", "-C", tmp, "add", "-A"], check=True)
        subprocess.run(["git", "-C", tmp, "commit", "-qm", "base"], check=True)

        # 1. unchanged -> passes
        if check("HEAD", cwd=tmp, commit_msg="no change"):
            print("FAIL: an unchanged file should pass"); ok = False

        # 2. added entry with NO ADR -> must fail
        open(p, "w").write("# base\nservices/foo/Infrastructure/Generated/\n")
        if not check("HEAD", cwd=tmp, commit_msg="chore: exclude some stuff"):
            print("FAIL: an added exclusion with no ADR must fail"); ok = False

        # 3. ADR cited but file missing -> must fail
        if not check("HEAD", cwd=tmp, commit_msg="chore: exclude (ADR-0099)"):
            print("FAIL: citing a nonexistent ADR must fail"); ok = False

        # 4. ADR exists but does not name the path -> must fail
        open(os.path.join(tmp, "docs", "adr", "0099-x.md"), "w").write("# unrelated\n")
        if not check("HEAD", cwd=tmp, commit_msg="chore: exclude (ADR-0099)"):
            print("FAIL: an ADR that does not name the path must fail"); ok = False

        # 5. ADR exists AND names the path -> passes
        open(os.path.join(tmp, "docs", "adr", "0099-x.md"), "w").write(
            "generated: services/foo/Infrastructure/Generated\n")
        if check("HEAD", cwd=tmp, commit_msg="chore: exclude (ADR-0099)"):
            print("FAIL: a properly documented exclusion should pass"); ok = False

        # 6. Domain is forbidden even WITH a perfect ADR
        open(p, "w").write("# base\nservices/foo/Domain/\n")
        open(os.path.join(tmp, "docs", "adr", "0099-x.md"), "w").write("services/foo/Domain\n")
        if not check("HEAD", cwd=tmp, commit_msg="chore: exclude (ADR-0099)"):
            print("FAIL: Domain must be unexcludable regardless of paperwork"); ok = False

    print("selftest: PASS — rejects undocumented, mis-documented and forbidden exclusions"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="HEAD~1", help="revision to compare the exclusion list against")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()

    problems = check(a.base)
    if problems:
        print("::error::coverage exclusions rejected:")
        for p in problems:
            print(f"  - {p}")
        return 1
    print("coverage-exclusions guard: OK — no undocumented or forbidden exclusions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
