-- ============================================================================================================
-- 0006 — say WHAT was prescribed and WHO prescribed it, in words.
-- ============================================================================================================
-- A prescription stored a drug uuid and a prescriber uuid and nothing a human can read, so the dispensing
-- screen had nothing to show and said so literally: every line rendered as "Medication · 20mg · PO · od" and
-- every row's prescriber column as the word "Prescriber". A pharmacist cannot check a prescription against
-- the packet in their hand from a uuid, and "Medication" is not a name — it is the absence of one, printed.
--
-- Both are SNAPSHOTS taken at submission, for the same two reasons as emr's allergen_display (its 0020):
--
--   1. The dispensing queue is a worklist. Resolving N drug names against masterdata on every render, for
--      every prescription, buys nothing at that price.
--   2. It is the more honest record. What belongs in a prescription is the product the prescriber SELECTED
--      and the name they saw, not whatever the catalogue is renamed to afterwards. A trade name that changes
--      between prescribing and dispensing is precisely the case where the recorded value should not move.
--
-- Nullable, deliberately: rows written before this migration captured no name, and NULL says exactly that.
-- Readers render "(not recorded)" rather than substituting the uuid — a dispensing screen that displays an
-- identifier where a drug name belongs has stopped communicating, which is the defect being fixed.
--
-- prescriber_name is the token's display name at the moment of writing, which is the only name the
-- prescribing service has: pharmacy-service holds no practitioner directory, and giving it one so a queue
-- could print a name would couple dispensing to the provider domain for a label.

ALTER TABLE pharmacy.prescription      ADD COLUMN IF NOT EXISTS prescriber_name varchar(160);
ALTER TABLE pharmacy.prescription_line ADD COLUMN IF NOT EXISTS drug_name       varchar(200);

COMMENT ON COLUMN pharmacy.prescription.prescriber_name IS
    'Prescriber display name, captured at submission. NULL for rows written before 0006.';
COMMENT ON COLUMN pharmacy.prescription_line.drug_name IS
    'Drug name as master data gave it at the moment of prescribing. NULL for rows written before 0006.';

-- ── The prescriber_id defect this migration exposes ─────────────────────────────────────────────────────────
-- Every existing row has prescriber_id = 00000000-0000-0000-0000-000000000000, because the submit endpoint
-- populated it from `me.Principal.ProviderId` — the PROVIDER the caller belongs to, not the practitioner who
-- wrote the prescription. A doctor's token carries no provider_id (doctors are practitioner-scoped), so the
-- parse failed and Guid.Empty was written, silently, on every prescription this platform has ever issued.
--
-- The write path is corrected in the same change as this migration. Existing rows are backfilled from
-- created_by, which is the subject of the token that submitted them — the value prescriber_id should have
-- held all along.
UPDATE pharmacy.prescription
   SET prescriber_id = created_by::uuid
 WHERE prescriber_id = '00000000-0000-0000-0000-000000000000'
   AND created_by IS NOT NULL
   AND created_by ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
