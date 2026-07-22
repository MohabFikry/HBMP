---
name: Healthcare Database Architect
description: Governs Mersal's schema-per-service PostgreSQL data model — 3NF, keys/indexes, audit and *_history tables, soft-delete, PHI/PII/SPI sensitivity classification, Row-Level Security, and append-only fulfillment/dispense with unique-constraint idempotency. Use when designing schemas, migrations, ERDs, data models, indexes, or reviewing any table/column change.
---

# Healthcare Database Architect

## Purpose
Keep every schema, migration, and data model consistent with Mersal's HBMP data architecture: each microservice owns its PostgreSQL schema (no shared tables, no cross-service FKs), transactional tables are 3NF with standard audit columns, soft-delete + `_history` twins give point-in-time reconstruction, sensitivity classes drive RLS/masking/minimization, and benefit consumption is enforced by append-only tables with unique-constraint idempotency.

## When to use / when not to use
- **Use when:** designing or reviewing table/column definitions, keys, indexes, constraints, migrations, ERDs, RLS policies, history/audit tables, master-data tables, or the consume/dispense storage pattern.
- **Not for:** service boundaries and event choreography (Platform Architect), external FHIR mapping (FHIR Integration Architect), or lifecycle transition rules (state machines — reference them for enum/CHECK values).

## Mersal domain knowledge & rules
- **Schema/DB-per-service; no cross-boundary FKs.** Ownership: `identity`, `patient`, `policy`, `eligibility`, `provider`, `emr`, `orders`, `pharmacy`, `approvals`, `notification`, `masterdata` (shared read), `audit`, `document`. Cross-service references stored as `*_id UUID` **values**; FK constraints exist only within a single schema. Cross-schema links are logical, maintained by events.
- **Keys:** surrogate PK is UUID **v7** (time-orderable) unless pure reference data keyed by natural code (`icd_code.code`, `cpt_code.code`, `loinc_code.code`, `atc_class.atc_code`). Human-facing business keys stored unique: `MRS-M-YYYY-NNNNNN`, `ENC-YYYY-NNNNNN`, `ORD-*`, `RX-*`, `AUTH-*`, `REF-*` (each with a regex/format check).
- **Standard audit columns on every mutable transactional table:** `created_at`, `created_by`, `updated_at`, `updated_by`, `row_version` (optimistic concurrency/ETag), `is_deleted`, `deleted_at`, `deleted_by`.
- **Normalization = 3NF** (identifiers/contacts/vitals/allergies as child tables; `benefit_category`, `atc_class` extracted to remove transitive deps). Deliberate, documented denormalizations only: `eligibility_snapshot` (derived read model) and `coverage_limit.consumed_value` (authoritative accumulator).
- **Soft-delete + history:** all read paths filter `is_deleted=false` (enforced via RLS predicates/default views). **Unique indexes are partial** (`WHERE is_deleted=false`) so a deleted business key can be reused if policy allows. Every mutable base table has a `_history` twin (`history_id`, `{entity}_id`, `row_version`, `jsonb snapshot`, `change_type` INSERT/UPDATE/SOFT_DELETE, system-time `valid_from`/`valid_to`, `changed_by`) written by trigger/outbox — never by app logic — giving point-in-time reconstruction without SQL:2011 temporal tables.
- **Audit vs history:** `_history` = per-entity temporal record (what the row looked like); `audit.audit_event` = per-action compliance log (who did what, `correlation_id` across services, minimized before/after snapshots), append-only, range-partitioned by month. Both are required; both immutable.
- **Self-hosted PostgreSQL (open-source, on-prem-first, cloud-ready — $0 licensing):** encryption-at-rest via **LUKS** (full-disk) + **pgcrypto** for column-level encryption of sensitive fields; **Patroni** for HA/failover; **pgBackRest** for PITR/backups; DB credentials and encryption keys live in **OpenBao/Vault** (never in the repo — SOPS-encrypted values in git). Same schema/design runs on-prem (k3s/Compose) and in cloud without change (see `../../0C-OPEN-SOURCE-STACK.md`).
- **Sensitivity classes drive controls:** **PHI** (clinical — LUKS + pgcrypto at rest, RLS, audit on read, minimized in search/exports), **PII** (personal — encrypted, RLS, masked in non-prod), **SPI** (refugee/legal status, e.g. `beneficiary_identifier.identifier_value` — strictest access, redacted by default), **Internal**, **Public** (master/reference — cacheable). Classify every new column.
- **Row-Level Security:** provider-scoped tables filter by the caller's `provider_id` claim; beneficiary clinical data is not provider-partitioned but is access-controlled by role + care-relationship and audited on read. RLS also enforces `is_deleted=false`.
- **Append-only consume/dispense with idempotency (the critical invariant):** `order_fulfillment` and `dispense_event` are immutable, no `updated_at`/soft-delete, with `UNIQUE(idempotency_key)`. A consume/dispense = one row insert + guarded parent update (`UPDATE order_line SET quantity_consumed = quantity_consumed + :q WHERE quantity_consumed + :q <= quantity_ordered`) + outbox insert, all in one **serializable** transaction. `CHECK (0 <= quantity_consumed <= quantity_ordered)` (and dispensed<=prescribed) makes over/duplicate use structurally impossible; no update path returns quantity (no-reuse).
- **Money** = `NUMERIC(14,2)` + ISO `currency_code`; **quantities** = `NUMERIC(14,3)`. Enums = constrained `VARCHAR` with `CHECK` (+ lookup table where useful); canonical values in `../../22-data-dictionary.md` §11.
- **Master data** (`masterdata` schema, Public, read-mostly, Valkey cache + OpenSearch): `icd_code` (ICD-10, `icd11_map` ready), `cpt_code`, `loinc_code`, `atc_class`, `drug` (+`drug_code`, `atc_code` FK), `drug_interaction` (severity Minor/Moderate/Major/Contraindicated), `allergen`. Clinical tables reference these as validated logical references (validated at write via lookup/cache), not cross-schema FKs.
- **Blobs never in the RDBMS:** `document`/`document_version` hold metadata + `checksum_sha256` + object path/key; content lives in **MinIO** (S3-compatible, server-side encryption, object-lock/WORM), never in the database.

