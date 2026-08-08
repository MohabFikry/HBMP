#!/usr/bin/env python3
"""Phase 24 Gate 4.4 — a skipped test reports green having proven nothing.

The suite ran for months with ~100 DB-gated tests skipping in CI because nothing exported their
connection strings: the concurrency proofs, the RLS isolation suites and the break-glass lifecycle
among them. Every one reported as a skip, and a skip is silent. 18.E1 fixed the exporter for the
services; 24.4 found the last one — the migration tool's sink test, skipping for exactly the same
reason four months later, because nothing said the number should be zero.

So this reads the TRX the test step already produces and fails when the skip count exceeds the
allow-list. The allow-list carries a REASON per test, because "why is this not running?" is the
question a green build cannot answer for you.

Usage:
  check-skipped-tests.py <results-dir>   scan *.trx beneath the directory
  check-skipped-tests.py --selftest      prove the parser and the gate on fixtures
"""
from __future__ import annotations

import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# Tests permitted to skip, each with the reason it cannot run and what would let it.
# An entry here is reviewed like a coverage exclusion: it is a hole in the evidence, not a preference.
ALLOWED: dict[str, str] = {}


def skipped(trx: Path) -> list[tuple[str, str]]:
    """(test name, message) for every non-executed result in one TRX."""
    out: list[tuple[str, str]] = []
    root = ET.parse(trx).getroot()
    for r in root.iterfind(".//t:UnitTestResult", NS):
        # "NotExecuted" is what xUnit reports for both [Fact(Skip=...)] and a SkippableFact that
        # answered Skip.If(...) at runtime. Both are the same thing to a reader of the summary.
        if r.get("outcome") != "NotExecuted":
            continue
        name = r.get("testName") or "(unnamed)"
        msg = ""
        node = r.find(".//t:Message", NS)
        if node is not None and node.text:
            msg = " ".join(node.text.split())
        out.append((name, msg))
    return out


def scan(results_dir: Path) -> list[tuple[str, str]]:
    found: list[tuple[str, str]] = []
    for trx in sorted(results_dir.rglob("*.trx")):
        found.extend(skipped(trx))
    return found


def report(found: list[tuple[str, str]]) -> int:
    unlisted = [(n, m) for n, m in found if n.split("(")[0] not in ALLOWED]
    print(f"skipped-test gate: {len(found)} skipped, {len(unlisted)} without an allow-list entry")
    for name, msg in found:
        mark = "ALLOWED" if name.split("(")[0] in ALLOWED else "UNLISTED"
        print(f"  [{mark}] {name}" + (f" — {msg}" if msg else ""))
    if unlisted:
        print()
        print("::error::a skipped test reports green having proven nothing. Either make it run (the usual")
        print("cause is a *_TEST_DB variable nobody exports — see tools/ci/print-test-db-env.sh), or add it")
        print("to ALLOWED in this script with the reason it cannot run.")
        return 1
    return 0


# ---- selftest ------------------------------------------------------------------------------------

TRX = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="A.Passes" outcome="Passed" />
    <UnitTestResult testName="{skipped_name}" outcome="NotExecuted">
      <Output><Message>MIGRATION_TEST_DB not set</Message></Output>
    </UnitTestResult>
  </Results>
</TestRun>
"""


def selftest() -> int:
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        d = Path(tmp)
        (d / "a.trx").write_text(TRX.format(skipped_name="B.Skips"), encoding="utf-8")

        found = scan(d)
        if found != [("B.Skips", "MIGRATION_TEST_DB not set")]:
            failures.append(f"parser did not find the skip: {found}")

        # An unlisted skip must fail...
        ALLOWED.clear()
        if report(found) != 1:
            failures.append("an unlisted skip did not fail the gate")

        # ...and a listed one must pass, so the allow-list is usable at all.
        ALLOWED["B.Skips"] = "selftest"
        if report(found) != 0:
            failures.append("an allow-listed skip failed the gate")
        ALLOWED.clear()

        # A run with no skips is the expected steady state.
        (d / "b.trx").unlink(missing_ok=True)
        (d / "a.trx").write_text(
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '<Results><UnitTestResult testName="A.Passes" outcome="Passed" /></Results></TestRun>',
            encoding="utf-8")
        if report(scan(d)) != 0:
            failures.append("a clean run was reported as a failure")

    if failures:
        for f in failures:
            print(f"SELFTEST FAIL: {f}")
        return 1
    print("check-skipped-tests selftest: OK")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    if len(argv) < 2:
        print("usage: check-skipped-tests.py <results-dir> | --selftest", file=sys.stderr)
        return 2
    results = Path(argv[1])
    if not results.exists():
        print(f"::error::results directory {results} does not exist — the test step produced no TRX, so this")
        print("gate cannot say anything about what ran. Failing rather than reporting it clean.")
        return 1
    return report(scan(results))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
