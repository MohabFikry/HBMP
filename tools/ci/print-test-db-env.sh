#!/usr/bin/env bash
# Emit KEY=VALUE lines for every env-gated DB test's connection string, so a CI job with a migrated hbmp
# DB runs the integration + RLS suites instead of skipping them. Two roles: the owner/superuser conn
# (integration suites + *_TEST_DB_OWNER) and the hbmp_app NOBYPASSRLS conn (*_TEST_DB_APP, the role the
# RLS isolation tests exercise). Redirect into "$GITHUB_ENV".
#
#   PGHOST PGPORT PGDATABASE — target DB (defaults localhost/5432/hbmp)
#   OWNER_USER OWNER_PASSWORD — schema owner / superuser (default user hbmp)
#   APP_USER   APP_PASSWORD   — hbmp_app NOBYPASSRLS role
set -euo pipefail
: "${PGHOST:=localhost}"; : "${PGPORT:=5432}"; : "${PGDATABASE:=hbmp}"
: "${OWNER_USER:=hbmp}"; : "${OWNER_PASSWORD:?OWNER_PASSWORD required}"
: "${APP_USER:=hbmp_app}"; : "${APP_PASSWORD:?APP_PASSWORD required}"

owner="Host=${PGHOST};Port=${PGPORT};Database=${PGDATABASE};Username=${OWNER_USER};Password=${OWNER_PASSWORD}"
app="Host=${PGHOST};Port=${PGPORT};Database=${PGDATABASE};Username=${APP_USER};Password=${APP_PASSWORD}"

# Single-conn integration suites (need the schema owner / superuser).
# 18.E1 (audit R2 Q2): IDENTITY added. The newest and most security-critical service — it mints every
# token on the platform — had 12 DB-gated tests that SKIPPED in CI because nothing exported its variable.
# 24.3 (Gate 3): MASTERDATA added. It serves the fail-closed validation contracts orders, emr and pharmacy
# refuse on — /icd-codes/{code}/exists, /drug-interactions/check-by-ids, /examination-types/{id} — and had no
# DB-gated test at all, because nothing exported its variable and so nobody wrote one.
# 24.3 (Gate 3): PATIENT added too. It had the two-role RLS pair below but no single-conn variable, so its
# endpoint suite — the 18.B3 read/write split, which is enforced in the Api layer and nowhere else — could
# not run anywhere.
for s in ADMIN APPROVALS CALLCENTRE CASE CLAIMS ELIGIBILITY EMR FINANCE IDENTITY INTEROP MASTERDATA NOTIFICATION ORDERS PATIENT PHARMACY POLICY REPORTING; do
  echo "${s}_TEST_DB=${owner}"
done
# Two-role RLS isolation suites (owner seeds/cleans; hbmp_app is the role under test).
# 18.B2 added ADMIN, CALLCENTRE, CLAIMS and INTEROP — the four services that gained a binder + a
# fail-closed policy set in the same commit their connection string left the superuser.
for s in ADMIN APPROVALS CALLCENTRE CASE CLAIMS DOCUMENT ELIGIBILITY EMR FINANCE INTEROP NOTIFICATION ORDERS PATIENT PHARMACY POLICY PROVIDER REPORTING; do
  echo "${s}_TEST_DB_OWNER=${owner}"
  echo "${s}_TEST_DB_APP=${app}"
done
# Shared events-lib outbox durability suite.
echo "EVENTS_TEST_DB=${owner}"
# 24.4 — the migration tool's sink. Its idempotency + rollback-by-batch proof was the ONLY skipped test in
# the whole suite, and it skipped for the same reason identity's twelve did in 18.E1: nothing exported its
# variable. A permanently skipped test is worse than a missing one, because it reports green. The sink
# creates its own schema (EnsureSchemaAsync) and cleans up the rows it writes, so the shared CI database is
# the right target.
echo "MIGRATION_TEST_DB=${owner}"
