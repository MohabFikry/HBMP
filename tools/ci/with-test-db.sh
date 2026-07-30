#!/usr/bin/env bash
# Export every *_TEST_DB / *_TEST_DB_OWNER / *_TEST_DB_APP variable for the LOCAL Compose Postgres, then exec
# whatever was passed. Meant to be sourced by ./dotnet.sh --with-db, or run directly:
#
#   tools/ci/with-test-db.sh ./dotnet.sh test HbmpPlatform.sln
#
# WHY THIS EXISTS. Roughly a hundred DB-gated tests answer `Skip.If(Environment.GetEnvironmentVariable(...)
# is null)`, so a plain `dotnet test` silently skips every integration and RLS suite in the repo — the
# concurrency proofs, the RLS isolation checks, the break-glass lifecycle. They report green by not running.
# CI wires them through tools/ci/print-test-db-env.sh into $GITHUB_ENV; locally, nothing did, and the
# connection details had to be reconstructed from compose.yaml and .env every time.
#
# This is the local half of that CI line, and it deliberately shares print-test-db-env.sh rather than
# restating the variable list: a service that gains a suite gets it in both places or neither.
#
# Secrets come from infra/compose/.env (gitignored) and are never printed. Override any of PGHOST / PGPORT /
# PGDATABASE / OWNER_USER / APP_USER by exporting it first — the defaults target the Compose stack.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_file="${COMPOSE_ENV_FILE:-$repo_root/infra/compose/.env}"

if [[ ! -f "$env_file" ]]; then
  echo "with-test-db: no $env_file — copy infra/compose/.env.example and fill it in, or set COMPOSE_ENV_FILE." >&2
  exit 1
fi

# `set -a` exports everything the file defines; the subshell-free source is deliberate — these must land in
# THIS process so the exec below inherits them.
set -a
# shellcheck disable=SC1090
. "$env_file"
set +a

# 55432, not 5432: compose publishes Postgres on 55432 because a local PG16 usually owns the default port
# (infra/compose/compose.yaml). `hbmp` is the application database; POSTGRES_DB in .env is the bootstrap one.
: "${PGHOST:=localhost}"
: "${PGPORT:=55432}"
: "${PGDATABASE:=hbmp}"
: "${OWNER_USER:=${POSTGRES_USER:-hbmp}}"
: "${OWNER_PASSWORD:=${POSTGRES_PASSWORD:-}}"
: "${APP_USER:=hbmp_app}"
: "${APP_PASSWORD:=${HBMP_APP_PASSWORD:-}}"
export PGHOST PGPORT PGDATABASE OWNER_USER OWNER_PASSWORD APP_USER APP_PASSWORD

if [[ -z "$OWNER_PASSWORD" || -z "$APP_PASSWORD" ]]; then
  echo "with-test-db: $env_file has no POSTGRES_PASSWORD and/or HBMP_APP_PASSWORD." >&2
  exit 1
fi

# Fail here rather than 40 minutes into a run that skipped everything. A reachability check is cheap and the
# failure it prevents — a green suite that tested nothing — is the one this script exists to stop.
if ! timeout 10 bash -c "</dev/tcp/$PGHOST/$PGPORT" 2>/dev/null; then
  echo "with-test-db: nothing is listening on $PGHOST:$PGPORT." >&2
  echo "              start it with:  docker compose -f infra/compose/compose.yaml up -d postgres" >&2
  exit 1
fi

# The single source of truth for WHICH variables exist. Values hold passwords, so this is read line by line
# into the environment and never echoed.
while IFS='=' read -r key value; do
  [[ -n "$key" ]] && export "$key=$value"
done < <("$repo_root/tools/ci/print-test-db-env.sh")

echo "with-test-db: $PGDATABASE on $PGHOST:$PGPORT as $OWNER_USER (app role $APP_USER) — DB-gated suites will RUN." >&2

[[ $# -gt 0 ]] || { echo "with-test-db: nothing to run." >&2; exit 2; }
exec "$@"
