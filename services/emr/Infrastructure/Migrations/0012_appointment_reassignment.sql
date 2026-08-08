-- emr-service — 0012 appointments left orphaned by a practitioner leaving a branch. ADDITIVE.
--
-- ============================================================================================================
-- THE GAP THIS CLOSES
-- ============================================================================================================
-- provider-service can end a clinician's assignment to a branch (14.5, `POST /practitioners/{id}/branches/revoke`).
-- That immediately makes `serves-branch` false, so emr's two booking gates refuse NEW slots and NEW bookings
-- there. What it could not do is anything about appointments ALREADY booked with that doctor at that branch:
-- provider-service does not own appointments and cannot see them.
--
-- So until now the outcome was a patient who kept an appointment with a doctor who no longer works at that
-- clinic, and nobody found out until they arrived. The event existed (`PractitionerBranchRevoked`) and nothing
-- consumed it.
--
-- ============================================================================================================
-- WHY A FLAG AND NOT AN AUTOMATIC ACTION
-- ============================================================================================================
-- Two automatic options were rejected:
--
--   * CANCEL the appointments — destroys a real patient's booked care because of an administrative change,
--     with no human deciding and, for a refugee beneficiary who may have travelled and taken unpaid leave,
--     no way to undo the cost.
--   * UNASSIGN the doctor — silently changes who the patient was told they would see, and the appointment
--     still LOOKS healthy on every board, which is the failure mode that hides itself.
--
-- Neither is a decision a background consumer should be making. What reception actually needs is to KNOW, so
-- they can ring the patient and rebook. So the appointment is left completely intact and simply marked, and
-- the boards surface the mark. The reconciliation is a human one; this column is what makes it possible.

ALTER TABLE emr.appointment ADD COLUMN IF NOT EXISTS reassignment_needed_at timestamptz;

COMMENT ON COLUMN emr.appointment.reassignment_needed_at IS
    'Set when the assigned practitioner stopped serving this appointment''s branch (PractitionerBranchRevoked). '
    'The appointment is otherwise untouched — reception decides whether to reassign, rebook or cancel.';

-- Partial: the flagged set is tiny and transient, and reception queries exactly "which ones still need doing".
CREATE INDEX IF NOT EXISTS ix_appointment_reassignment
    ON emr.appointment (branch_id, scheduled_start)
    WHERE reassignment_needed_at IS NOT NULL;

-- The at-least-once ledger for this service's consumers. Same shape as policy-service's: a redelivered event
-- id short-circuits, so a broker retry cannot re-flag rows a receptionist has already cleared.
CREATE TABLE IF NOT EXISTS emr.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT ON emr.processed_event TO hbmp_app;
    END IF;
END $$;
