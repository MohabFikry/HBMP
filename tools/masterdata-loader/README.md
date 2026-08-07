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

### `Master Lists/egyptian-drug-list_5.xlsx` → `masterdata.drug`, `masterdata.atc_class`, `masterdata.drug_indication`

**This workbook supersedes `Egyptian Drugs - ATC Classified.csv` as the drug source.** The CSV remains a
fallback for environments without the workbook — but it carries no indications, so the phase-26 indication
check reports *"not checked"* on every line when it is used. The loader prints which source it took.

Inspected before mapping (phase 26.1 requires it, and the design assumed a shape the file does not have).
Five sheets; the loader reads **`Drug List`** and validates against `masterdata.icd_code`:

| sheet | rows | role |
|---|---|---|
| `Drug List` | 22,653 × 33 cols | **loaded** — drugs, ATC, indications |
| `Notes` | 60 | provenance narrative (quoted below) |
| `ATC to ICD map` | 597 | the generator of `Related ICDs`; not loaded (see *granularity*) |
| `ATC reference` | 6,996 | WHO ATC hierarchy; `atc_class` is derived from the drug rows instead |
| `ICD usage` | 874 | frequency telemetry; not loaded |

#### `Drug List` → schema

| col | header | fill | target |
|---|---|---|---|
| A | `ID` | 100% | `drug.source_row_id` — 22,653 distinct, **zero duplicates**; `drug_id` is derived from it |
| B | `Trade Name (EN)` | 100% | `drug.name` (+ normalized `drug_code`) |
| C | `Price (EGP)` | 100% | `drug.price_egp` |
| D | `Active Ingredient` | 95.3% | `drug.scientific_name` |
| E | `Manufacturer` | 98.5% | `drug.manufacturer` |
| H | `ATC Code` | 85.2% | `drug.atc_code` |
| J,L,N,P,R | `ATC L1–L5 Name` | 85.2→83.9% | `atc_class` (derived by truncation, as for the CSV) |
| **T** | **`Related ICDs`** | **100%** | **`drug_indication.icd_code`** — comma-separated |
| U | `ICD Count` | 100% | checksum for T; not stored |
| V | `ICD Basis` | 100% | `drug_indication.source` |
| Y | `Volume / Weight` | 33.3% | `drug.strength` (fallback) |
| Z | `Strength` | 60.4% | `drug.strength` |
| AA | `Dosage Form` | 98.7% | `drug.form` |
| F,G,W,X,AB–AF | class, category, pack size, barcode, origin, price date | — | not loaded |
| **AG** | **`UNHCR`** | **0%** | **header only, no data** — a UNHCR formulary must be authored as a `benefit_list` (phase 27), not loaded from here |

Columns are bound **by header name**; a rename or reorder throws rather than loading nulls.

#### Three properties of this data that the code is shaped around

1. **The ICDs are 3-character categories.** All 874 distinct codes are categories (`E11`, `J01`); not one is
   4-character or dotted. `masterdata.icd_code` stores dotted codes and `emr.diagnosis` records the specific
   one, so the indication check compares via `MasterDataNormalize.IcdCategory` on both sides. Comparing by
   equality would report *"not a listed indication"* on virtually every prescription — a warning that always
   fires is a warning clinicians learn to click through.
2. **`Z76` is a filler, not an indication.** The source drops it wherever a real indication exists, so it only
   appears alone. **1,019 drugs (4.5%) carry it as their only code**; they load with *zero* indications and
   report "not checked". Storing `Z76` would let a product with no clinical data render as checked.
3. **The mapping is keyed at ATC level 4 and is clinical judgement.** The `Notes` sheet says so plainly:
   *"the ATC-to-ICD step itself is still clinical judgement, not a published dataset, because no free
   authoritative drug-to-indication mapping exists. Spot-validate a stratified sample against EDA leaflets or
   FDA/EMA labels before this gates live claims."* That sentence is why `source` is mandatory, is surfaced to
   the prescriber, and why an indication mismatch may only ever **warn** (doc 43 §1).

#### What this file cannot supply

- **`drug.name_ar` — no Arabic column exists.** Only 108 of 22,653 rows contain any Arabic character, and
  incidentally. The combobox falls back to the English trade name; the load report states the count.
- **A UNHCR formulary** — column AG is empty (above).
- **Dosing rules** — no max-dose, weight-band or duration data. The dose check reports "no rule configured".

#### Observed load (release `phase26-test`, dry-run, 11 s, 176 MB peak)

```
[drug          ] read=  22653  final=  22653
                 ! name_ar: 0/22653 — the workbook carries no Arabic trade name
                 ! strength: 17873/22653 populated (from 'Strength', falling back to 'Volume / Weight')
[drug_indication] read= 214402  final= 214402
                 ! 1019 drug(s) carry no indication data — these report "not checked", never "OK"
```

**Every ICD category resolved** against `masterdata.icd_code` — zero unmatched. The unmatched path is
nonetheless exercised by unit tests (`DrugListLoaderTests`), because a drug that silently loses its
indications reports "not checked" forever and nothing would surface it.

The sheet is ~750,000 cells, so `XlsxReader` streams it with a SAX reader rather than materialising the
workbook, which costs on the order of a gigabyte.

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
- **Idempotent**: upsert by natural key (icd/cpt/atc code, drug `source_row_id` then `drug_code`, indication `(drug_id, icd_code)`) — a second run updates in place, never duplicates (final counts stable).
- **Stable ids**: `drug_id` is *derived* from the source row id (`MasterDataNormalize.DrugId`), not minted per run. It used to be `Guid.NewGuid()`, which made id stability depend on the trade-name string never drifting; any drift minted a fresh uuid and orphaned the indications, interactions and prescription lines pointing at the old one.
- **Adoption, not duplication**: a workbook row matches an existing row by `source_row_id`, then by `drug_code`, and **keeps the existing uuid**. Rows present in the database but absent from the file are left alone — reference data is never hard-deleted. Indications withdrawn by a release are soft-deleted.
- **Versioned**: every row stamps `source_release`.
- **Reversible**: rollback by `source_release` (`DELETE ... WHERE source_release = '<release>'` for rows introduced by that load; drugs referenced by later data are soft-handled). Dry-run makes no writes.

## Seeds (0b.3)
Allergens are seeded idempotently via `services/masterdata/Infrastructure/Migrations/0002_seed_allergens.sql`. Drug-drug interactions reference `drug_id`, so they are seeded **after** drugs load (curated pairs resolved by `drug_code`). The `/drug-interactions/check` and `/allergies/check` validation endpoints consume these.
