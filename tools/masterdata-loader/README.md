# masterdata-loader

Ingests the client's **real** reference data into `masterdata-service` (phase-0b §0b.2). Idempotent, versioned by `--release`, audited, reversible. `--dry-run` parses + maps + reports **without a database** (used to validate mapping offline).

## Detected source headers → schema mapping
Headers were **inspected from the actual files** before mapping (do not assume).

### `Raw Files/ICD10_2019_full.csv` → `masterdata.icd_code` (16,751 rows)
Headers: `Code, Description, Type, Parent_Code, Parent_Description, Chapter_Code, Chapter_Description, Block_Code, Block_Description`
| source | target | notes |
|--------|--------|-------|
| `Code` | `code` (PK) | normalized upper/trim, dotted format kept |
| `Description` | `title` | |
| `Chapter_Description` | `chapter` | |
| `Type` | `is_billable` | `false` for `chapter`/`block`; `true` for leaf types (category/subcategory) |

### `Raw Files/CPT 2022 Codes.csv` → `masterdata.cpt_code` (10,810 rows)
Headers (BOM on first): `Code, Category, Description`. `Code`→code(PK), `Description`→description, `Category`→category.

### `Raw Files/Egyptian Drugs - ATC Classified.csv` → `masterdata.drug` (25,063 rows) **and** `masterdata.atc_class` (2,150 nodes)
Headers: `Commercial Name (EN), Scientific Name, Manufacturer, Drug Class, Route, Price (EGP), ATC Code, ATC L1 – Anatomical Main Group … ATC L5 – Chemical Substance, Classification Status, Component ATC (combinations)`
- **drug**: `Commercial Name (EN)`→name (+ normalized `drug_code` natural key), `Scientific Name`→scientific_name, `Manufacturer`, `Route`→form, `Price (EGP)`→price_egp, `ATC Code`→atc_code (nullable).
- **atc_class** is **derived** from each row's `ATC Code` (the L5 code) + the `ATC L1…L5` title columns: every present level is emitted (ancestor codes by truncation, level from code length), deduped by `atc_code`. This guarantees every drug's `atc_code` FK resolves.
  - *Deviation flagged (per CLAUDE.md ground rules):* `ATC_Classification_Full_2024.xlsx` is the canonical hierarchy, but is not machine-inspectable in this environment (no spreadsheet lib at inspection time). Deriving from the ATC-classified drug file yields a consistent, referenced subset now; reconcile against the full xlsx (via ClosedXML) in a follow-up if fuller coverage is needed.
- Combination drugs (empty `ATC Code`, populated `Component ATC`) currently load with a null ATC link — component-level interaction expansion is a follow-up.

`Raw Files/egyptian-drugs.csv` is **not** used (no ATC column). `WHO_ATC_DDD_Guidelines_2026.pdf` is reference-only (not parsed).

## Run
```bash
# Dry-run: parse + validate + report, no DB (proves ingestion)
./dotnet.sh run --project tools/masterdata-loader -- --dry-run --release R2019-2022-EG

# Real load (idempotent upsert; ATC before drugs so FKs resolve)
./dotnet.sh run --project tools/masterdata-loader -- \
  --connection "Host=localhost;Database=hbmp;Username=hbmp;Password=***" --release R2019-2022-EG
```
A load report (rows read/inserted/updated/skipped + final counts) is printed and written to `bin/.../reports/`. The run emits an audit event (source file SHA-256 + counts + actor) via `libs/audit-client`.

## Idempotency & reversibility
- **Idempotent**: upsert by natural key (icd/cpt/atc code, drug_code) — a second run updates in place, never duplicates (final counts stable).
- **Versioned**: every row stamps `source_release`.
- **Reversible**: rollback by `source_release` (`DELETE ... WHERE source_release = '<release>'` for rows introduced by that load; drugs referenced by later data are soft-handled). Dry-run makes no writes.

## Seeds (0b.3)
Allergens are seeded idempotently via `services/masterdata/Infrastructure/Migrations/0002_seed_allergens.sql`. Drug-drug interactions reference `drug_id`, so they are seeded **after** drugs load (curated pairs resolved by `drug_code`). The `/drug-interactions/check` and `/allergies/check` validation endpoints consume these.
