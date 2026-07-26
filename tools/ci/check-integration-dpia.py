#!/usr/bin/env python3
"""Integration DPIA / data-sharing gate (phase 13.3 / ../20 §6).

No external integration may be marked `Enabled` in any environment unless BOTH a signed-off DPIA and a
data-sharing agreement reference exist for it. This is the CI half of the gate (the runtime half is
`DpiaGate.CanEnable` in the interop registry + a DB CHECK on interop.integration_partner). It parses the
committed register (docs/compliance/integration-register.md) and fails the build if any Enabled partner is
missing either artifact.

  every ENABLED partner  =>  DPIA == SignedOff  AND  a non-empty data-sharing agreement reference

Modes:
  (default)   enforce the gate over the register; a violation exits 1, a malformed/absent register exits 2.
  --selftest  run the enforcement logic over synthetic rows (compliant + non-compliant) — exits 0 on success.

Exit: 0 gate green; 1 an Enabled partner is missing DPIA/agreement; 2 the register is missing/unparseable.
"""
from __future__ import annotations

import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
REGISTER = REPO / "docs" / "compliance" / "integration-register.md"

EMPTY_REF = {"", "—", "-", "n/a", "none", "tbd"}


def parse_rows(md: str) -> list[dict[str, str]]:
    """Parse the register's partner table into rows keyed by the header cells."""
    rows: list[dict[str, str]] = []
    header: list[str] | None = None
    for line in md.splitlines():
        s = line.strip()
        if not (s.startswith("|") and s.endswith("|")):
            header = None
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        if all(set(c) <= set("-: ") for c in cells):  # the |---|---| separator
            continue
        if header is None:
            header = [c.lower() for c in cells]
            continue
        if len(cells) == len(header):
            rows.append(dict(zip(header, cells)))
    # Keep only rows that look like partner rows (have a status column).
    return [r for r in rows if "status" in r and "partner id" in r]


def violations(rows: list[dict[str, str]]) -> list[str]:
    out: list[str] = []
    for r in rows:
        if r.get("status", "").lower() != "enabled":
            continue
        pid = r.get("partner id", "?")
        if r.get("dpia", "").lower() != "signedoff":
            out.append(f"{pid}: Enabled but DPIA is '{r.get('dpia', '')}' (must be SignedOff)")
        if r.get("data-sharing agreement", "").strip().lower() in EMPTY_REF:
            out.append(f"{pid}: Enabled but has no data-sharing agreement reference")
    return out


def selftest() -> int:
    rows = [
        {"partner id": "ok", "status": "Enabled", "dpia": "SignedOff", "data-sharing agreement": "DSA-1"},
        {"partner id": "bad-dpia", "status": "Enabled", "dpia": "InProgress", "data-sharing agreement": "DSA-2"},
        {"partner id": "bad-dsa", "status": "Enabled", "dpia": "SignedOff", "data-sharing agreement": "—"},
        {"partner id": "off", "status": "Disabled", "dpia": "NotStarted", "data-sharing agreement": "—"},
    ]
    v = violations(rows)
    assert any("bad-dpia" in x for x in v), "should flag a missing DPIA"
    assert any("bad-dsa" in x for x in v), "should flag a missing agreement"
    assert not any("ok:" in x for x in v), "compliant enabled partner must pass"
    assert not any("off:" in x for x in v), "disabled partner is exempt"
    print("SELFTEST OK — DPIA gate flags enabled partners missing DPIA/agreement; passes compliant + disabled.")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    if not REGISTER.exists():
        print(f"MISSING: {REGISTER.relative_to(REPO)} — the integration register is required.", file=sys.stderr)
        return 2
    rows = parse_rows(REGISTER.read_text(encoding="utf-8"))
    if not rows:
        print("MISSING: no partner rows parsed from the register.", file=sys.stderr)
        return 2
    v = violations(rows)
    if v:
        print("DPIA GATE BLOCKED — an external integration is Enabled without a DPIA + data-sharing agreement:", file=sys.stderr)
        for x in v:
            print(f"  - {x}", file=sys.stderr)
        return 1
    enabled = sum(1 for r in rows if r.get("status", "").lower() == "enabled")
    print(f"OK — DPIA gate green: {len(rows)} partner(s) registered, {enabled} Enabled, all compliant.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
