#!/usr/bin/env bash
# OpenAPI drift gate — do the committed specs in docs/api/ still describe the running services?
#
# WHY THIS FILE EXISTS, given the gate already ran in CI.
#
# It did, and it was blocking, and it had been RED for a day while every local check reported green. The
# drift gate lived only inside .github/workflows/backend-ci.yml, so the only way to run it was to push and
# wait ~8 minutes for a scoreboard. Nobody does that before a commit, so nobody ran it — and "all gates
# green" got said out loud on the strength of the ten gates that DID have a local entry point.
#
# A gate you cannot run locally is a gate you find out about afterwards. This is the local half, sharing
# generate-openapi.sh with CI rather than restating it, so the two cannot answer differently.
#
#   tools/ci/check-openapi-drift.sh                    # generate to a temp dir, report drift, exit 1 if any
#   tools/ci/check-openapi-drift.sh --fix              # regenerate docs/api/ in place, then say what changed
#   tools/ci/check-openapi-drift.sh <generated-dir>    # compare specs already generated (CI: reuses artifacts/)
#
#   DOTNET — dotnet launcher (default `dotnet`; locally pass DOTNET=./dotnet.sh)
#
# Prereq: the solution builds. Generation is offline — DB access is lazy, so dummy connection strings
# satisfy DI and nothing connects.
set -euo pipefail
cd "$(dirname "$0")/../.."

FIX=0
if [ "${1:-}" = "--fix" ]; then FIX=1; shift; fi

COMMITTED="docs/api"

# CI has already generated the specs (it uploads them as an artefact), so it passes that directory in
# rather than paying for a second generation. Locally there is nothing to reuse, so we generate.
if [ -n "${1:-}" ]; then
  GENERATED="$1"
  [ -d "$GENERATED" ] || { echo "::error::$GENERATED is not a directory"; exit 1; }
else
  GENERATED="$(mktemp -d)"
  trap 'rm -rf "$GENERATED"' EXIT
  DOTNET="${DOTNET:-dotnet}" bash tools/ci/generate-openapi.sh "$GENERATED" >/dev/null
fi

# REFUSE TO COMPARE NOTHING — the same rule the CI copy learned in 24.1. A generation that produced zero
# specs would otherwise diff clean against everything and report the contract verified, so the louder the
# upstream failure the greener this gate gets.
n=$(find "$GENERATED" -maxdepth 1 -name '*.json' | wc -l)
if [ "$n" -eq 0 ]; then
  echo "::error::no specs were generated — this gate cannot say anything about the committed contract."
  echo "Failing rather than reporting it clean. Check that the solution builds and the local tool is restored."
  exit 1
fi

echo "openapi drift gate: comparing $n generated spec(s) against $COMMITTED"

drifted=()
for gen in "$GENERATED"/*.json; do
  name="$(basename "$gen")"
  if [ ! -f "$COMMITTED/$name" ]; then
    drifted+=("$name — NOT COMMITTED: a whole service's contract is missing from $COMMITTED")
  elif ! diff -q "$gen" "$COMMITTED/$name" >/dev/null; then
    # `|| true` on BOTH: diff exits 1 when files differ, and under `set -e` + `pipefail` that non-zero
    # propagates out of the command substitution and kills the script — silently, BEFORE any of the
    # reporting below runs. The first version of this file did exactly that: exit 1, no output, no way to
    # tell a drifted spec from a broken gate. A gate whose failure cannot be read is not much better than
    # one that does not run.
    changed="$( { diff "$COMMITTED/$name" "$gen" || true; } | grep -c '^[<>]' || true )"
    drifted+=("$name — $changed changed line(s)")
  else
    continue
  fi
  [ "$FIX" -eq 1 ] && cp "$gen" "$COMMITTED/$name"
done

if [ "${#drifted[@]}" -eq 0 ]; then
  echo "✓ every committed spec matches the running services"
  exit 0
fi

for d in "${drifted[@]}"; do echo "  - $d"; done

if [ "$FIX" -eq 1 ]; then
  echo "regenerated ${#drifted[@]} spec(s) in $COMMITTED — review the diff and commit it."
  exit 0
fi

echo "::error::${#drifted[@]} committed OpenAPI spec(s) are stale. The spec is the contract (CLAUDE.md)."
echo "Run: DOTNET=./dotnet.sh tools/ci/check-openapi-drift.sh --fix   then review and commit."
exit 1
