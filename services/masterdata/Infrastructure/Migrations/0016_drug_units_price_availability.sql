-- masterdata-service — 0016 prescribing unit, pack size, splittability (29.6) + lowest-price and
-- availability (29.7). Design 45 §6, §7.

-- ============================================================================================================
-- 29.6 — the three facts the drug master did not have
-- ============================================================================================================
ALTER TABLE masterdata.drug
    ADD COLUMN IF NOT EXISTS prescribing_unit      varchar(16) NULL,
    ADD COLUMN IF NOT EXISTS pack_size             numeric(14,3) NULL,
    ADD COLUMN IF NOT EXISTS pack_unit             varchar(16) NULL,
    ADD COLUMN IF NOT EXISTS is_pack_splittable    boolean NULL,
    ADD COLUMN IF NOT EXISTS unit_data_incomplete  boolean NOT NULL DEFAULT true;

-- The vocabulary from design 45 §6. NULL is permitted and is NOT a failure state — it is "the sheet did not
-- say", which the quantity check reports as NotChecked naming the field rather than guessing.
--
-- ============================================================================================================
-- ADDED ONLY IF ABSENT — and the DROP that used to precede it is gone (31.3)
-- ============================================================================================================
-- Migrations here are re-run from 0001 on every loader invocation and every service start, so each one has to
-- be safe to apply to a database that already has all of them. This one was not. `DROP` then `ADD` re-imposed
-- the ORIGINAL thirteen-value vocabulary, and 0018 widened it to twenty-one — so the second run over a
-- database holding any row loaded under 0018 failed:
--
--     23514: check constraint "ck_drug_prescribing_unit" of relation "drug" is violated by some row
--
-- and the whole load aborted before touching a table. The first load succeeded, which is what made it
-- invisible: the failure needs 0018's data to exist, so it appears only on the run after the one that worked.
--
-- Guarding the ADD instead of dropping first makes the migration a no-op wherever the constraint already
-- exists — in whatever form the latest migration left it — and still gives a fresh database this vocabulary
-- at 0016 and the wider one at 0018. Any future narrowing of a CHECK that a later migration widens has the
-- same trap in it.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_drug_prescribing_unit' AND conrelid = 'masterdata.drug'::regclass)
    THEN
        ALTER TABLE masterdata.drug
            ADD CONSTRAINT ck_drug_prescribing_unit CHECK (
                prescribing_unit IS NULL OR prescribing_unit IN (
                    'Tablet','Capsule','ML','Puff','Spray','IU','Drop','Sachet','Suppository',
                    'Vial','Ampoule','Patch','Gram'));
    END IF;
END $$;

ALTER TABLE masterdata.drug DROP CONSTRAINT IF EXISTS ck_drug_pack_size_positive;
ALTER TABLE masterdata.drug
    ADD CONSTRAINT ck_drug_pack_size_positive CHECK (pack_size IS NULL OR pack_size > 0);

-- `unit_data_incomplete` DEFAULTS TRUE, and that is the point: every existing row starts incomplete, because
-- every existing row IS. A default of false would silently assert that 14,000 drugs have unit data nobody has
-- loaded, and the quantity check would return confident answers computed from nothing.
COMMENT ON COLUMN masterdata.drug.unit_data_incomplete IS
    '29.6 — true until the loader has populated prescribing_unit, pack_size and is_pack_splittable. Defaults '
    'TRUE so an unloaded row reports NotChecked rather than a guessed quantity (design 45 §6, invariant 8).';

-- ============================================================================================================
-- 29.7 — availability: THREE states, defaulting to Unknown
-- ============================================================================================================
--
-- "It must NOT default to 'unavailable'. A boolean defaulting to false would render the entire catalogue as
-- out of stock on day one, and prescribers would learn to ignore the indicator before it ever carried real
-- data." So: a three-valued column, not a boolean, and the default is the honest one.
ALTER TABLE masterdata.drug
    ADD COLUMN IF NOT EXISTS availability varchar(16) NOT NULL DEFAULT 'Unknown';

ALTER TABLE masterdata.drug DROP CONSTRAINT IF EXISTS ck_drug_availability;
ALTER TABLE masterdata.drug
    ADD CONSTRAINT ck_drug_availability CHECK (availability IN ('Available','Unavailable','Unknown'));

COMMENT ON COLUMN masterdata.drug.availability IS
    '29.7 — Available / Unavailable / Unknown (default). Unknown renders NOTHING in the UI: no badge and no '
    'warning. Only a positive Unavailable shows a badge (design 45 §7, invariant 10).';

-- ============================================================================================================
-- 29.7 — the lowest-price label: DERIVED, never authored
-- ============================================================================================================
--
-- "The flag is DERIVED, not authored — recomputed whenever prices load, with a computed_at so a stale label
-- is detectable." A hand-set flag would go stale the first time a price moved and there would be no way to
-- tell that it had.
ALTER TABLE masterdata.drug
    ADD COLUMN IF NOT EXISTS is_lowest_price        boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS price_per_unit         numeric(14,6) NULL,
    ADD COLUMN IF NOT EXISTS lowest_price_group_key text NULL,
    ADD COLUMN IF NOT EXISTS lowest_price_computed_at timestamptz NULL;

COMMENT ON COLUMN masterdata.drug.price_per_unit IS
    '29.7 — price_egp / pack_size. THE comparison basis (design 45 §7): a 20-tablet pack at 100 EGP is MORE '
    'expensive per tablet than a 30-tablet pack at 120 EGP, so comparing pack prices would label the first as '
    'cheaper and actively mislead a prescriber trying to save a beneficiary money. NULL where pack_size is '
    'unknown — and a NULL is NEVER labelled, because falling back to pack price is the exact error this '
    'column exists to prevent.';

COMMENT ON COLUMN masterdata.drug.lowest_price_group_key IS
    '29.7 — the equivalence group: active ingredient + strength + dosage form. Ingredient ALONE is not a valid '
    'group — a 500 mg tablet and a 250 mg/5 mL syrup share an ingredient and cannot be price-compared.';

-- The grouping key is indexed (design 45 §7) — the recompute groups by it over the whole catalogue, and the
-- combobox reads one group per keystroke.
CREATE INDEX IF NOT EXISTS ix_drug_lowest_price_group
    ON masterdata.drug (lowest_price_group_key) WHERE lowest_price_group_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_drug_availability
    ON masterdata.drug (availability) WHERE availability <> 'Unknown';
