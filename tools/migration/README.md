# Mersal HBMP — Migration Toolkit (`mersal-migrate`)

Phase 12.1. Reversible, audited, idempotent onboarding pipelines for existing **master data**,
**providers**, and **beneficiaries**. Dry-run in **staging with masked data** before any production
run. Companion DPIA: [`docs/compliance/migration-dpia.md`](../../docs/compliance/migration-dpia.md).

## Guarantees (all proven by tests)
- **Idempotent** — upsert on `(stream, natural_key)`; re-running a batch updates in place, never
  duplicates.
- **Reversible** — `rollback --batch <id>` soft-reverts exactly the rows a batch touched, leaving
  pre-existing rows untouched.
- **Audited** — every load emits a hash-chained audit event (via audit-service) + a local JSONL
  audit artifact; every landed row carries provenance (source system, source id, batch id).
- **Reconciled** — each run emits a reconciliation report (source vs loaded vs held vs rejected +
  field-mapping coverage + exception list); a run is not "done" until it **balances**.
- **Safe dedupe** — low/medium-confidence beneficiary matches are **never auto-merged**; they are
  **held** in a review queue for human sign-off.

## Pipeline
`staging → validate → transform/map → load → reconcile`, driven by a versioned per-stream
`StreamConfig` (JSON). The config version is recorded on every batch for reproducibility.

## Streams
- **A — master data** (`MasterDataStream`): validates counts + versions against phase-0b ingest
  (validate, not re-load).
- **B — providers** (`ProviderStream`): imports provider orgs/locations/contracts/users;
  `ProviderIsolationVerifier` proves each user is scoped to its own provider before enabling.
- **C — beneficiaries** (`BeneficiaryStream`): identifier normalization (national ID / UNHCR /
  passport) → deterministic + fuzzy dedupe → policy/coverage mapping; produces a dedupe report.

## Usage
```bash
# print a starter config to edit
mersal-migrate default-config --stream beneficiaries > ben.config.json

# run a stream (staging is masked by default)
mersal-migrate run-beneficiaries --conn "<connstring>" --csv beneficiaries.csv \
  --config ben.config.json --env staging --audit-log run.jsonl

mersal-migrate run-providers     --conn "<connstring>" --csv providers.csv

# reverse a run
mersal-migrate rollback --conn "<connstring>" --batch <guid>
```
Production runs additionally require `--i-understand-prod` **and** a passed go-live gate (../35 §3).

## Schema
`Migrations/0001_migration.sql` provisions the `migration` schema (`batch`, `landing`). The toolkit
self-applies it (`PostgresSink.EnsureSchemaAsync`); it is also picked up by the migration apply glob
for staging/prod.

## Tests
`Tests/` — pure-logic unit tests (normalization, dedupe tiers, reconciliation, isolation, stream
routing) run without a DB; the Postgres sink round-trip (idempotency + rollback-by-batch) is
env-gated on `MIGRATION_TEST_DB` (operator connection), matching the rest of the repo.

```bash
dotnet test tools/migration/Tests/Mersal.Migration.Tests.csproj                 # 25 unit
MIGRATION_TEST_DB="Host=localhost;Port=55432;Database=hbmp;Username=hbmp;Password=…" \
  dotnet test tools/migration/Tests/Mersal.Migration.Tests.csproj               # + real-PG
```
