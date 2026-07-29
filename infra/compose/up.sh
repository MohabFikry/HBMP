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

# Schema DDL runs here, under the owning superuser, BEFORE any service starts — not from inside a service.
# 18.B2 put every service on a least-privilege login role, and a role that cannot own a schema cannot create
# one: audit-service used to apply its own migrations at startup and began crash-looping on
# `42501: permission denied for database hbmp`, because `CREATE SCHEMA IF NOT EXISTS` still performs the
# CREATE-on-database ACL check when the schema already exists. This is the same script CI runs
# (.github/workflows/backend-ci.yml) and the path the other services already depended on.
echo "==> Applying SQL migrations for every service (idempotent)…"
# shellcheck disable=SC1091
set -a; . ./.env; set +a
PGHOST=localhost PGPORT=55432 PGUSER="$POSTGRES_USER" PGPASSWORD="$POSTGRES_PASSWORD" PGDATABASE=hbmp \
  HBMP_APP_PASSWORD="$HBMP_APP_PASSWORD" HBMP_AUDIT_PASSWORD="$HBMP_AUDIT_PASSWORD" \
  bash ../../tools/ci/apply-migrations.sh

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
#
# The grep/awk pair this replaced wrote the PEM across multiple lines (GNU awk turns "%s\\n" into a real
# newline), which dotenv accepts — so the FIRST run looked fine. The second run's `grep -v '^KEY='` then
# dropped only the opening line and orphaned the rest of the PEM, after which every `docker compose`
# command in this script failed with `unexpected character "/" in variable name` and was never checked,
# leaving Kong up with a key the issuer had already replaced. set-env-key.py writes one escaped line.
# ── Persistent issuer keys ────────────────────────────────────────────────────────────────────────────────
# Generated ONCE and kept, because ephemeral issuer keys cost two things that both look like other bugs:
#   * the ENCRYPTION key wraps refresh tokens (JWE), so every restart made outstanding refresh tokens
#     undecryptable — the SPA's silent renew answered invalid_grant and the user was signed out at the
#     5-minute access-token expiry, which reads as "the session timeout is far too short";
#   * the SIGNING key's `kid` changed on every restart, so Kong's pinned public key went stale and the
#     gateway answered "Invalid signature" until the step below was re-run.
# Owned by uid 10001 (the container's appuser) and mode 400, chowned via a throwaway root container so this
# needs no sudo on the host. Gitignored by *.pem. OpenBao's transit engine remains the production home.
if [ ! -f ./secrets/issuer-signing.pem ] || [ ! -f ./secrets/issuer-encryption.pem ]; then
  echo "==> Generating persistent issuer keys (once) into ./secrets…"
  mkdir -p ./secrets
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out ./secrets/issuer-signing.pem 2>/dev/null
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out ./secrets/issuer-encryption.pem 2>/dev/null
  docker run --rm -v "$PWD/secrets:/s" alpine:3 sh -c 'chown 10001:10001 /s/*.pem && chmod 400 /s/*.pem'
  echo "    - issuer-signing.pem, issuer-encryption.pem (uid 10001, mode 400)"
fi

echo "==> Deriving IDENTITY_JWKS_PUBLIC_KEY from the issuer's live JWKS…"
python3 ./jwks-to-pem.py http://localhost:8090/.well-known/jwks \
  | python3 ./set-env-key.py IDENTITY_JWKS_PUBLIC_KEY .env

# .env is read by every compose command below; a malformed one fails them all, so stop here rather than
# carrying on and reporting a stack that is "up".
docker compose config --quiet || { echo "  .env is not parseable after the key write — aborting." >&2; exit 1; }

# --force-recreate because the key is passed as an environment variable: without it compose sees an unchanged
# service definition and leaves the old container — still holding the previous key — running.
echo "==> Starting Kong…"
docker compose up -d --force-recreate kong

echo "==> Building + starting application services (audit, masterdata)…"
docker compose up -d --build audit-service masterdata-service

cat <<'EOF'

==> Stack is up. Endpoints (host):
  identity-service (issuer)  http://localhost:8090   (users seeded per role; password = .env IDENTITY_DEMO_PASSWORD)
  Kong API gateway           http://localhost:8000   (e.g. /api/v1/icd-codes — 401 without a token)
  Grafana (traces/logs)      http://localhost:3000   (admin / see .env GRAFANA_ADMIN_PASSWORD)
  MinIO console              http://localhost:9001
  RabbitMQ management        http://localhost:15672
  masterdata-service         http://localhost:8091/swagger

Next: seed reference data →  ./seed-masterdata.sh
EOF
