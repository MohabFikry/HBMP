# masterdata-service

Phase 0b. Read-mostly reference data (ICD-10, CPT, LOINC, ATC, Drug master, interactions, allergens) with the lookup/validation APIs EMR, orders and prescriptions call (22-data-dictionary §10.5). Public data, but loads are audited + reversible.

## Real data ingested (via `tools/masterdata-loader`)
- **16,751** ICD-10 codes · **10,810** CPT codes · **2,150** ATC classes · **25,063** Egyptian drugs — from the client's `Raw Files/`. See the loader README for the inspected-header→schema mapping.

## Read/search APIs (`/api/v1`, authenticated)
`/icd-codes`, `/cpt-codes`, `/atc-classes`, `/drugs`, `/allergens` (paginated, allow-list filters), `/search?domain=icd|cpt|drug&q=` (typeahead; Tier-1 DB ILIKE, OpenSearch indexer is a follow-up).

## Validation endpoints (the stable contracts for phase 4/5/6)
- `GET /icd-codes/{code}/exists`, `GET /cpt-codes/{code}/exists` — code validation before clinical use.
- `GET /drugs/resolve?code=` — resolve a drug (name/form/strength/atc).
- `POST /drug-interactions/check` — highest-severity interaction among drug codes (order-insensitive).
- `POST /allergies/check` — flag a drug against patient allergen codes/classes (ATC-chain match).

## Schema & seeds
`Infrastructure/Migrations/0001_masterdata_schema.sql` (schema + FKs + indexes); `0002_seed_allergens.sql` (starter allergen catalog, idempotent). Interactions are seeded after drugs load (they reference drug_id).

## Tests
16 mapper/normalization tests (ICD billable logic, drug-code key stability, ATC level derivation + class extraction). Run: `./dotnet.sh test services/masterdata/Tests/Mersal.MasterData.Tests.csproj`.
