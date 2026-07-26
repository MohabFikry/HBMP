#!/usr/bin/env bash
# Phase 16.7 (identity slice) — provision the call-centre + claims portal identities in a RUNNING Keycloak.
#
# WHY THIS EXISTS: Keycloak only imports realm-mersal.json on FIRST boot. Roles/scopes/users added to the
# realm file afterwards do NOT appear in an already-running realm (they surface only on a clean re-import).
# This script closes that gap idempotently — safe to re-run. It provisions exactly the additions that
# realm-mersal.json now also carries, so a fresh import and a running realm converge to the same state.
#
# Usage:  KC_ADMIN_PASSWORD=... ./provision-identity.sh   (defaults to the dev password below)
set -euo pipefail

KC_CONTAINER="${KC_CONTAINER:-mersal-hbmp-keycloak-1}"
REALM="${REALM:-mersal}"
CLIENT_ID="${CLIENT_ID:-hbmp-web}"
KC_ADMIN="${KC_ADMIN:-admin}"
KC_ADMIN_PASSWORD="${KC_ADMIN_PASSWORD:-Dev_KcPass_2026!}"   # dev-only; override out of band in real envs
USER_PASSWORD="${USER_PASSWORD:-Mersal2026!}"                # dev-only login password for seeded portal users
TENANT_ID="${TENANT_ID:-11111111-1111-1111-1111-111111111111}"

kc() { docker exec "$KC_CONTAINER" /opt/keycloak/bin/kcadm.sh "$@"; }

kc config credentials --server http://localhost:8080 --realm master --user "$KC_ADMIN" --password "$KC_ADMIN_PASSWORD" >/dev/null
echo "authenticated to $REALM"

# 1) Realm roles ------------------------------------------------------------
for role in call_center claims_officer; do
  kc create roles -r "$REALM" -s name="$role" >/dev/null 2>&1 && echo "role + $role" || echo "role = $role (exists)"
done

# 2) Client scopes (optional — the SPA requests them explicitly) ------------
CID=$(kc get clients -r "$REALM" -q clientId="$CLIENT_ID" --fields id --format csv --noquotes 2>/dev/null | head -n1)
for scope in callcentre:read callcentre:act callcentre:interaction callcentre:verify claims:read claims:reconcile claims:export; do
  kc create client-scopes -r "$REALM" -s name="$scope" -s protocol=openid-connect \
     -s 'attributes."include.in.token.scope"=true' -s 'attributes."display.on.consent.screen"=false' >/dev/null 2>&1 \
     && echo "scope + $scope" || echo "scope = $scope (exists)"
  SID=$(kc get client-scopes -r "$REALM" --fields id,name --format csv --noquotes 2>/dev/null | awk -F, -v n="$scope" '$2==n{print $1}' | head -n1)
  [ -n "$SID" ] && kc update "clients/$CID/optional-client-scopes/$SID" -r "$REALM" >/dev/null 2>&1 && echo "    attached $scope -> $CLIENT_ID"
done

# 3) Portal users -----------------------------------------------------------
provision_user() {
  local user="$1" role="$2" first="$3"
  kc create users -r "$REALM" -s username="$user" -s enabled=true -s emailVerified=true \
     -s firstName="$first" -s lastName="Mersal" -s email="$user@mersal.local" \
     -s "attributes.tenant_id=[\"$TENANT_ID\"]" >/dev/null 2>&1 && echo "user + $user" || echo "user = $user (exists)"
  kc set-password -r "$REALM" --username "$user" --new-password "$USER_PASSWORD" >/dev/null 2>&1 || true
  kc add-roles -r "$REALM" --uusername "$user" --rolename "$role" >/dev/null 2>&1 || true
  echo "    $user -> role $role, password set"
}
provision_user callcentre    call_center    "Call Centre Agent Salma"
provision_user claimsofficer  claims_officer "Claims Officer Tarek"

echo "done — login as callcentre / claimsofficer (password: $USER_PASSWORD)"
