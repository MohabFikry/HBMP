#!/usr/bin/env python3
"""Go-live gate enforcement (phase 12.2 / ../35 §3).

The staging->prod promotion is blocked unless the governance gates are green. This script consumes
the evidence artifacts produced by phase 11 + phase 12.1 and reports each gate's status:

  SECURITY   docs/compliance/security-sign-off.md      (pen-test resolved, authz green, break-glass audited)
  COMPLIANCE docs/compliance/migration-dpia.md         (migration DPIA signed)
  DR         docs/runbooks/dr-drill-report.md          (restore/DR drill passed)
  PERF       docs/PERFORMANCE-BASELINE.md              (perf baseline captured)
  MIGRATION  tools/ci/check-migration-compat.py --all  (expand/contract clean)

Modes:
  (default)         verify every artifact is PRESENT + wired; MISSING artifact => block (exit 2).
  --require-signed  additionally require each sign-off block to be SIGNED; unsigned => block (exit 1).
                    This is the mode the PROD promotion job runs — it stays red until humans sign,
                    which is the intended behaviour (../35 §3: a missing/unsigned gate blocks prod;
                    overrides need recorded steering-committee approval).

Exit: 0 all required gates green; 1 a required gate PENDING under --require-signed; 2 an artifact MISSING.
"""
from __future__ import annotations

import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


@dataclass
class Gate:
    key: str
    path: str
    # signed detector: ("row", "<label>") checks a signature-table row's cells are filled;
    # ("contains", "<regex>") checks the doc contains a token; None = informational (always green).
    signed_check: tuple[str, str] | None


GATES = [
    Gate("SECURITY", "docs/compliance/security-sign-off.md", ("row", "Security owner")),
    Gate("COMPLIANCE", "docs/compliance/migration-dpia.md", ("row", "DPO")),
    Gate("DR", "docs/runbooks/dr-drill-report.md", ("contains", r"(?i)\b(PASS|passed)\b")),
    Gate("PERF", "docs/PERFORMANCE-BASELINE.md", None),
]


def row_signed(doc: str, label: str) -> bool:
    """A signature row `| <label> | Name | Date | Decision |` is signed when at least one cell
    AFTER the label cell contains real (non-whitespace) content."""
    for line in doc.splitlines():
        if "|" not in line or label not in line:
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if cells and cells[0] == label and any(c for c in cells[1:]):
            return True
    return False


def signed(doc: str, check: tuple[str, str] | None) -> bool:
    if check is None:
        return True  # informational artifact (no human sign-off block)
    kind, arg = check
    return row_signed(doc, arg) if kind == "row" else re.search(arg, doc) is not None


def migration_gate() -> tuple[str, str]:
    try:
        subprocess.check_output([sys.executable, str(REPO / "tools/ci/check-migration-compat.py"), "--all"],
                                stderr=subprocess.STDOUT, text=True)
        return "GREEN", "expand/contract clean"
    except subprocess.CalledProcessError as e:
        return "BLOCK", e.output.strip().splitlines()[-1] if e.output else "contract op found"


def main(argv: list[str]) -> int:
    require_signed = "--require-signed" in argv
    rows: list[tuple[str, str, str]] = []
    worst = 0  # 0 ok, 1 pending, 2 missing

    for g in GATES:
        p = REPO / g.path
        if not p.exists():
            rows.append((g.key, "MISSING", g.path))
            worst = max(worst, 2)
            continue
        doc = p.read_text(encoding="utf-8")
        if signed(doc, g.signed_check):
            rows.append((g.key, "GREEN", g.path))
        else:
            rows.append((g.key, "PENDING (unsigned)", g.path))
            if require_signed:
                worst = max(worst, 1)

    mstatus, mdetail = migration_gate()
    rows.append(("MIGRATION", mstatus if mstatus == "GREEN" else "BLOCK", mdetail))
    if mstatus != "GREEN":
        worst = max(worst, 2)

    print(f"Go-live gate check ({'require-signed / PROD' if require_signed else 'artifact-presence'} mode):\n")
    for key, status, detail in rows:
        print(f"  {key:<11} {status:<20} {detail}")

    print()
    if worst == 2:
        print("BLOCKED: a required gate artifact is MISSING or a contract migration is unacknowledged.")
        return 2
    if worst == 1:
        print("BLOCKED for prod: one or more sign-offs are PENDING (unsigned). "
              "Obtain signatures or a recorded steering-committee override (../35 §3).")
        return 1
    print("All required go-live gates are GREEN." if require_signed
          else "All gate artifacts present + wired (sign-offs verified separately at the prod gate).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
