-- masterdata-service — 0018: widen the prescribing-unit vocabulary to the forms the catalogue actually holds.
--
-- ============================================================================================================
-- WHY THIS IS A MIGRATION AND NOT A LOADER FIX
-- ============================================================================================================
--
-- Design 45 §6 fixed the vocabulary at thirteen values, derived from the dosage forms the design anticipated.
-- The real Egyptian catalogue holds 38 distinct dosage forms the list does not cover — 2,495 products — and
-- every one of them loaded with prescribing_unit NULL. A null unit sets `unit_data_incomplete`, which makes
-- the quantity check report NotChecked, which means those 2,495 products could not be written as a chronic
-- script at all. The vocabulary was not wrong; it was short.
--
-- It stays CLOSED, and it stays enforced here rather than in application code, because the value is what the
-- prescriber reads beside the dose field. Eight words are added, and only where the catalogue needs a word it
-- does not already have: a vaccine is supplied AS a vial and a herbal "bag" is a tea bag, so neither gets one.
--
--   Syringe    196 rows   prefilled syringes — one item, given whole
--   Bar        118 rows   medicated soap; the pack IS the bar
--   Lozenge     57 rows
--   Cartridge   36 rows   insulin penfills sold as cartridges rather than as a pen
--   Gummy       84 rows
--   Pessary     31 rows
--   Enema       13 rows
--   Dressing     9 rows
--
-- EXPAND-ONLY. Every value the old constraint permitted is still permitted, so a rollback of the application
-- leaves no row failing the old rule EXCEPT ones written after this ran — which is why the contract half
-- (narrowing anything) is deliberately absent. NULL remains permitted and remains meaningful: it is "the
-- sheet did not say", reported as NotChecked naming the field, never guessed.

ALTER TABLE masterdata.drug DROP CONSTRAINT IF EXISTS ck_drug_prescribing_unit;  -- migrate-compat: contract-ok (widening a CHECK in place; the old thirteen values are a strict subset of the new twenty-one, so no existing row can fail it and a previous-build instance keeps writing values this still permits)
ALTER TABLE masterdata.drug
    ADD CONSTRAINT ck_drug_prescribing_unit CHECK (
        prescribing_unit IS NULL OR prescribing_unit IN (
            -- design 45 §6, unchanged
            'Tablet','Capsule','ML','Puff','Spray','IU','Drop','Sachet','Suppository',
            'Vial','Ampoule','Patch','Gram',
            -- added by 0018, from the forms the loaded catalogue actually carries
            'Syringe','Cartridge','Lozenge','Pessary','Gummy','Bar','Enema','Dressing'));

-- ============================================================================================================
-- pack_unit — free text from the source, so it cannot be sized for the forms the design imagined
-- ============================================================================================================
--
-- `pack_unit` stores the catalogue's own `Dosage Form` verbatim, and 0016 sized it varchar(16) alongside
-- `prescribing_unit`, which is a closed vocabulary and genuinely does fit. The real column does not:
-- "prefilled syringe" is 17 characters and "effervescent tablet" is 19, so the very first load carrying pack
-- data failed on 22001 before writing a single drug. It never surfaced earlier because nothing had loaded
-- these columns since 0016 added them.
--
-- WIDEN ONLY. No value that fitted before stops fitting, so this is safe to apply ahead of the code.
ALTER TABLE masterdata.drug ALTER COLUMN pack_unit TYPE varchar(64);  -- migrate-compat: contract-ok (WIDENING a varchar, not narrowing: every value that fitted varchar(16) still fits, so a previous-build instance reading or writing this column is unaffected and no deploy order matters)

COMMENT ON COLUMN masterdata.drug.pack_unit IS
    'The source catalogue''s own Dosage Form, verbatim and unnormalised — free text, which is why it is not '
    'sized to the prescribing_unit vocabulary. Read prescribing_unit for the value the platform reasons with.';

COMMENT ON COLUMN masterdata.drug.prescribing_unit IS
    'design 45 §6 (widened by migration 0018) — the unit a doctor prescribes in, and the word shown beside '
    'the dose field. CLOSED vocabulary. NULL is permitted and means "the source did not say": the quantity '
    'check reports NotChecked NAMING the field rather than guessing (invariant 8).';
