#!/usr/bin/env bash
# Ingest the real ICD/CPT/ATC/drug reference data into the running Compose Postgres.
set -euo pipefail
cd "$(dirname "$0")/../.."
set -a; . infra/compose/.env; set +a
CONN="Host=localhost;Port=55432;Database=hbmp;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
echo "==> Loading master data into hbmp (idempotent upsert)…"
./dotnet.sh run --project tools/masterdata-loader -c Release -- --connection "$CONN" --release R2019-2022-EG
echo "==> Row counts:"
PGPASSWORD="${POSTGRES_PASSWORD}" psql -h localhost -p 55432 -U "${POSTGRES_USER}" -d hbmp -c \
  "select 'icd' t, count(*) from masterdata.icd_code union all select 'cpt', count(*) from masterdata.cpt_code union all select 'atc', count(*) from masterdata.atc_class union all select 'drug', count(*) from masterdata.drug;"
