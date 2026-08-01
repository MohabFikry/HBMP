#!/usr/bin/env bash
# Generate every service's OpenAPI (Swagger) document offline via the Swashbuckle CLI, and FAIL if any
# service's spec cannot be produced. This catches broken Swagger config, duplicate route templates, and
# bad annotations at build time — without running the services or a database (DB access is lazy, so dummy
# connection strings satisfy DI registration and nothing actually connects).
#
# Prereq: the local tool is restored (`dotnet tool restore`). THIS SCRIPT BUILDS RELEASE ITSELF — see below.
#   $1        — output dir for the generated specs (default artifacts/openapi)
#   DOTNET    — dotnet launcher (default `dotnet`; locally pass DOTNET=./dotnet.sh)
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT="${1:-artifacts/openapi}"
DOTNET="${DOTNET:-dotnet}"
mkdir -p "$OUT"

# ── BUILD RELEASE FIRST. This is not a convenience; it is what makes the answer true. ──────────────────────
#
# The loop below reads `services/*/Api/bin/Release/net8.0/*.Api.dll`. It used to only CHECK that the file
# exists, with a comment saying "build the solution in Release first" — advice that appears solely in the
# branch where the dll is MISSING. A dll that is present but stale sails straight past it.
#
# That is the normal state of a working tree. `dotnet build` and `dotnet test` default to Debug, so a
# developer can change a contract, build, test, and still have a Release dll from days ago sitting on disk.
# Generation then describes the OLD service, the drift gate compares it against the equally old committed
# spec, finds no difference, and prints "✓ every committed spec matches the running services".
#
# Observed exactly that: an endpoint's request contract was rewritten — three properties removed — and the
# gate reported clean. A Release build made it report 26 changed lines immediately.
#
# A gate that answers "no drift" from stale inputs is worse than no gate, because the green is *reported*
# rather than merely absent, and CLAUDE.md is explicit that the spec is the contract. Building here means the
# thing being described and the thing being compared are the same build, always.
#
# In CI this is a no-op: backend-ci.yml already builds the solution in Release before calling this, so the
# incremental build finds nothing to do. Paying that cost is the point — the guarantee must not depend on
# the caller having remembered.
echo "==> Building Release so the generated specs describe THIS tree, not a stale artefact…"
if ! $DOTNET build HbmpPlatform.sln -c Release -v quiet --nologo >/dev/null; then
  echo "::error::Release build failed — refusing to generate specs from whatever dlls happen to be on disk."
  echo "Run: $DOTNET build HbmpPlatform.sln -c Release   and fix the errors first."
  exit 1
fi

# DI reads ConnectionStrings:<Key> (throws if absent) + Auth:*. Most services connect to the DB lazily so a
# dummy value suffices; a few (e.g. audit) migrate at startup and need a REAL connection — so any
# ConnectionStrings__<Key> already exported by the caller (CI, which has the migrated Postgres) is kept.
dummy="Host=localhost;Port=5432;Database=placeholder;Username=placeholder;Password=placeholder"
# 18.E1 (audit R2 Q2): Identity and Interop added. Both were missing, so neither had an OpenAPI gate —
# identity-service being the one that mints every token on the platform.
# profile-service is deliberately ABSENT from this list: it owns no data and has no DbContext, so it reads no
# ConnectionStrings key at all (phase 20, design 39 §7.4). Adding a dummy for it would imply a database it does
# not have.
for k in Admin Approvals Audit CallCentre Case Claims Document Eligibility Emr Finance \
         Identity Interop Inventory MasterData Notification Orders Patient Pharmacy Policy Provider Reporting; do
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

# 24.1 — COUNT WHAT WE EXPECT BEFORE WE PRODUCE IT. Discover the services that DECLARE a Swagger document
# up front, so the run has a target to be measured against. Without one, "generated nothing" and "generated
# everything" both look like success: the loop below simply has no iteration that fails, and the drift gate
# downstream copies `artifacts/openapi/*.json`, matches no files, sees no diff and reports the contract
# verified. A silent zero is the worst outcome this script can produce, so it is now an explicit failure.
declare -a want=()
for api in services/*/Api; do
  [ -d "$api" ] || continue
  grep -q AddSwaggerGen "$api"/*.cs 2>/dev/null || continue
  want+=("$(basename "$(dirname "$api")")")
done
expected=${#want[@]}
if [ "$expected" -eq 0 ]; then
  echo "::error::no service under services/*/Api declares AddSwaggerGen — either the tree moved or this" \
       "script is running from the wrong directory. Refusing to report success having generated nothing."
  exit 1
fi

fail=0
count=0
for svc in "${want[@]}"; do
  api="services/$svc/Api"
  # `|| true`: under `set -e` + `pipefail` a failing `ls` inside a command substitution kills the script
  # outright, so the "dll not found" branch below could never actually be reached to explain itself.
  dll=$(ls "$api"/bin/Release/net8.0/Mersal.*.Api.dll 2>/dev/null | head -1 || true)
  if [ -z "$dll" ]; then
    # The Release build above succeeded, so a missing dll means this service is not IN the solution — it
    # would silently drop out of the contract otherwise, which is the failure mode the expected-count check
    # at the end exists to catch. Say which one, and why.
    echo "::error::$svc — no Api dll after a successful Release build. Is it missing from HbmpPlatform.sln?"
    fail=1; continue
  fi
  if $DOTNET swagger tofile --output "$OUT/$svc.json" "$dll" v1 >/dev/null 2>"$OUT/$svc.err"; then
    paths=$(python3 -c "import json,sys;print(len(json.load(open('$OUT/$svc.json')).get('paths',{})))" 2>/dev/null || echo "?")
    printf '  ok   %-14s %s path(s)\n' "$svc" "$paths"; count=$((count + 1)); rm -f "$OUT/$svc.err"
  else
    echo "::error::$svc — OpenAPI generation failed:"; tail -6 "$OUT/$svc.err"; fail=1
    # A half-written document is worse than none: the drift gate would copy it over the committed contract
    # and the diff would read as a deliberate API change.
    rm -f "$OUT/$svc.json"
  fi
done

echo "==> Generated $count of $expected service OpenAPI spec(s) into $OUT."
if [ "$count" -ne "$expected" ]; then
  echo "::error::expected $expected spec(s), produced $count — the OpenAPI contract is NOT verified by this run."
  fail=1
fi
exit $fail
