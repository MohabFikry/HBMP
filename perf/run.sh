#!/usr/bin/env bash
# Convenience wrapper for the Mersal HBMP perf suite (Phase 11.1).
# Usage:
#   perf/run.sh all                 # run the whole suite, publish a JSON summary per script
#   perf/run.sh k6/01-eligibility.js # run one script
# Env: BASE_URL, OIDC_TOKEN_URL/OIDC_CLIENT_ID/OIDC_CLIENT_SECRET (or BEARER for smoke),
#      SMOKE=1 for a short laptop/CI-smoke profile.
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 not found — install from https://k6.io/docs/get-started/installation/" >&2
  exit 127
fi

RESULTS_DIR="${RESULTS_DIR:-results}"
mkdir -p "$RESULTS_DIR"

run_one() {
  local script="$1"
  local name
  name="$(basename "$script" .js)"
  echo "▶ $script"
  # --summary-export makes the pass/fail (thresholds) machine-readable for CI gating.
  k6 run --summary-export "$RESULTS_DIR/${name}.json" "$script"
}

if [[ "${1:-all}" == "all" ]]; then
  rc=0
  for s in k6/01-eligibility.js k6/02-consume.js k6/03-worklists.js k6/04-dashboards.js k6/05-mixed-soak.js; do
    run_one "$s" || rc=1
  done
  echo "Summaries in $RESULTS_DIR/. Any threshold miss ⇒ non-zero exit (CI gate)."
  exit $rc
else
  run_one "$1"
fi
