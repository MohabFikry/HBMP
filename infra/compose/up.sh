#!/usr/bin/env bash
# Bring up the Mersal HBMP Tier 1 stack (infra + built services), then show endpoints.
# Prereq: Docker + the compose plugin installed (see repo README / docs/adr/0003).
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] || { echo "Creating .env from .env.example — edit secrets before real data."; cp .env.example .env; }

echo "==> Starting infrastructure (Postgres, Keycloak, Kong, MinIO, RabbitMQ, NATS, Valkey, OpenSearch, OpenBao, ClamAV, LGTM)…"
docker compose up -d postgres keycloak kong minio rabbitmq nats valkey opensearch openbao clamav prometheus loki tempo grafana

echo "==> Waiting for Postgres to be healthy…"
until [ "$(docker inspect -f '{{.State.Health.Status}}' "$(docker compose ps -q postgres)" 2>/dev/null || echo starting)" = "healthy" ]; do
  sleep 3; printf '.'
done
echo " ok"

echo "==> Building + starting application services (audit, masterdata)…"
docker compose up -d --build audit-service masterdata-service

cat <<'EOF'

==> Stack is up. Endpoints (host):
  Keycloak (realm 'mersal')  http://localhost:8080   (admin / see .env KEYCLOAK_ADMIN_PASSWORD)
  Kong API gateway           http://localhost:8000   (e.g. /health/live, /api/v1/masterdata)
  Grafana (traces/logs)      http://localhost:3000   (admin / see .env GRAFANA_ADMIN_PASSWORD)
  MinIO console              http://localhost:9001
  RabbitMQ management        http://localhost:15672
  masterdata-service         http://localhost:8091/swagger

Next: seed reference data →  ./seed-masterdata.sh
EOF
