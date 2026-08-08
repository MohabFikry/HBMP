-- pharmacy-service — 0014: a refill window can be SUPERSEDED (phase 30 Gate 3, design 46 §4).
--
-- ============================================================================================================
-- WHY A NEW WINDOW STATUS
-- ============================================================================================================
-- Amending a chronic script's duration or frequency supersedes the LINE (0013): the original is never
-- mutated and a new version is inserted beside it. The original's schedule stays exactly where it is —
-- nothing is reparented, nothing is copied — and the successor gets a fresh schedule for the remaining
-- duration. See docs/superpowers/specs/2026-08-07-chronic-amendment-design.md for the three options and why
-- this one.
--
-- That leaves the original line holding windows that will now never be collected. Without a terminal status
-- for them the SWEEPER finds them past their close date and records them as `Missed` — a FORFEITURE for a
-- collection that was never owed, on a line the prescriber replaced. Phantom forfeitures, on the report that
-- answers "how much benefit did members lose by not attending".
--
-- The sweeper needs no code change: its partial index and its query both filter `status IN ('Pending','Open')`,
-- so a terminal status removes these rows from its sight by construction.
--
-- ============================================================================================================
-- WHY NOT REUSE 'Missed' OR ADD 'Cancelled'
-- ============================================================================================================
-- `Missed` says the patient did not attend. They did not fail to attend — the window stopped existing.
-- `Cancelled` would say somebody withdrew their medicine, which is what the CANCEL path does and is a
-- different clinical fact with a different reason code. A superseded window was REPLACED, and the successor
-- carries the quantity forward. Collapsing any of the three into another makes the refill report wrong in a
-- way nobody reading it could detect.
--
-- Additive + idempotent.

DO $$  -- migrate-compat: contract-ok (WIDENS the status CHECK to admit 'Superseded'; the old, narrower constraint is replaced in the same migration, so no value that was legal before becomes illegal)
DECLARE c record;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'pharmacy.prescription_dispense_window'::regclass AND contype = 'c'
               AND pg_get_constraintdef(oid) LIKE '%status%'
               AND pg_get_constraintdef(oid) LIKE '%Pending%'
    LOOP
        EXECUTE format('ALTER TABLE pharmacy.prescription_dispense_window DROP CONSTRAINT %I', c.conname);
    END LOOP;
END $$;

ALTER TABLE pharmacy.prescription_dispense_window
    ADD CONSTRAINT ck_window_status CHECK (
        status IN ('Pending','Open','Dispensed','PartiallyDispensed','Missed','Blocked','Superseded'));

-- A superseded window records WHICH amendment replaced it, so the refill history reads as one chain rather
-- than as a schedule that stops without explanation.
ALTER TABLE pharmacy.prescription_dispense_window
    ADD COLUMN IF NOT EXISTS superseded_by_amendment_id uuid NULL
        REFERENCES pharmacy.line_amendment(amendment_id);

ALTER TABLE pharmacy.prescription_dispense_window
    -- migrate-compat: contract-ok (idempotency drop of a constraint THIS migration creates two lines below —
    -- nothing that existed before this file ran is being removed)
    DROP CONSTRAINT IF EXISTS ck_window_superseded_names_its_amendment;
ALTER TABLE pharmacy.prescription_dispense_window
    ADD CONSTRAINT ck_window_superseded_names_its_amendment CHECK (
        status <> 'Superseded' OR superseded_by_amendment_id IS NOT NULL) NOT VALID;

-- A COLLECTED window is never superseded. What was handed over is a fact, and a fact does not stop existing
-- because the prescriber later shortened the course — this is invariant 2 expressed on the schedule.
ALTER TABLE pharmacy.prescription_dispense_window
    -- migrate-compat: contract-ok (idempotency drop of a constraint THIS migration creates two lines below)
    DROP CONSTRAINT IF EXISTS ck_window_superseded_was_not_collected;
ALTER TABLE pharmacy.prescription_dispense_window
    ADD CONSTRAINT ck_window_superseded_was_not_collected CHECK (
        status <> 'Superseded' OR dispensed_quantity = 0) NOT VALID;

COMMENT ON COLUMN pharmacy.prescription_dispense_window.superseded_by_amendment_id IS
    'The amendment that replaced this window. Set only on Superseded rows — a window the prescriber''s '
    'duration/frequency change made obsolete. NOT Missed (the patient did not fail to attend) and NOT '
    'Cancelled (nobody withdrew the medicine): it was replaced, and the successor line carries the quantity.';
