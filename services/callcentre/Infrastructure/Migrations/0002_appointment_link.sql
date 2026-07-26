-- callcentre-service — Phase 15.3 appointment-action linkage. The Call Centre reuses the emr appointment engine;
-- this table records only the LINK between an emr appointment change and the call that produced it (we never add a
-- call column to emr's appointment table). Append-only, auditable.

CREATE TABLE IF NOT EXISTS callcentre.appointment_link (
    link_id        uuid PRIMARY KEY,
    interaction_id uuid NOT NULL REFERENCES callcentre.call_interaction(interaction_id),
    call_ref       varchar(20) NOT NULL,
    tenant_id      text NOT NULL,
    beneficiary_id uuid NOT NULL,
    appointment_id uuid NOT NULL,
    action         text NOT NULL CHECK (action IN ('Book','Reschedule','Cancel')),
    cancel_reason  text CHECK (cancel_reason IN ('PatientRequest','PatientUnwell','TransportIssue',
                        'Rescheduling','ClinicClosure','DuplicateBooking','Other')),
    branch_id      text,
    created_by     text,
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_apptlink_interaction ON callcentre.appointment_link (interaction_id);
CREATE INDEX IF NOT EXISTS ix_apptlink_appointment ON callcentre.appointment_link (appointment_id);
