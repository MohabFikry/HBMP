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
#   HBMP_APP_PASSWORD   — password to set on the hbmp_app role. Applied ONLY when the role is created,
#                         or when HBMP_FORCE_ROLE_PASSWORDS=1 is also set (see below).
#   HBMP_AUDIT_PASSWORD — same, for hbmp_audit (audit-service's own login role, 18.B2).
#   HBMP_FORCE_ROLE_PASSWORDS=1 — reset the passwords of roles that ALREADY EXIST. Needed on a fresh CI
#                         database only in the rare case the roles predate the run; NOT wanted against a
#                         database a stack is running against.
#
# WHY THAT IS OPT-IN: the script used to ALTER both passwords unconditionally, to the built-in dev default
# when the env vars were unset. Run against the live Tier 1 database — which is a reasonable thing to want,
# since it is also how you apply a new migration locally — it silently changed the credentials out from under
# every running service, and the only symptom was every container logging "password authentication failed"
# until someone thought to connect the two events. Creating a role still sets its password, because a role
# with no password cannot be logged into and there is nothing to preserve.
set -euo pipefail
cd "$(dirname "$0")/../.."

: "${PGDATABASE:=hbmp}"
: "${HBMP_APP_PASSWORD:=app_dev_ci_password}"
: "${HBMP_AUDIT_PASSWORD:=audit_dev_ci_password}"
export PGDATABASE

run() { psql -v ON_ERROR_STOP=1 -q "$@"; }

# Set a login role's password only when it is safe to: on a role we just created (it has none yet), or when
# the caller explicitly asks. Never silently, because the credential may be in use by a running stack.
ensure_role_password() {
  local role="$1" password="$2"
  if [ "${created_role:-0}" = "1" ] || [ "${HBMP_FORCE_ROLE_PASSWORDS:-0}" = "1" ]; then
    run -c "ALTER ROLE ${role} PASSWORD '${password}';"
    echo "    - ${role}: password set"
  else
    echo "    - ${role}: exists, password left as-is (HBMP_FORCE_ROLE_PASSWORDS=1 to reset)"
  fi
}

echo "==> Provisioning hbmp_app runtime role (idempotent, NOBYPASSRLS)…"
created_role=$(psql -tAc "SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_app') THEN 0 ELSE 1 END")
run -c "DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_app') THEN
    CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END \$\$;"
ensure_role_password hbmp_app "${HBMP_APP_PASSWORD}"

# 18.B2 — audit-service gets its own login role rather than hbmp_app, so the twenty services that share
# hbmp_app cannot read the audit trail. audit 0002 grants it membership in hbmp_audit_writer.
echo "==> Provisioning hbmp_audit runtime role (idempotent, NOBYPASSRLS)…"
created_role=$(psql -tAc "SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_audit') THEN 0 ELSE 1 END")
run -c "DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='hbmp_audit') THEN
    CREATE ROLE hbmp_audit LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END \$\$;"
ensure_role_password hbmp_audit "${HBMP_AUDIT_PASSWORD}"

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
