#!/usr/bin/env bash
# Generate every service's OpenAPI (Swagger) document offline via the Swashbuckle CLI, and FAIL if any
# service's spec cannot be produced. This catches broken Swagger config, duplicate route templates, and
# bad annotations at build time — without running the services or a database (DB access is lazy, so dummy
# connection strings satisfy DI registration and nothing actually connects).
#
# Prereq: the solution is built (Release) and the local tool is restored (`dotnet tool restore`).
#   $1        — output dir for the generated specs (default artifacts/openapi)
#   DOTNET    — dotnet launcher (default `dotnet`; locally pass DOTNET=./dotnet.sh)
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT="${1:-artifacts/openapi}"
DOTNET="${DOTNET:-dotnet}"
mkdir -p "$OUT"

# DI reads ConnectionStrings:<Key> (throws if absent) + Auth:*. Most services connect to the DB lazily so a
# dummy value suffices; a few (e.g. audit) migrate at startup and need a REAL connection — so any
# ConnectionStrings__<Key> already exported by the caller (CI, which has the migrated Postgres) is kept.
dummy="Host=localhost;Port=5432;Database=placeholder;Username=placeholder;Password=placeholder"
# 18.E1 (audit R2 Q2): Identity and Interop added. Both were missing, so neither had an OpenAPI gate —
# identity-service being the one that mints every token on the platform.
for k in Admin Approvals Audit CallCentre Case Claims Document Eligibility Emr Finance \
         Identity Interop MasterData Notification Orders Patient Pharmacy Policy Provider Reporting; do
  var="ConnectionStrings__${k}"
  [ -n "${!var:-}" ] || export "${var}=${dummy}"
done
# identity-service fails fast without these (18.B1) — dummies satisfy DI without minting anything.
export Issuer__ServiceClientSecret="${Issuer__ServiceClientSecret:-openapi-generation-only}"
export Issuer__SeedDemoUsers="false"
export Auth__Authority="http://localhost:8080/realms/mersal"
export Auth__Audience="hbmp-api"
export Auth__RequireHttpsMetadata="false"
export OTEL_SDK_DISABLED="true"
export ASPNETCORE_ENVIRONMENT="Development"

fail=0
count=0
for api in services/*/Api; do
  svc=$(basename "$(dirname "$api")")
  grep -q AddSwaggerGen "$api"/*.cs 2>/dev/null || continue
  dll=$(ls "$api"/bin/Release/net8.0/Mersal.*.Api.dll 2>/dev/null | head -1)
  if [ -z "$dll" ]; then
    echo "::error::$svc — built Api dll not found (build the solution in Release first)"; fail=1; continue
  fi
  if $DOTNET swagger tofile --output "$OUT/$svc.json" "$dll" v1 >/dev/null 2>"$OUT/$svc.err"; then
    paths=$(python3 -c "import json,sys;print(len(json.load(open('$OUT/$svc.json')).get('paths',{})))" 2>/dev/null || echo "?")
    printf '  ok   %-14s %s path(s)\n' "$svc" "$paths"; count=$((count + 1)); rm -f "$OUT/$svc.err"
  else
    echo "::error::$svc — OpenAPI generation failed:"; tail -6 "$OUT/$svc.err"; fail=1
  fi
done
echo "==> Generated $count service OpenAPI spec(s) into $OUT."
exit $fail
