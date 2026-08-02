#!/usr/bin/env bash
# ============================================================================================================
# dev-token.sh — an access token for a demo account, from the command line.
#
#   tools/dev/dev-token.sh doctor            # prints the access token
#   TOKEN=$(tools/dev/dev-token.sh doctor)
#   curl -H "Authorization: Bearer $TOKEN" http://localhost:8000/api/v1/appointments?mine=true
#
# The issuer allows only authorization_code + PKCE for the SPA client — no password grant, deliberately. So
# this drives the SAME flow a browser does: sign in at /connect/login with the antiforgery token, then redeem
# the code. It is a testing convenience, not a back door; it needs the demo password and works only against
# the dev issuer.
#
# DEV ONLY. IDENTITY_DEMO_PASSWORD is a local value from infra/compose/.env.
# ============================================================================================================
set -euo pipefail

ROLE="${1:-doctor}"
ISSUER="${ISSUER:-http://localhost:8090}"
REDIRECT="${REDIRECT:-http://localhost:5173/}"
CLIENT_ID="${CLIENT_ID:-hbmp-web}"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="$REPO/infra/compose/.env"
COMPOSE="$REPO/infra/compose/compose.yaml"
PASSWORD="${IDENTITY_DEMO_PASSWORD:-$(grep -E '^IDENTITY_DEMO_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)}"
PGPASSWORD="${POSTGRES_PASSWORD:-$(grep -E '^POSTGRES_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)}"
[ -n "$PASSWORD" ] || { echo "no IDENTITY_DEMO_PASSWORD in $ENV_FILE" >&2; exit 1; }

JAR="$(mktemp)"; trap 'rm -f "$JAR"' EXIT

# PKCE: verifier → S256 challenge, base64url without padding.
VERIFIER="$(head -c 48 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
CHALLENGE="$(printf '%s' "$VERIFIER" | openssl dgst -binary -sha256 | base64 | tr '+/' '-_' | tr -d '=\n')"

# The scope set, read from the REGISTERED CLIENT rather than from the SPA's source.
#
# A token narrower than the SPA's authenticates fine and then 403s on the first endpoint guarding a scope we
# left out — which reads as a permissions bug and is not one. The obvious source is `scope:` in
# apps/web/src/config.ts, but that value is ~15 string literals joined with `+` and interleaved with comments;
# parsing it is guesswork, and guessing wrong either under-asks (silent 403s later) or over-asks, which the
# issuer refuses outright with ID2051 and no usable message.
#
# The OpenIddict application row is the authority on what this client may request, so asking for exactly its
# `scp:` permissions cannot be refused and cannot be short. ClientSeeder reconciles that row from
# IdentityContract.InteractiveScopes on every startup, so it tracks the contract on its own.
SCOPES="$(docker compose -f "$COMPOSE" exec -T -e PGPASSWORD="$PGPASSWORD" postgres \
  psql -U "${PGUSER:-hbmp}" -d "${PGDATABASE:-hbmp}" -tAc \
  "SELECT string_agg(replace(p, 'scp:', ''), ' ')
     FROM identity.\"OpenIddictApplications\" a,
          LATERAL jsonb_array_elements_text(a.permissions::jsonb) p
    WHERE a.client_id = '$CLIENT_ID' AND p LIKE 'scp:%';" | tr -d '\r')"
SCOPES="openid offline_access $SCOPES"
[ "$(printf '%s' "$SCOPES" | wc -w)" -gt 10 ] \
  || { echo "only got '$SCOPES' from the $CLIENT_ID client row — is postgres up?" >&2; exit 1; }

# 1. The login page, for the antiforgery cookie + field.
FORM="$(curl -sS -c "$JAR" "$ISSUER/connect/login")"
AF="$(printf '%s' "$FORM" | grep -o 'name="__hbmp_csrf"[^>]*value="[^"]*"' | sed 's/.*value="//;s/"//')"
[ -n "$AF" ] || { echo "no antiforgery token — is identity-service up at $ISSUER?" >&2; exit 1; }

# 2. Sign in. Success is a redirect; a 200 means the page came back with an error on it.
CODE_HTTP="$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' \
  -X POST "$ISSUER/connect/login" \
  --data-urlencode "username=$ROLE" \
  --data-urlencode "password=$PASSWORD" \
  --data-urlencode "__hbmp_csrf=$AF")"
[ "$CODE_HTTP" = "302" ] || { echo "sign-in failed for '$ROLE' (HTTP $CODE_HTTP)" >&2; exit 1; }

# 3. Authorize → the code arrives in the Location header of the redirect to the SPA.
LOC="$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -D - -G "$ISSUER/connect/authorize" \
  --data-urlencode "client_id=$CLIENT_ID" \
  --data-urlencode "response_type=code" \
  --data-urlencode "redirect_uri=$REDIRECT" \
  --data-urlencode "scope=$SCOPES" \
  --data-urlencode "code_challenge=$CHALLENGE" \
  --data-urlencode "code_challenge_method=S256" \
  --data-urlencode "state=devtoken" \
  | grep -i '^location:' | tail -1 | tr -d '\r')"
AUTH_CODE="$(printf '%s' "$LOC" | grep -o 'code=[^&]*' | cut -d= -f2- || true)"
[ -n "$AUTH_CODE" ] || { echo "no authorization code. issuer said: $LOC" >&2; exit 1; }

# 4. Redeem.
curl -sS -X POST "$ISSUER/connect/token" \
  --data-urlencode "grant_type=authorization_code" \
  --data-urlencode "client_id=$CLIENT_ID" \
  --data-urlencode "redirect_uri=$REDIRECT" \
  --data-urlencode "code=$AUTH_CODE" \
  --data-urlencode "code_verifier=$VERIFIER" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])'
