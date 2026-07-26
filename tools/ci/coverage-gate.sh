#!/usr/bin/env bash
# Domain-coverage gate. Aggregates every coverage.cobertura.xml under the given results dir and enforces a
# line-coverage floor over DOMAIN source (paths containing `/Domain/`) — the layer CLAUDE.md requires to be
# unit-tested (target ≥80%). The enforced floor is a REGRESSION GUARD set below the current measured value;
# raise COVERAGE_MIN_DOMAIN over time toward the 80% target. Overall coverage is printed for visibility but
# not gated (Api/Infrastructure lines are exercised by the DB-gated integration suites).
#
#   $1                      — results directory holding **/coverage.cobertura.xml (default ./coverage)
#   COVERAGE_MIN_DOMAIN     — minimum domain line-rate percent (default 55)
set -euo pipefail
RESULTS="${1:-./coverage}"
MIN="${COVERAGE_MIN_DOMAIN:-55}"

python3 - "$RESULTS" "$MIN" <<'PY'
import glob, sys, xml.etree.ElementTree as ET
results, minpct = sys.argv[1], float(sys.argv[2])
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
print(f"coverage — overall {cov}/{val} = {overall:.1f}%  |  domain {dcov}/{dval} = {domain:.1f}%  (gate: domain ≥ {minpct:.0f}%)")
if dval == 0:
    print("::error::no domain lines measured"); sys.exit(2)
if domain + 1e-9 < minpct:
    print(f"::error::domain coverage {domain:.1f}% is below the {minpct:.0f}% floor"); sys.exit(1)
print("coverage gate: PASS")
PY
