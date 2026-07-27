#!/usr/bin/env bash
# Bring up the Mersal HBMP Tier 1 stack (infra + built services), then show endpoints.
# Prereq: Docker + the compose plugin installed (see repo README / docs/adr/0003).
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] || { echo "Creating .env from .env.example — edit secrets before real data."; cp .env.example .env; }

# Phase 17 (ADR-0015) retired Keycloak for the in-app issuer, but this script kept naming it as a service to
# start — and compose no longer defines one, so `docker compose up … keycloak …` failed and the documented
# way to bring the stack up did not work at all. Kong also has to come up AFTER identity-service: it reads
# the issuer's public key at boot to validate tokens at the edge.
echo "==> Checking required secrets…"
missing=()
for key in POSTGRES_PASSWORD HBMP_APP_PASSWORD IDENTITY_SERVICE_SECRET IDENTITY_DEMO_PASSWORD; do
  grep -qE "^${key}=.+" .env || missing+=("$key")
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "  .env is missing values for: ${missing[*]}" >&2
  echo "  Compose substitutes a BLANK for each, which fails at runtime somewhere unrelated." >&2
  echo "  Copy the keys from .env.example and set them." >&2
  exit 1
fi

echo "==> Starting infrastructure (Postgres, MinIO, RabbitMQ, NATS, Valkey, OpenSearch, OpenBao, ClamAV, LGTM)…"
docker compose up -d postgres minio rabbitmq nats valkey opensearch openbao clamav prometheus loki tempo grafana

echo "==> Waiting for Postgres to be healthy…"
until [ "$(docker inspect -f '{{.State.Health.Status}}' "$(docker compose ps -q postgres)" 2>/dev/null || echo starting)" = "healthy" ]; do
  sleep 3; printf '.'
done
echo " ok"

echo "==> Building + starting identity-service (the in-app OIDC issuer)…"
docker compose up -d --build identity-service

echo "==> Waiting for the issuer to publish its signing key…"
until curl -sf http://localhost:8090/.well-known/jwks >/dev/null 2>&1; do
  sleep 2; printf '.'
done
echo " ok"

# Kong validates every token at the edge against the issuer's RS256 PUBLIC key, registered as a consumer
# credential (Kong OSS has no JWKS discovery). The key is not a secret, but it must track whatever the issuer
# is currently signing with — a mismatch rejects every request at the gateway with no clue as to why.
#
# REFRESHED ON EVERY RUN, not written once. In Development the issuer mints an EPHEMERAL signing key at
# startup, so any rebuild of identity-service silently invalidates the copy Kong booted with; a stale value
# is worse than a missing one, because Kong comes up healthy and rejects every real token. (Phase 12.3 made
# the keys persistent in OpenBao for the deployed tiers; this dev path has no such store.)
echo "==> Deriving IDENTITY_JWKS_PUBLIC_KEY from the issuer's live JWKS…"
pem=$(python3 ./jwks-to-pem.py http://localhost:8090/.well-known/jwks)
tmp_env=$(mktemp)
grep -v '^IDENTITY_JWKS_PUBLIC_KEY=' .env > "$tmp_env"
printf 'IDENTITY_JWKS_PUBLIC_KEY="%s"\n' "$(printf '%s' "$pem" | awk '{printf "%s\\n", $0}')" >> "$tmp_env"
mv "$tmp_env" .env

# --force-recreate because the key is passed as an environment variable: without it compose sees an unchanged
# service definition and leaves the old container — still holding the previous key — running.
echo "==> Starting Kong…"
docker compose up -d --force-recreate kong

echo "==> Building + starting application services (audit, masterdata)…"
docker compose up -d --build audit-service masterdata-service

cat <<'EOF'

==> Stack is up. Endpoints (host):
  identity-service (issuer)  http://localhost:8090   (users seeded per role; password = .env IDENTITY_DEMO_PASSWORD)
  Kong API gateway           http://localhost:8000   (e.g. /health/live, /api/v1/masterdata)
  Grafana (traces/logs)      http://localhost:3000   (admin / see .env GRAFANA_ADMIN_PASSWORD)
  MinIO console              http://localhost:9001
  RabbitMQ management        http://localhost:15672
  masterdata-service         http://localhost:8091/swagger

Next: seed reference data →  ./seed-masterdata.sh
EOF
