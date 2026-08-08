-- emr-service — 0013 the patient's display name on the appointment itself. ADDITIVE.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 0012 gave the reception dashboard names by reading the QUEUE TICKET, which captures `display_name` at
-- check-in. That covers people who have arrived and nobody else, so the appointments board — the list the desk
-- works from all day, mostly BEFORE anyone arrives — still had only a masked token to show.
--
-- Reception and the call centre are entitled to the name: the desk greets the person and arranges their
-- journey, and the call centre is speaking to them. A masked token does neither, and forces the operator to
-- open a second screen to find out who "•••4821" is.
--
-- emr holds no beneficiary demographics and must not fetch them from a sibling to fill this in — that is the
-- aggregation shape the platform forbids. So the name is captured at the moment the operator ALREADY has it,
-- exactly as check-in has always done: the booking request carries it, and it is stored here.
--
-- DELIBERATELY minimum-necessary and deliberately a SNAPSHOT:
--   * a display name only — no DOB, no identifiers, nothing that makes this a demographic record;
--   * not kept in sync with patient-service, because this is what the appointment was booked under. A name
--     that silently changed under a booked appointment would make the desk's list disagree with the card the
--     patient is holding.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS beneficiary_name text;

COMMENT ON COLUMN emr.appointment.beneficiary_name IS
    'Display name captured at booking (minimum-necessary snapshot; NOT synced with patient-service). Reception '
    'and the call centre are entitled to it; emr never fetches it from a sibling.';

-- Backfill from the queue tickets that already hold one, so appointments booked before this column existed
-- are not stuck showing a token forever once their patient has arrived.
UPDATE emr.appointment a
   SET beneficiary_name = q.display_name
  FROM emr.appointment_queue q
 WHERE q.appointment_id = a.appointment_id
   AND a.beneficiary_name IS NULL
   AND q.display_name IS NOT NULL;