## Key entities, states & invariants
- Hot-path indexes: eligibility `(beneficiary_id, coverage_id) INCLUDE (decision, expires_at)`; consume guarded update on `(order_line_id)` + `UNIQUE(idempotency_key)`; dispense `UNIQUE(idempotency_key)` + `(prescription_line_id)`; beneficiary lookup `(identifier_type, identifier_value) WHERE is_deleted=false`; encounter timeline `(beneficiary_id, started_at DESC)`; expiry sweeps partial index on `expires_at WHERE status IN active-ish`; audit `(entity_type, entity_id, occurred_at)` monthly partitions.
- `status` columns carry the canonical enums whose transitions are defined in `../../23-state-machines.md`; CHECK constraints must match those enum sets exactly.
- Invariants any migration must preserve: no cross-service FK, append-only fulfillment/dispense, idempotency uniqueness, quantity CHECKs, soft-delete + history twin, correct sensitivity classification + RLS.

## How to apply
- For a new table: assign it to exactly one owning schema; add UUID v7 PK, business key with format check if human-facing, standard audit columns, `_history` twin (unless append-only), sensitivity class per column, partial unique indexes, and RLS policy.
- For consumption/fulfillment: always use the append-only + `UNIQUE(idempotency_key)` + guarded parent update + `CHECK` pattern inside a serializable transaction; never a mutable running total without a guard.
- Reference other services and master data by ID value + write-time validation, never cross-schema FK.
- Classify PHI/PII/SPI on every column and wire the matching RLS/masking/minimization; keep PHI/SPI out of search indexes and exports.
- In reviews, flag: cross-schema FKs, missing idempotency uniqueness on consume/dispense, mutable fulfillment rows, missing quantity CHECKs, hard deletes, missing `_history`/audit, unclassified sensitive columns, non-partial unique indexes on soft-deletable keys.

## Canonical references
- Logical model, ERDs, normalization, indexes, soft-delete/history: `../../15-database-erd.md`
- Field types, keys, PII/PHI/SPI classification, enums: `../../22-data-dictionary.md`
- Consume/dispense storage detail & idempotency: `../../15-database-erd.md` §8–9, `../../16-service-architecture.md` §8
- RLS, LUKS/pgcrypto encryption-at-rest, OpenBao secrets, masking, minimization: `../../18-security-model.md`; status enums: `../../23-state-machines.md`
- Free/open-source, on-prem/cloud-ready data stack (PostgreSQL + LUKS + pgcrypto + Patroni + pgBackRest + OpenBao + MinIO): `../../0C-OPEN-SOURCE-STACK.md`

## Guardrails
- One schema per service; no shared tables and no foreign keys across service boundaries — cross-service links are ID values validated via events.
- Fulfillment/dispense tables are append-only and immutable with `UNIQUE(idempotency_key)`; quantity guarded by CHECK + conditional update; never add a reuse/return path.
- Every mutable table gets standard audit columns, soft-delete, and a `_history` twin; deletes are soft, unique indexes partial on `is_deleted=false`.
- Classify every column's sensitivity and enforce RLS/masking; PHI/SPI never enter search indexes, logs, or exports beyond minimum-necessary.
- CHECK-constrained enums must exactly match the canonical values in the data dictionary and state machines; blobs stay in MinIO object storage, not the RDBMS.
