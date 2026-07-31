#!/usr/bin/env bash
# Domain-coverage gate. Merges every coverage.cobertura.xml under the given results dir and enforces a
# line-coverage floor over DOMAIN source — the layer CLAUDE.md requires to be unit-tested (target ≥80%).
# The enforced floor is a REGRESSION GUARD; it only ever moves UP (tools/ci/check-floor-monotonicity.py).
#
#   $1                      — results directory holding **/coverage.cobertura.xml (default ./coverage)
#   COVERAGE_MIN_DOMAIN     — minimum domain line-rate percent (default 58)
#   COVERAGE_MIN_OVERALL    — minimum OVERALL line-rate percent (default 45)
#
# 18.E1 (audit R2 Q4): overall coverage was printed and NOT gated. That is the number that falls when the
# DB-gated integration suites stop running — a *_TEST_DB variable quietly missing from CI drops hundreds of
# Api/Infrastructure lines and every remaining test still passes. Gating it means that failure is loud.
#
# 24.0 — THE ARITHMETIC WAS WRONG, AND IT IS THE ARITHMETIC THAT CHANGED. NOT THE FLOORS.
#
# This script used to walk every coverage.cobertura.xml and add up every line of every class it found.
# `dotnet test` on a solution writes ONE report per test assembly, and each report describes every
# assembly that project loaded — so libs/authz, libs/auth and each service's Domain were counted once per
# test project that referenced them (161 files appeared in more than one report; one in 27). Worse, a
# project that merely LOADS a library contributes its whole line count with almost no hits, so every test
# assembly added pushed the percentage DOWN. That is a denominator tracking the number of test projects
# rather than the amount of code, which is why phases 19–21 appeared to collapse coverage by 19 points.
#
# The proof it was wrong is arithmetic, not opinion: it reported 22,625 "domain lines" while the whole
# Domain layer contains 12,488 PHYSICAL source lines, blanks and braces included. Unique coverable lines
# cannot outnumber the lines in the files they come from.
#
# coverage-report.py merges by UNION — each physical line counted once, covered if any run covered it —
# and leaves test code out of the denominator, since measuring how much of a test suite the suite itself
# executes is a tautology. Floors are untouched at 58/45 per the phase-24 sponsor decision.
set -euo pipefail
RESULTS="${1:-./coverage}"
HERE="$(cd "$(dirname "$0")" && pwd)"

# ONE source of truth for the floors (Gate 1.3 / Gate 7.3). They used to be shell defaults here, which
# made lowering one a one-character edit that reads like a config tweak in review — the wrong shape for a
# control whose whole job is to be hard to move. They now live in coverage-floors.json, guarded by
# check-floor-monotonicity.py. The env vars still override, for a local experiment; CI passes neither.
FLOORS="$HERE/coverage-floors.json"
MIN="${COVERAGE_MIN_DOMAIN:-$(python3 -c "import json;print(json.load(open('$FLOORS'))['aggregates']['domain'])")}"
MIN_OVERALL="${COVERAGE_MIN_OVERALL:-$(python3 -c "import json;print(json.load(open('$FLOORS'))['aggregates']['overall'])")}"

python3 "$HERE/coverage-report.py" "$RESULTS" \
    --json "$RESULTS/coverage-report.json" \
    --markdown "$RESULTS/coverage-report.md" \
    --compare-naive

# Publish the per-module table on every run, pass or fail (Gate 0.3): the number has to be visible even
# when something else in the pipeline is red, because a coverage collapse hiding behind an unrelated
# failure is exactly how a month went by without anyone seeing this one.
if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -f "$RESULTS/coverage-report.md" ]]; then
  {
    echo "## Coverage by module"
    echo
    cat "$RESULTS/coverage-report.md"
  } >> "$GITHUB_STEP_SUMMARY"
fi

python3 - "$RESULTS" "$MIN" "$MIN_OVERALL" "$FLOORS" <<'PY'
import json, sys
results, minpct, minoverall, floors_path = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
report = json.load(open(f"{results}/coverage-report.json"))
t = report["totals"]
domain, overall = t["domain"], t["overall"]

print(f"coverage — overall {overall['covered']}/{overall['total']} = {overall['pct']}% "
      f"(gate >= {minoverall:.0f}%)  |  domain {domain['covered']}/{domain['total']} = {domain['pct']}% "
      f"(gate >= {minpct:.0f}%)")

if domain["total"] == 0:
    print("::error::no domain lines measured"); sys.exit(2)

failed = False
if domain["pct"] + 1e-9 < minpct:
    print(f"::error::domain coverage {domain['pct']}% is below the {minpct:.0f}% floor"); failed = True
if overall["pct"] + 1e-9 < minoverall:
    print(f"::error::overall coverage {overall['pct']}% is below the {minoverall:.0f}% floor — "
          "check that the DB-gated integration suites ran (./dotnet.sh test --with-db)"); failed = True

# 24.3 — THE PER-MODULE FLOORS ARE NOW ENFORCED, and until this loop existed they were not.
#
# coverage-floors.json says of them, in its own header: "seeded 3 points below measured, so ordinary
# run-to-run variance does not fail a build while a real regression does". Nothing failed a build. Two
# scripts read that table — check-floor-monotonicity.py, which stops it being LOWERED, and raise-floors.py,
# which proposes RAISES — and between them they kept a number moving upward that no gate ever compared
# anything against. A module could fall from 90% to 3% and the aggregate would absorb it: overall is 35,654
# lines, so a whole service's Api layer going dark moves it by a couple of points.
#
# That is the same shape as every other finding in this phase: a control that reads as enforcement, is
# described as enforcement, and is not enforcement. It is enforced now, against the same table, with the
# same one-way ratchet — a floor still only moves down through check-floor-monotonicity.py and an ADR.
floors = json.load(open(floors_path)).get("modules") or {}
measured = {f"{m['module']}:{m['layer']}": m for m in report["modules"] if m["layer"] != "Tests"}

below = []
vanished = []
for key, floor in sorted(floors.items()):
    m = measured.get(key)
    if m is None:
        # A floor whose module produced no coverage at all. Usually the module was renamed or deleted —
        # but it is also exactly what a test project silently dropping out of the run looks like, so it is
        # reported rather than skipped.
        vanished.append(key)
        continue
    if m["pct"] + 1e-9 < floor:
        below.append(f"{key}: {m['pct']}% ({m['covered']}/{m['total']}) is below its {floor}% floor")

for line in below:
    print(f"::error::{line}")
for key in vanished:
    print(f"::error::{key} has a coverage floor but produced no measured lines — the module was renamed or "
          f"deleted (update tools/ci/coverage-floors.json), or its test project stopped running")
if below or vanished:
    print("per-module floors move DOWN only through tools/ci/check-floor-monotonicity.py, which requires an "
          "ADR that names the module. Write the test instead.")
    failed = True
else:
    print(f"per-module floors — {len(floors)} modules, all at or above their floor")

if failed:
    sys.exit(1)
PY
