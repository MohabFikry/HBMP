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
for s in ADMIN APPROVALS CALLCENTRE CASE CLAIMS ELIGIBILITY EMR FINANCE INTEROP NOTIFICATION ORDERS PHARMACY POLICY REPORTING; do
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
