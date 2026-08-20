-- emr-service — 0026: record WHICH medicine a medication-history row is for, in words.
--
-- emr.medication_history has stored drug_id, a masterdata uuid, and nothing else identifying — the same gap
-- 0020 closed for emr.allergy, in the table one over. Nobody noticed, because until 32.2 nothing ever wrote
-- a row and nothing ever read one: the POST had no caller anywhere on the platform.
--
-- It has two readers now, and both need the name:
--   1. The encounter's current-medications list, where a row rendering "4f2b8c1a-…" is the safety control
--      0020's own header describes as one a clinician learns to ignore.
--   2. The prescribing interaction check (32.1), whose warning reads "interacts with St John's Wort, which
--      the patient is already taking (SelfReported)". Without a name that sentence has a uuid in it, at the
--      moment a prescriber is deciding whether to change a prescription.
--
-- SNAPSHOT at write time, not joined at read time — 0020's two reasons hold unchanged here. The interaction
-- check runs on every keystroke-debounced compose, so a fan-out to masterdata per medication per run buys
-- nothing at that price; and what belongs in a clinical record is the medicine the clinician selected and
-- saw, not whatever masterdata renames that row to later.
--
-- Nullable, deliberately, and there is nothing to backfill: no row has ever been written. NULL means "no
-- name was captured", and readers fall back to "(unspecified)" rather than showing the uuid.
ALTER TABLE emr.medication_history ADD COLUMN IF NOT EXISTS drug_name varchar(160);

COMMENT ON COLUMN emr.medication_history.drug_name IS
    'Drug name as resolved from masterdata at the moment of recording. NULL when no name was captured.';
