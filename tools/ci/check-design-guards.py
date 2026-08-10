#!/usr/bin/env python3
"""2026-08-10 table & button audit — the guards that hold the design standards must still be there.

The audit found 106 of 116 tables and most of the product's buttons diverging from a design system that
already contained the right answer for nearly every case. The fixes closed those; six static guards keep
them closed — they read the SPA's own source and fail on a table that ships without its toolbar, a
destructive write on a non-destructive button, a glyph on two of ten Save buttons, a column of figures
aligned by hand, a sortable header that sorts nothing, or a variant nobody uses.

Every one of them runs inside the ordinary web suite, so a VIOLATION is already loud. What is not loud is
DELETION: remove `button-icon-policy.test.ts` and the suite goes green with one fewer file, which is
indistinguishable from a good day. That is the same shape as the openapi-drift gate sitting red for a day
because nothing said it should be running, and the same reason this repo has a freshness watchdog at all.

So this gate asserts the guards EXIST before it asserts they pass. The manifest below carries a reason per
file, because "why is this not being checked any more?" is the question a green build cannot answer.

Usage:
  check-design-guards.py                 verify the manifest, then run the guards
  check-design-guards.py --list          print the manifest and exit
  check-design-guards.py --selftest      prove the gate catches a missing guard
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WEB = ROOT / "apps" / "web"
DS = ROOT / "apps" / "design-system"

# Each guard, and the standard it holds. Removing an entry is a claim that the standard no longer applies —
# which is a decision worth writing down, not a line to delete quietly.
GUARDS: dict[Path, str] = {
    WEB / "test" / "queue-table-view.test.tsx":
        "an operational queue uses DataTableView; a bare table needs a stated reason (audit H3)",
    WEB / "test" / "table-truncation.test.tsx":
        "a page showing a SUBSET says so — pager, total, truncation notice (audit H1, H4)",
    WEB / "test" / "table-sortable.test.tsx":
        "a sortable header actually sorts, and controlled vs self-sorting stay distinct (audit M1)",
    WEB / "test" / "table-numeric-columns.test.ts":
        "a column of magnitudes is aligned by the COLUMN, not by a span (audit L5)",
    WEB / "test" / "destructive-actions.test.tsx":
        "a destructive write is danger + confirmed, and no button ships unstyled (audit H2, L6)",
    WEB / "test" / "button-icon-policy.test.ts":
        "one glyph per action class; no variant offered that nothing uses (audit M4, L2)",
    WEB / "test" / "button-context-rules.test.ts":
        "row-action size, dismiss weight beside danger, no selection-by-hue (audit M6, M7, M8)",
    DS / "test" / "icons.test.ts":
        "one glyph per meaning, and no two names drawing the same path (audit M5)",
}


def missing() -> list[tuple[Path, str]]:
    """Guards named in the manifest that are no longer on disk."""
    return [(p, why) for p, why in GUARDS.items() if not p.is_file()]


def run_guards() -> int:
    """Run each package's guards through its own vitest. Returns a process exit code."""
    failed = 0
    for pkg, files in ((WEB, [p for p in GUARDS if WEB in p.parents]),
                       (DS, [p for p in GUARDS if DS in p.parents])):
        if not files:
            continue
        vitest = pkg / "node_modules" / ".bin" / "vitest"
        if not vitest.is_file():
            print(f"::error::{vitest} is not installed — run the install step before this gate")
            return 1
        rel = [str(p.relative_to(pkg)) for p in files]
        print(f"  {pkg.name}: {len(rel)} guard(s)")
        proc = subprocess.run([str(vitest), "run", *rel], cwd=pkg, check=False)
        failed |= proc.returncode
    return 1 if failed else 0


def selftest() -> int:
    """The gate is worth having only if a missing guard is what makes it fail."""
    global GUARDS
    keep = GUARDS
    try:
        with tempfile.TemporaryDirectory() as tmp:
            GUARDS = {Path(tmp) / "gone.test.ts": "a guard that is not there"}
            if not missing():
                print("::error::selftest — a deleted guard was not reported missing")
                return 1
        GUARDS = {p: why for p, why in keep.items()}
        if missing():
            print("::error::selftest — the real manifest reports a guard missing")
            return 1
    finally:
        GUARDS = keep
    print("selftest ok — a deleted guard is caught, and the real manifest is intact")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()

    if "--list" in argv:
        for p, why in GUARDS.items():
            print(f"{p.relative_to(ROOT)}\n    {why}")
        return 0

    print(f"design guards: {len(GUARDS)} expected")
    gone = missing()
    if gone:
        for p, why in gone:
            print(f"::error::{p.relative_to(ROOT)} is gone — it held: {why}")
        print("::error::a design guard was deleted. The suite stays green without it, which is the "
              "whole reason this gate names them.")
        return 1

    return run_guards()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
