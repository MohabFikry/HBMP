-- pharmacy-service — 0018 the numbers a prescription was WRITTEN from (31.5). Design 45 §6.
--
-- ============================================================================================================
-- THE RECORD KEPT THE SENTENCE AND THREW AWAY THE NUMBERS
-- ============================================================================================================
-- `doseAmount` and `timesPerDay` arrive on every line of every prescription. The daily-dose rule compares
-- against them, the quantity check divides by them, the chronic allocation splits a course by them — and then
-- they were dropped on the floor. What the line kept was `dose`: a SENTENCE this application formatted,
-- "1 Tablet x 3/day".
--
-- Three things that cost, in ascending order of seriousness:
--
--   * A prescription cannot be COPIED without retyping the dose, because there is no dose to copy.
--   * A prescription cannot be RE-CHECKED. Re-running the daily-dose rule over a written script — after a
--     label update, after a new interaction is published — needs the numbers it was graded on, and the only
--     way back to them is to parse the display string this app printed. Reading clinical numbers out of a
--     string formatted for humans is a defect waiting for the first locale that formats it differently.
--   * The sentence and the numbers can disagree and nothing would know. `dose` is derived from them at
--     compose time and then stands alone; there is nothing left to derive it from, so nothing can check it.
--
-- EXPAND ONLY. Both nullable, no default, no backfill: a line written before this reads NULL, which is the
-- honest answer for a row whose numbers were never kept. The sig stays exactly as it is — it is what a
-- pharmacist reads at the counter, and these do not replace it.
ALTER TABLE pharmacy.prescription_line
    ADD COLUMN IF NOT EXISTS dose_amount   numeric(12,3) NULL,
    ADD COLUMN IF NOT EXISTS times_per_day integer       NULL;

ALTER TABLE pharmacy.prescription_line DROP CONSTRAINT IF EXISTS ck_rx_line_dose_positive;  -- migrate-compat: contract-ok (the constraint is being ADDED; the DROP is the idempotency guard for a re-run, and no previous build writes either column)
ALTER TABLE pharmacy.prescription_line
    ADD CONSTRAINT ck_rx_line_dose_positive CHECK (
        (dose_amount IS NULL OR dose_amount > 0)
        AND (times_per_day IS NULL OR times_per_day > 0));

COMMENT ON COLUMN pharmacy.prescription_line.dose_amount IS
    '31.5 — how much per administration, in the drug''s prescribing unit. The number the daily-dose rule and '
    'the quantity check were run against. NULL on lines written before 31.5 — never 1, which would assert a '
    'dose nobody wrote.';
COMMENT ON COLUMN pharmacy.prescription_line.times_per_day IS
    '31.5 — administrations per day. See dose_amount. NULL means the record does not hold it.';

-- ============================================================================================================
-- AND THEY ARE SIGNED CLINICAL CONTENT, so the database freezes them like everything else
-- ============================================================================================================
-- 0013's guard enumerates the frozen columns, which means a column added later is mutable until it is named
-- here. Two are added now, and `quantity_unit` (0017) is added with them: it says what a signed quantity
-- COUNTS, and editing that in place changes what the quantity means without changing the quantity — the
-- quietest possible way to alter a prescription.
--
-- Replaced rather than extended in place: `CREATE OR REPLACE` in a later file wins, because migrations run in
-- filename order and this one runs after 0013 on every application.
CREATE OR REPLACE FUNCTION pharmacy.guard_rx_line_signed()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.prescription_id     IS DISTINCT FROM NEW.prescription_id
       OR OLD.drug_id          IS DISTINCT FROM NEW.drug_id
       OR OLD.drug_name        IS DISTINCT FROM NEW.drug_name
       OR OLD.dose             IS DISTINCT FROM NEW.dose
       OR OLD.dose_amount      IS DISTINCT FROM NEW.dose_amount
       OR OLD.times_per_day    IS DISTINCT FROM NEW.times_per_day
       OR OLD.route            IS DISTINCT FROM NEW.route
       OR OLD.frequency        IS DISTINCT FROM NEW.frequency
       OR OLD.quantity_prescribed IS DISTINCT FROM NEW.quantity_prescribed
       OR OLD.quantity_unit    IS DISTINCT FROM NEW.quantity_unit
       OR OLD.duration_days    IS DISTINCT FROM NEW.duration_days
       OR OLD.refills_allowed  IS DISTINCT FROM NEW.refills_allowed THEN
        RAISE EXCEPTION
            'prescription line % is signed clinical content and can never be edited in place — supersede it (design 46 §1)',
            OLD.prescription_line_id USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.version_no IS DISTINCT FROM NEW.version_no
       OR OLD.supersedes_id IS DISTINCT FROM NEW.supersedes_id
       OR OLD.root_line_id  IS DISTINCT FROM NEW.root_line_id THEN
        RAISE EXCEPTION 'the version chain of line % is immutable', OLD.prescription_line_id
            USING ERRCODE = 'raise_exception';
    END IF;

    IF OLD.status IN ('Cancelled','Superseded') AND NEW.status IS DISTINCT FROM OLD.status THEN
        RAISE EXCEPTION 'line % is %; it cannot be reinstated — write a new prescription',
            OLD.prescription_line_id, OLD.status USING ERRCODE = 'raise_exception';
    END IF;

    RETURN NEW;
END $$;
