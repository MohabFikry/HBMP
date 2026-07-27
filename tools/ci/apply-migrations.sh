#!/usr/bin/env bash
# Apply every service's hand-authored SQL migrations to a target Postgres and provision the hbmp_app
# NOBYPASSRLS runtime role, so the env-gated *_TEST_DB integration + *_TEST_DB_APP RLS tests actually
# run. Used by .github/workflows/backend-ci.yml against a clean postgres service, and reusable locally
# against a scratch DB.
#
# Migrations are schema-per-service and otherwise independent; the only shared object is the hbmp_app
# role, which we create FIRST (before the RLS migrations that GRANT to it). Files apply in filename
# order per service (zero-padded numeric prefixes + 9000_outbox.sql sort correctly).
#
# Connection comes from standard libpq env vars (PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE); the user
# must own/superuse the target so CREATE ROLE / CREATE EXTENSION succeed.
#
#   HBMP_APP_PASSWORD   — password to set on the hbmp_app role (default: a dev value).
#   HBMP_AUDIT_PASSWORD — password for hbmp_audit, audit-service's own login role (18.B2).
set -euo pipefail
cd "$(dirname "$0")/../.."

: "${PGDATABASE:=hbmp}"
: "${HBMP_APP_PASSWORD:=app_dev_ci_password}"
: "${HBMP_AUDIT_PASSWORD:=audit_dev_ci_password}"
export PGDATABASE

run() { psql -v ON_ERROR_STOP=1 -q "$@"; }

echo "==> Provisioning hbmp_app runtime role (idempotent, NOBYPASSRLS)…"
run -c "DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_app') THEN
    CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END \$\$;"
run -c "ALTER ROLE hbmp_app PASSWORD '${HBMP_APP_PASSWORD}';"

# 18.B2 — audit-service gets its own login role rather than hbmp_app, so the twenty services that share
# hbmp_app cannot read the audit trail. audit 0002 grants it membership in hbmp_audit_writer.
echo "==> Provisioning hbmp_audit runtime role (idempotent, NOBYPASSRLS)…"
run -c "DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_audit') THEN
    CREATE ROLE hbmp_audit LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END \$\$;"
run -c "ALTER ROLE hbmp_audit PASSWORD '${HBMP_AUDIT_PASSWORD}';"

total=0
for mig_dir in services/*/Infrastructure/Migrations; do
  [ -d "$mig_dir" ] || continue
  svc=$(basename "$(dirname "$(dirname "$mig_dir")")")
  mapfile -t files < <(find "$mig_dir" -maxdepth 1 -name '*.sql' | sort)
  [ ${#files[@]} -eq 0 ] && continue
  echo "==> [$svc] applying ${#files[@]} migration(s)…"
  for f in "${files[@]}"; do
    echo "     - $(basename "$f")"
    run -f "$f"
    total=$((total + 1))
  done
done

# The migration toolkit (phase 12.1) owns the `migration` schema (staging/prod onboarding); it lives
# under tools/, not services/, so apply it explicitly.
if [ -d tools/migration/Migrations ]; then
  mapfile -t mfiles < <(find tools/migration/Migrations -maxdepth 1 -name '*.sql' | sort)
  if [ ${#mfiles[@]} -gt 0 ]; then
    echo "==> [migration-toolkit] applying ${#mfiles[@]} migration(s)…"
    for f in "${mfiles[@]}"; do
      echo "     - $(basename "$f")"
      run -f "$f"
      total=$((total + 1))
    done
  fi
fi

echo "==> Done: $total migration file(s) applied across all services."
