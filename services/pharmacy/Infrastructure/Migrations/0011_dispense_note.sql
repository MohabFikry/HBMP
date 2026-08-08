-- pharmacy-service — 0011 a note the pharmacist writes at the counter, recorded with the dispense.
--
-- WHAT IT IS FOR, AND WHAT IT IS NOT.
--
-- The counter routinely knows something the record does not: the patient was given the last two boxes and is
-- coming back Thursday for the rest; the strip was damaged and replaced from a second lot; the carer collected
-- on the patient's behalf. None of that is a substitution and none of it is out-of-stock, so today it is said
-- out loud and lost. This is where it goes.
--
-- It is NOT a clinical note and must not become one. It rides on `dispense_event` — the append-only record of
-- one act at one counter — rather than on the prescription, because it describes THAT handover and not the
-- prescriber's decision. It is never read by the clinical checks and it is not a channel to the doctor: a
-- pharmacist who needs to tell a prescriber something has `RxLineOutOfStock`, the substitution reason, and the
-- approval team.
--
-- Nullable, additive, no backfill: rows written before this carry no note because no note was taken.

ALTER TABLE pharmacy.dispense_event
    ADD COLUMN IF NOT EXISTS note text;

-- 500 characters, enforced where the value lands rather than only in a form. A dispensing note is a sentence
-- or two; a field with no ceiling is one somebody eventually pastes a clinical history into, and this is the
-- one table on the platform that is append-only and can never be edited afterwards.
ALTER TABLE pharmacy.dispense_event DROP CONSTRAINT IF EXISTS ck_dispense_note_len;  -- migrate-compat: contract-ok (idempotent re-run guard for a constraint this same migration creates below; it has never existed on a previously-deployed build)
ALTER TABLE pharmacy.dispense_event
    ADD CONSTRAINT ck_dispense_note_len CHECK (note IS NULL OR char_length(note) <= 500);

COMMENT ON COLUMN pharmacy.dispense_event.note IS
    'What the pharmacist recorded about THIS handover — collection arrangements, a replaced lot, who '
    'collected. Not a clinical note, never read by the clinical checks, and not a channel to the prescriber.';
