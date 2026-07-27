#!/usr/bin/env bash
# Domain-coverage gate. Aggregates every coverage.cobertura.xml under the given results dir and enforces a
# line-coverage floor over DOMAIN source (paths containing `/Domain/`) — the layer CLAUDE.md requires to be
# unit-tested (target ≥80%). The enforced floor is a REGRESSION GUARD set below the current measured value;
# raise COVERAGE_MIN_DOMAIN over time toward the 80% target. Overall coverage is printed for visibility but
# not gated (Api/Infrastructure lines are exercised by the DB-gated integration suites).
#
#   $1                      — results directory holding **/coverage.cobertura.xml (default ./coverage)
#   COVERAGE_MIN_DOMAIN     — minimum domain line-rate percent (default 58)
#   COVERAGE_MIN_OVERALL    — minimum OVERALL line-rate percent (default 45)
#
# 18.E1 (audit R2 Q4): overall coverage was printed and NOT gated. That is the number that falls when the
# DB-gated integration suites stop running — a *_TEST_DB variable quietly missing from CI drops hundreds of
# Api/Infrastructure lines and every remaining test still passes. Gating it means that failure is loud.
set -euo pipefail
RESULTS="${1:-./coverage}"
MIN="${COVERAGE_MIN_DOMAIN:-58}"
MIN_OVERALL="${COVERAGE_MIN_OVERALL:-45}"

python3 - "$RESULTS" "$MIN" "$MIN_OVERALL" <<'PY'
import glob, sys, xml.etree.ElementTree as ET
results, minpct, minoverall = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
cov=val=dcov=dval=0
files = glob.glob(f"{results}/**/coverage.cobertura.xml", recursive=True)
if not files:
    print(f"::error::no coverage.cobertura.xml found under {results}"); sys.exit(2)
for f in files:
    for cls in ET.parse(f).getroot().iter('class'):
        fn = cls.get('filename','').replace('\\','/')
        lines = cls.find('lines')
        if lines is None: continue
        for ln in lines.findall('line'):
            hit = int(ln.get('hits','0')) > 0
            val += 1; cov += hit
            if '/Domain/' in fn:
                dval += 1; dcov += hit
overall = cov/val*100 if val else 0
domain  = dcov/dval*100 if dval else 0
print(f"coverage — overall {cov}/{val} = {overall:.1f}% (gate ≥ {minoverall:.0f}%)  |  "
      f"domain {dcov}/{dval} = {domain:.1f}% (gate ≥ {minpct:.0f}%)")
if dval == 0:
    print("::error::no domain lines measured"); sys.exit(2)
failed = False
if domain + 1e-9 < minpct:
    print(f"::error::domain coverage {domain:.1f}% is below the {minpct:.0f}% floor"); failed = True
if overall + 1e-9 < minoverall:
    print(f"::error::overall coverage {overall:.1f}% is below the {minoverall:.0f}% floor — "
          "this usually means the DB-gated integration suites did not run"); failed = True
if failed:
    sys.exit(1)
print("coverage gate: PASS")
PY
