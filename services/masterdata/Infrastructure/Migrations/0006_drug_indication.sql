-- masterdata-service — 0006 drug ↔ indication link (43-approval-engine-and-prescribing-support §6, phase 26.1).
--
-- SOURCE: "Master Lists/egyptian-drug-list_5.xlsx", sheet "Drug List", column T "Related ICDs"
-- (100% populated, 22,653 rows, ~215k drug↔ICD pairs, 874 distinct codes, median 9 per drug).
--
-- TWO PROPERTIES OF THE SOURCE THAT THE SCHEMA HAS TO RESPECT:
--
-- 1. Every code is a 3-character ICD-10 CATEGORY ("E11", "J01") — there is not one 4-character or
--    dotted code in the file. masterdata.icd_code stores DOTTED codes and emr.diagnosis records the
--    specific one ("E11.9"), so the consistency check must compare at category level. Comparing by
--    equality would report "not a listed indication" on virtually every prescription, which is the
--    alert-fatigue failure that teaches clinicians to click through warnings. icd_code here is
--    therefore documented as a category, and is deliberately NOT foreign-keyed to masterdata.icd_code:
--    the loader validates and REPORTS unmatched codes (phase 26.1) rather than letting the database
--    drop them silently.
--
-- 2. The mapping is generated at ATC level 4 and stamped onto each drug, so every substance in an ATC
--    L4 group shares an indication set. The workbook's own Notes sheet states the ATC→ICD step "is
--    still clinical judgement, not a published dataset". That is why `source` is mandatory and is
--    surfaced to the prescriber: an advisory whose provenance is unknown is one a clinician is right
--    to ignore (doc 43 §1, invariant 3), and why an indication mismatch may only ever WARN.

-- source_row_id — the workbook's own stable "ID" column (22,653 distinct, zero duplicates). It makes
-- the drug surrogate key derivable and therefore stable across reloads; see MasterDataNormalize.DrugId.
ALTER TABLE masterdata.drug ADD COLUMN IF NOT EXISTS source_row_id text;

-- Unique among rows that have one; older rows loaded from the previous CSV carry NULL and keep working.
CREATE UNIQUE INDEX IF NOT EXISTS uq_drug_source_row_id
    ON masterdata.drug (source_row_id) WHERE source_row_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS masterdata.drug_indication (
    indication_id   uuid PRIMARY KEY,
    drug_id         uuid        NOT NULL REFERENCES masterdata.drug(drug_id) ON DELETE CASCADE,
    -- 3-character ICD-10 category (see note 1). varchar(10) matches emr.diagnosis.icd_code so the two
    -- can be compared without a cast.
    icd_code        varchar(10) NOT NULL,
    -- The source carries no ranking over a drug's indications, so this stays false on load rather than
    -- inventing a clinical priority the data does not express.
    is_primary      boolean     NOT NULL DEFAULT false,
    -- Per-row provenance straight from the workbook's "ICD Basis" column:
    -- 'ATC + drug class' (19,184) · 'drug class' (3,458) · 'ATC' (4) · 'placeholder' (7).
    source          varchar(64) NOT NULL,
    source_release  varchar(64),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    deleted_at      timestamptz,
    CONSTRAINT uq_drug_indication UNIQUE (drug_id, icd_code)
);

-- The check is "what does this drug treat?" — one indexed hit on drug_id, live rows only.
CREATE INDEX IF NOT EXISTS ix_drug_indication_drug
    ON masterdata.drug_indication (drug_id) WHERE deleted_at IS NULL;
-- The reverse ("what treats this diagnosis?") backs the alternatives list.
CREATE INDEX IF NOT EXISTS ix_drug_indication_icd
    ON masterdata.drug_indication (icd_code) WHERE deleted_at IS NULL;
