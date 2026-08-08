-- DEV BACKFILL — stamp the validity window on prescriptions written before it existed.
--
-- `pharmacy.prescription.expires_at` has been in the schema since migration 0001 and the dispensing rule has
-- always honoured it, but nothing WROTE it until the validity work landed. Four of the eight prescriptions in
-- this environment predate that, so they carry no expiry — and the counter renders a header with a blank
-- where "Valid until" belongs, on a field that is a safety control.
--
-- THE FIGURE IS DERIVED, NOT INVENTED. `submitted_at + ValidityPolicy.DefaultDays` (10) is exactly what the
-- creation path writes today when the tenant has configured nothing else. That is the same arithmetic, applied
-- to rows that missed it — not a number chosen to make a screen look complete.
--
-- IT DOES NOT RETROACTIVELY EXPIRE ANYTHING. Every affected row was submitted within the last ten days, so
-- each stays dispensable, which is the state the counter already treats them as. A backfill that silently
-- lapsed a live prescription would take medicine away from someone on the strength of a data fix.
--
-- WHAT IS DELIBERATELY NOT BACKFILLED: `prescription_line.duration_days`, missing on three lines. Duration is
-- a clinical instruction, not a derivable one — "20 tablets BD" is ten days only if the quantity IS the
-- course, and pack sizes routinely break that. It also feeds the dose/duration reference check, so a
-- plausible guess there is a safety fiction rather than a gap. Those lines read "duration not recorded",
-- which is true and is what the platform does everywhere else an unrecorded fact would otherwise look clean.

UPDATE pharmacy.prescription
SET expires_at = submitted_at + INTERVAL '10 days'
WHERE expires_at IS NULL
  AND submitted_at IS NOT NULL;

SELECT count(*) FILTER (WHERE expires_at IS NOT NULL) AS with_expiry,
       count(*) FILTER (WHERE expires_at IS NULL)     AS without_expiry
FROM pharmacy.prescription;
