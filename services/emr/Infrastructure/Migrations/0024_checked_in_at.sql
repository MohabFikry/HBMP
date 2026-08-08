-- emr-service — 0024: the appointment records WHEN it was checked in (phase 30 Gate 5c, design 46 §7c).
--
-- ============================================================================================================
-- THE COLUMN DID NOT EXIST, AND THAT IS WHY WAITING TIME COULD NOT BE DERIVED
-- ============================================================================================================
-- Check-in has always set `status = 'CheckedIn'` and stamped `updated_at`. `updated_at` is overwritten by
-- every later transition — completing the visit, cancelling, a name backfill — so the only durable record of
-- the arrival moment was `queue_ticket.enqueued_at`, on a row that belongs to the reception board rather than
-- to the appointment, and which no longer exists once the ticket is cleared.
--
-- Design 46 §7c wants `visit started − checked in` on the timeline and on the branch dashboard. Deriving it
-- from `updated_at` would produce a number that silently degrades every time the appointment is touched
-- again: right on the day, wrong by the end of the week, and wrong in a direction nobody can detect from the
-- value itself. A metric that decays quietly is worse than one that is absent, because it gets used.
--
-- NULLABLE, deliberately and permanently. A walk-in taken straight into the room was never checked in, and
-- the timeline must say "no check-in recorded" rather than invent a moment. Absence of a record is not
-- evidence the step happened — the platform's standing rule, and §7c restates it for exactly this case.
--
-- NOT BACKFILLED. Every appointment checked in before this migration has no recorded arrival time, and there
-- is no honest source to reconstruct one from: `updated_at` is the very value this column exists because it
-- cannot be trusted. Those rows read as "no check-in recorded", which is true — the check-in happened, and
-- its time was not kept.
--
-- Additive + idempotent.

ALTER TABLE emr.appointment
    ADD COLUMN IF NOT EXISTS checked_in_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS checked_in_by text NULL;

-- The arrival is attributed, like every other timeline entry (design 46 §7c: "each entry carries its actor
-- and branch, consistent with every other timeline in the platform").
COMMENT ON COLUMN emr.appointment.checked_in_at IS
    'When reception recorded the arrival. NULL means NO CHECK-IN WAS RECORDED — a walk-in taken straight in, '
    'or a missed step — and readers must say so rather than assuming the visit-start moment. Never derived '
    'from updated_at, which every later transition overwrites (design 46 §7c).';

-- The dashboard''s waiting-time query: checked-in appointments in a branch over a period.
CREATE INDEX IF NOT EXISTS ix_appointment_checked_in
    ON emr.appointment (branch_id, checked_in_at)
    WHERE checked_in_at IS NOT NULL;
