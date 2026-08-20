-- pharmacy-service — 0017 what the prescribed quantity is COUNTED IN (31.3). Design 45 §6.
--
-- ============================================================================================================
-- WHY A NUMBER NEEDED A UNIT
-- ============================================================================================================
-- 31.3 made the composer's Quantity field a BOX COUNT wherever the catalogue records what a box holds, because
-- a box is what the patient carries home and what a pharmacy counts out. That is the right number to write.
-- It is the wrong number to store unlabelled:
--
--     Panadol Advance 500 MG 24 F.C.Tabs      quantity 1      <- one box, twenty-four tablets
--     Lantus Solostar 100 I.U./ML 5 Pens      quantity 2250   <- IU, because this box's contents are unrecorded
--
-- Both are correct and they are counted in different things, and the dispensing screen renders
-- `quantity_prescribed` as a bare figure. A pharmacist reading "1" against Panadol and handing over one
-- TABLET is a dispensing error that the record, as it stood, gave them no way to catch.
--
-- So the unit travels with the number. It is written at prescribing time from the same pack facts the
-- quantity was computed from, and it is a SNAPSHOT for the same reason `drug_name` is one (migration 0006):
-- what the catalogue says today must not change what a prescription written last year meant.
--
-- EXPAND ONLY. Nullable, no default, no backfill. A row written before this reads NULL, which the screens
-- render as no unit rather than as a guessed one — the same rule as every other absent fact here. A
-- previous-build instance neither reads nor writes it.
ALTER TABLE pharmacy.prescription_line
    ADD COLUMN IF NOT EXISTS quantity_unit varchar(24) NULL;

COMMENT ON COLUMN pharmacy.prescription_line.quantity_unit IS
    '31.3 — what quantity_prescribed COUNTS: "box"/"boxes" where the catalogue records a box''s contents, '
    'otherwise the prescribing unit ("tabs", "IU", "ml"). A snapshot taken at prescribing time, like '
    'drug_name. NULL on rows written before 31.3 and on any line whose unit the catalogue does not record — '
    'rendered as no unit, never as a default (invariant 8).';
