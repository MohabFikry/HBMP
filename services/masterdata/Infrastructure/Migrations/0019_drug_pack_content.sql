-- masterdata-service — 0019 what a box HOLDS, in the unit the medicine is dosed in (31.3). Design 45 §6.
--
-- ============================================================================================================
-- WHY A SECOND NUMBER BESIDE pack_size
-- ============================================================================================================
-- `pack_size` is the catalogue's "Minor Units (total)", and it counts whatever the catalogue counts. For a box
-- of tablets that is tablets, and it is also the number a course is divided by. For everything measured it is
-- not:
--
--     a 120 ml bottle of syrup      pack_size = 1     ← one bottle
--     a box of five insulin pens    pack_size = 5     ← five pens, dosed in IU
--     a 30 gm tube of cream         pack_size = 1     ← one tube
--
-- So the divisor was wrong for every liquid, cream and pen in the catalogue. A 210 ml course of syrup came out
-- as 210 packs, and a box of insulin pens could not be divided at all — the composer showed the raw IU with a
-- note saying boxes could not be counted for this product.
--
-- `pack_content` is the missing number: how many PRESCRIBING units one box holds. 24 tablets, 120 millilitres,
-- 30 grams, 1500 IU. It is derived at load time from the workbook's "Volume / Weight" and "Strength" columns
-- (libs/prescribing/PackUnitRules.ContentOf) and it is NULL wherever they do not say — the usual fill of an
-- insulin pen is three millilitres and the platform does not assume it, because a guessed box count is a
-- dispensing error that looks exactly like a correct one (invariant 8).
--
-- EXPAND ONLY. A nullable column with no default and no backfill: a previous-build instance neither reads nor
-- writes it, and every row starts NULL, which is the state that reports NotChecked.
ALTER TABLE masterdata.drug
    ADD COLUMN IF NOT EXISTS pack_content numeric(14,3) NULL;

ALTER TABLE masterdata.drug DROP CONSTRAINT IF EXISTS ck_drug_pack_content_positive;  -- migrate-compat: contract-ok (the constraint is being ADDED; the DROP is the idempotency guard for a re-run, and no previous build writes this column at all)
ALTER TABLE masterdata.drug
    ADD CONSTRAINT ck_drug_pack_content_positive CHECK (pack_content IS NULL OR pack_content > 0);

COMMENT ON COLUMN masterdata.drug.pack_content IS
    '31.3 — how many PRESCRIBING units one box holds: 24 tablets, 120 ml, 30 gm, 1500 IU. THE divisor for '
    'every quantity question. Equal to pack_size for countable forms and different for every measured one, '
    'which is why it exists. NULL means the workbook records no volume, weight or concentration to derive it '
    'from — never a default (design 45 §6, invariant 8).';

COMMENT ON COLUMN masterdata.drug.pack_size IS
    '29.6 — the catalogue''s "Minor Units (total)", as recorded. Kept because the price-per-unit comparison '
    'is defined against it. Read pack_content, not this, to convert a course into boxes: for a 120 ml bottle '
    'of syrup this is 1.';
