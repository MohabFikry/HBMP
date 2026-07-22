# Phase 0b — Master Data Service + Reference-Data Ingestion (Release R0)

**Goal:** build `masterdata-service` (ICD, CPT, LOINC, ATC, Drug Master, drug interactions, allergens), ingest the client's **real** reference lists, and expose lookup/validation APIs that EMR, orders and prescriptions call to validate codes and check interactions.

Back to [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Design set: [../0A-DESIGN-FOUNDATIONS.md](../0A-DESIGN-FOUNDATIONS.md) §3 · [../15-database-erd.md](../15-database-erd.md) §13 (Master Data ERD) · [../22-data-dictionary.md](../22-data-dictionary.md) §10.5 · [../16-service-architecture.md](../16-service-architecture.md)

> Requires Phase 0 (audit client, authz library, service template, gateway). Reference data is **Public** sensitivity and read-mostly, but the **load process is audited** and reversible. Codes must be validated before any clinical use in later phases.

## Skills to activate
> Activate `healthcare-database-architect`, `pbm-adjudication-engine`, `clinical-workflow-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../15-database-erd.md](../15-database-erd.md) §13 — Master Data ERD: `icd_code`, `cpt_code`, `loinc_code`, `atc_class`, `drug`, `drug_interaction`, `allergen`, and the `ATC_CLASS ||--o{ DRUG` / `DRUG ||--o{ DRUG_INTERACTION` relationships.
- [../22-data-dictionary.md](../22-data-dictionary.md) §10.5 — masterdata schema (columns/keys), §11.4 (drug-interaction severity enum, allergen category), §12 (distribution: cache + OpenSearch).
- [../16-service-architecture.md](../16-service-architecture.md) — masterdata as a read-shared bounded context; how other services consume it (cache + logical FK validation, not cross-schema FK).
- [../0A-DESIGN-FOUNDATIONS.md](../0A-DESIGN-FOUNDATIONS.md) §3 — ID and code conventions.

The root `CLAUDE.md` carries stack, naming, security, audit, testing and Definition of Done. Do not restate; apply.

## Prompts

### 0b.1 — `masterdata-service`: schema, entities, read + search APIs

```text
Read ../15-database-erd.md §13 and ../22-data-dictionary.md §10.5 and §11.4. Scaffold `masterdata-service` from the Phase 0 service template (Api/Domain/Infrastructure/Tests, libs/auth + libs/audit-client + libs/authz + libs/events + OpenTelemetry pre-wired).

Create the `masterdata` schema and entities exactly per the data dictionary:
  - icd_code:        code (PK), title, chapter, is_billable (bool), icd11_map (nullable, ICD-11 ready)
  - cpt_code:        code (PK), description, category
  - loinc_code:      code (PK), long_name, component, property
  - atc_class:       atc_code (PK), title, level (int)
  - drug:            drug_id (uuid v7 PK), drug_code (UK), name, atc_code (FK -> atc_class), form, strength
  - drug_interaction: interaction_id (PK), drug_a_id (FK -> drug), drug_b_id (FK -> drug), severity (enum Minor|Moderate|Major|Contraindicated), description
  - allergen:        allergen_id (PK), code (UK), name, category (enum Drug|Food|Environmental)
Reference/keyed-by-natural-code tables (icd/cpt/loinc/atc) use the code as PK; drug/interaction/allergen use uuid v7 surrogate keys. Add a `version`/`source_release` column set so master data is versioned and re-loads are trackable. Migrations expand/contract.

Expose read APIs under /api/v1 (OpenAPI 3.1), all paginated with explicit allow-list filtering:
  - GET /icd-codes, /icd-codes/{code}   (filter by chapter, is_billable, text search)
  - GET /cpt-codes, /cpt-codes/{code}
  - GET /loinc-codes, /loinc-codes/{code}
  - GET /atc-classes, /atc-classes/{atcCode}
  - GET /drugs, /drugs/{drugCode}       (filter by atc_code, form)
  - GET /allergens
These are Public reference reads (broadly readable per role scopes) but still authorized through libs/authz and observable.

Index into OpenSearch: create indexers/index definitions for icd_code, cpt_code, loinc_code, drug (and allergen) exposing ONLY the necessary fields for lookup/typeahead (code, title/name, chapter/category, atc_code) — no unnecessary fields. Provide a search endpoint (GET /search?domain=icd|cpt|loinc|drug&q=) backed by OpenSearch.

Acceptance criteria:
  - Schema matches ../22-data-dictionary.md §10.5 exactly; drug.atc_code FKs atc_class; interaction severity + allergen category use the canonical enums.
  - Read APIs return paginated results with problem+json errors; OpenAPI is the source of truth.
  - OpenSearch returns typeahead matches for ICD, CPT and drug over only the indexed necessary fields.
Applies to: platform/reference enablement for FR-EMR (diagnosis coding), FR-ORDERS (CPT/LOINC), FR-RX (drug), US-MD-* master-data stories.
```

### 0b.2 — Data loaders in `/tools`: ingest the client's real Master Lists

```text
Read ../22-data-dictionary.md §10.5 and ../15-database-erd.md §13 for the target schema. You are ingesting the client's REAL reference data. The source files live in the workspace (repo-relative; adjust the base path to where the repo mounts them):

  Master Lists/ICD10_2019_WHO_Full.xlsx              -> icd_code
  Master Lists/CPT 2022 Codes.xlsx                   -> cpt_code
  Master Lists/Egyptian Drugs - ATC Classified.xlsx  -> drug (+ atc_class link)
  Raw Files/ICD10_2019_full.csv                      -> icd_code (CSV alternative)
  Raw Files/CPT 2022 Codes.csv                       -> cpt_code (CSV alternative)
  Raw Files/egyptian-drugs.csv                       -> drug (CSV alternative)
  Raw Files/ATC_Classification_Full_2024.xlsx        -> atc_class (ATC hierarchy + titles + level)
  Raw Files/WHO_ATC_DDD_Guidelines_2026.pdf          -> REFERENCE ONLY for ATC/DDD level rules; do not parse as data

FIRST, before writing any mapping code: OPEN each file and INSPECT the actual sheet names, header rows and column names (they will not match the schema field names). Print the detected headers and a few sample rows. Do not assume column names — derive the column->field mapping from what you actually see, and record the mapping in the loader's README.

Build the loaders under /tools (a small .NET console/worker or a scripted tool — pick one, keep it version-tracked and CI-runnable). Each loader must:
  - read the source file (xlsx via a spreadsheet lib, csv via a CSV parser),
  - map columns to the masterdata schema per the headers you inspected,
  - validate + normalize: trim, canonicalize codes (e.g., ICD dotted format), enforce enums, drop/flag malformed rows, and DEDUPE on the natural key (icd/cpt/loinc code, atc_code, drug_code),
  - link drug -> atc_class by atc_code; load atc_class from ATC_Classification_Full_2024.xlsx FIRST (parents before children by level), then drugs, so FKs resolve; for drugs whose ATC code has no matching class, log them and load the drug with a null/placeholder atc link rather than failing the batch,
  - be IDEMPOTENT and re-runnable: upsert by natural key (insert-or-update), never duplicate on a second run, and stamp source_release/version,
  - emit a LOAD REPORT: rows read, inserted, updated, skipped (with reasons), and final table counts, written to /tools/<loader>/reports and to stdout,
  - emit an audit event (via libs/audit-client) for the load run: action, source file, checksum, counts, actor — the load process is audited even though the data is Public,
  - be REVERSIBLE: support a dry-run mode and a documented rollback (e.g., by source_release) so a bad load can be undone.

Wire the loaders so they can run against the dev environment and in CI as a data-seed step. Include a README documenting each file's detected headers, the column mapping, run commands, and rollback steps.

Acceptance criteria:
  - Running a loader twice produces identical table counts the second time (idempotent) — proven by the load report.
  - ATC classes load before drugs; drugs link to atc_class; unmatched ATC codes are logged, not fatal.
  - The load report shows expected non-trivial counts for ICD-10, CPT, and ATC-classified Egyptian drugs.
  - A load run appears as an audit event; a dry-run makes no writes.
Applies to: FR-MD ingestion, US-MD-* (reference data available), Invariant: load process audited + reversible.
```

### 0b.3 — Seed interactions/allergens + lookup & validation endpoints

```text
Read ../22-data-dictionary.md §10.5 (drug_interaction, allergen) and §11.4 (severity/category enums).

Seed reference data that has no bulk source file:
  - drug_interaction: seed from a version-controlled seed set (curated pairs) with severity in {Minor, Moderate, Major, Contraindicated} and a description; drug_a_id/drug_b_id resolve against loaded drugs by drug_code. Make the pair order-insensitive (store canonically and/or check both directions).
  - allergen: seed a starter allergen catalog with category in {Drug, Food, Environmental}. Drug-class allergens should be resolvable against ATC/drug for cross-checking.
Seeds are idempotent, versioned, and audited like the 0b.2 loaders.

Expose the lookup/validation endpoints that EMR, orders and prescriptions will consume (these are the contracts later phases call — keep them stable and documented in OpenAPI):
  - GET  /api/v1/icd-codes/{code}/exists            -> validate an ICD-10 diagnosis code exists (used by EMR before saving a diagnosis)
  - GET  /api/v1/cpt-codes/{code}/exists            -> validate a procedure/order code exists
  - GET  /api/v1/drugs/resolve?code={drugCode}      -> resolve a drug by code (name, form, strength, atc_code)
  - POST /api/v1/drug-interactions/check            -> body: list of drug codes (or a pair); returns the highest-severity interaction(s) among them, with severity + description
  - POST /api/v1/allergies/check                    -> body: drug code + patient allergen codes/classes; returns any allergen conflict
All validation endpoints return a clear allow/deny + reason and problem+json on bad input; all are authorized via libs/authz and observable; reads of master data are Public but still logged for usage metrics.

Acceptance criteria:
  - A clinician-facing call can validate that a diagnosis ICD-10 code exists and resolve a drug by code.
  - Submitting a known interacting drug pair returns the correct severity (e.g., Contraindicated); a safe pair returns none.
  - An allergy check flags a drug against a matching patient allergen/class.
  - Interaction pair check is order-insensitive.
Applies to: FR-EMR (diagnosis validation), FR-RX (interaction + allergy checks), US-MD-* consumer stories; consumed later by Phase 4/5/6.
```

## Guardrails

- Master data is **Public** sensitivity and read-mostly, distributed via cache + OpenSearch — but the **load/seed process is audited** (source file, checksum, counts, actor) via `libs/audit-client`.
- Loaders and seeds are **idempotent, re-runnable, versioned, and reversible** (dry-run + rollback by source_release). A second run must not duplicate rows.
- **Inspect real file headers before mapping** — never assume column names; record the mapping.
- Load ATC classes before drugs; link drugs to `atc_class`; unmatched ATC codes are logged, not fatal.
- **Codes are validated before clinical use** — the validation endpoints are the gate EMR/orders/prescriptions must call; consuming services validate against masterdata (logical FK), not cross-schema DB FK.
- Migrations expand/contract; OpenSearch indexes carry only necessary fields.
- Reads authorized via `libs/authz` even though data is Public; no PHI in this service.

## Done when

- ICD-10, CPT, and ATC-classified Egyptian drugs are **queryable via the read APIs and searchable via OpenSearch**.
- A clinician-facing API can **validate a diagnosis (ICD-10) code exists and resolve a drug by code**; a drug-interaction pair check and an allergy check return correct results.
- The **load report shows expected counts** for ICD-10, CPT, and drugs, and re-running a loader is idempotent (counts stable, no duplicates).
- Every load/seed run is recorded as an **audit event**; a dry-run writes nothing; rollback by source_release is documented and works.
- OpenAPI documents all read + validation endpoints as the stable contract for Phases 4/5/6.
