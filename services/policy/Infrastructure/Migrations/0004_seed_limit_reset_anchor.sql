-- policy-service — 0004 anchor every resettable limit to its own reset window (phase 18.A3 / audit R2 X10).
--
-- IsResetDue() used to treat last_reset_on IS NULL as "a reset is due as soon as anything has been
-- consumed", so the first run of the reset job WIPED in-period consumption: a member who had used 8 of
-- their 10 annual visits was silently handed all 10 back. The predicate now requires a real anchor, and
-- new limits are seeded at creation — this backfills the rows that predate that.
--
-- The anchor is the start of the period CONTAINING the coverage's effective date, so the first boundary
-- crossing after this migration is a genuine reset and nothing before it is. Lifetime limits and
-- reset_period = 'None' stay NULL: they never reset. Additive + idempotent.

UPDATE policy.coverage_limit cl
SET last_reset_on = CASE cl.reset_period
        WHEN 'Monthly'   THEN date_trunc('month',   c.effective_from::timestamp)::date
        WHEN 'Quarterly' THEN date_trunc('quarter', c.effective_from::timestamp)::date
        WHEN 'Yearly'    THEN date_trunc('year',    c.effective_from::timestamp)::date
    END
FROM policy.coverage c
WHERE c.coverage_id = cl.coverage_id
  AND cl.last_reset_on IS NULL
  AND cl.reset_period <> 'None'
  AND cl.limit_type <> 'Lifetime';
