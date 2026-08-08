#!/usr/bin/env bash
# Apply the DEFERRED (contract-step) migrations that apply-migrations.sh deliberately skips.
#
# WHY THIS IS A SEPARATE SCRIPT. apply-migrations.sh globs `services/*/Infrastructure/Migrations/*.sql` at
# maxdepth 1, so anything under a `deferred/` subdirectory is committed and reviewed but never applied by a
# normal deploy. That is how expand → backfill → switch → CONTRACT is enforced structurally rather than by
# someone remembering: a contract migration physically cannot ship on the deploy that performs the switch,
# which is what collapses a dual-accept window to zero seconds and strands every token in flight.
#
# Contract migrations are NOT idempotent-by-nature the way expand/backfill are — they narrow CHECKs and drop
# roles. Read the banner at the top of each file and satisfy its preconditions before running this.
#
#   DEFERRED_FILTER  — apply only files whose path matches this substring (e.g. "radiology"). Strongly
#                      recommended: this script would otherwise apply every pending contract step at once,
#                      including ones belonging to a different, still-open rename window.
#   DRY_RUN=1        — list what would be applied and exit.
#
# Connection comes from standard libpq env vars, same as apply-migrations.sh.
set -euo pipefail
cd "$(dirname "$0")/../.."

: "${PGDATABASE:=hbmp}"
export PGDATABASE

filter="${DEFERRED_FILTER:-}"
mapfile -t files < <(find services/*/Infrastructure/Migrations/deferred -maxdepth 1 -name '*.sql' 2>/dev/null | sort)

if [ ${#files[@]} -eq 0 ]; then
  echo "No deferred migrations found."
  exit 0
fi

selected=()
for f in "${files[@]}"; do
  if [ -n "$filter" ] && [[ "$f" != *"$filter"* ]]; then continue; fi
  selected+=("$f")
done

if [ ${#selected[@]} -eq 0 ]; then
  echo "No deferred migrations match DEFERRED_FILTER='${filter}'."
  echo "Available:"; printf '  %s\n' "${files[@]}"
  exit 0
fi

if [ -z "$filter" ]; then
  echo "⚠ No DEFERRED_FILTER set — this would apply EVERY pending contract step:"
  printf '  %s\n' "${selected[@]}"
  echo "  Set DEFERRED_FILTER=<substring> to scope it, or DRY_RUN=1 to inspect."
fi

echo "==> ${#selected[@]} deferred migration(s) selected:"
printf '     - %s\n' "${selected[@]}"

if [ "${DRY_RUN:-0}" = "1" ]; then
  echo "==> DRY_RUN=1, nothing applied."
  exit 0
fi

for f in "${selected[@]}"; do
  echo "==> applying $f"
  psql -v ON_ERROR_STOP=1 -q -f "$f"
done

echo "==> Done: ${#selected[@]} deferred migration(s) applied."
echo "    Remember the CODE-side removals listed in each file's banner — a half-contracted rename is the one"
echo "    state with no working spelling."
