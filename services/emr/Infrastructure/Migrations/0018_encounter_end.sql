-- ============================================================================================================
-- 0018 — a visit can END.
-- ============================================================================================================
-- `encounter.status` has allowed 'Completed' since 0001 and `AppointmentWorkflow` has listed
-- CheckedIn → Completed with the comment "encounter closed (phase 4)" since phase 3. Neither has ever
-- happened: nothing in this platform writes either value.
--
-- The visible consequence is on the doctor's own day list. "Start visit" is offered for any appointment in
-- CheckedIn, and a finished consultation stayed CheckedIn forever — so the button that means "I am seeing
-- this patient now" was still being offered for a patient who had been seen, signed off and sent home. There
-- was no way to tell a clinic's remaining work from its finished work, on any screen.
--
-- The two columns below are what makes a closed visit answerable rather than merely flagged: `started_at`
-- alone gives a consultation no duration, and a status with no time and no author cannot be put on a
-- timeline, which is precisely what the appointment's history is for.

ALTER TABLE emr.encounter ADD COLUMN IF NOT EXISTS ended_at timestamptz;
ALTER TABLE emr.encounter ADD COLUMN IF NOT EXISTS ended_by text;

COMMENT ON COLUMN emr.encounter.ended_at IS
    'When the clinician closed the visit. NULL while in progress. With started_at this is the consultation '
    'duration — the one operational fact a clinic cannot derive from anything else it stores.';
COMMENT ON COLUMN emr.encounter.ended_by IS
    'Subject id of the clinician who closed it. Display attribution, NOT the audit trail — that lives in '
    'audit-service behind audit:read, which no clinician holds.';

-- Existing rows keep NULL. Back-filling them with their start time, or with now(), would assert that a visit
-- ended at a moment nobody recorded — and every in-progress encounter in the system predates this column, so
-- the back-fill would close visits that are genuinely still open.

-- The doctor's day list asks "which of my checked-in appointments still need me", which after this change is
-- a status filter over the branch's appointments for a date. That read had no index of its own.
CREATE INDEX IF NOT EXISTS ix_encounter_appointment_status
    ON emr.encounter (appointment_id, status) WHERE appointment_id IS NOT NULL;
