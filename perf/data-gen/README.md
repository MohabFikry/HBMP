# Synthetic volume generator (Phase 11.1)

Deterministic, masked, **synthetic-only** data sized to **NFR-012** (≥ 1M beneficiaries,
≥ 10M encounters). Reproducible via `SEED`. **No PHI** — opaque `SYN-*` keys, non-real
name tokens, dates masked to year/day, `synthetic=true` on every row (NFR-042).

## Generate

```bash
node generate.mjs --dataset beneficiaries --count 1000000 --seed 42 --out beneficiaries.tsv
node generate.mjs --dataset encounters   --count 10000000 --seed 42 --beneficiaries 1000000 --out encounters.tsv
```

## Load into staging (synthetic schema only — never prod)

```sql
CREATE SCHEMA IF NOT EXISTS synthetic;
CREATE TABLE synthetic.beneficiary (member_no text primary key, display_name text,
  branch_code text, birth_year int, synthetic bool);
\copy synthetic.beneficiary FROM 'beneficiaries.tsv' WITH (FORMAT csv, DELIMITER E'\t', HEADER true);

CREATE TABLE synthetic.encounter (encounter_ref text primary key, member_no text,
  branch_code text, service_type text, occurred_on date);
\copy synthetic.encounter FROM 'encounters.tsv' WITH (FORMAT csv, DELIMITER E'\t', HEADER true);
```

The staging seed job projects `synthetic.*` into each service's own schema through its normal
registration/ingest API (so RLS, identifiers, and coverage snapshots are populated the same
way production data is) — the raw tables above are the volume source, not a bypass of service
invariants.

## Guardrail

This generator must never read from, or write to, a production database. It emits synthetic
rows to stdout/file only; loading is an explicit staging step.
