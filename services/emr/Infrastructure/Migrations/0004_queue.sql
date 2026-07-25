-- emr-service — Phase 3.3 reception walk-in queue. Per (location, provider, optional doctor); fed by
-- checked-in appointments and walk-ins; carries only minimum-necessary display identity (NO EMR data).
-- Kept consistent with appointment status: cancel/no-show remove tickets; call-next → InConsultation.

CREATE SCHEMA IF NOT EXISTS emr;

CREATE TABLE IF NOT EXISTS emr.appointment_queue (
    queue_id         uuid PRIMARY KEY,
    appointment_id   uuid NOT NULL,
    beneficiary_id   uuid NOT NULL,
    provider_id      uuid NOT NULL,
    location_id      uuid NOT NULL,
    doctor_id        uuid,
    member_no        text,                 -- min-necessary display identity captured at check-in
    display_name     text,
    appointment_type text NOT NULL CHECK (appointment_type IN ('WalkIn','Scheduled','Referral','FollowUp')),
    priority         int  NOT NULL DEFAULT 0,
    state            text NOT NULL DEFAULT 'Waiting' CHECK (state IN ('Waiting','InConsultation','Done','Removed')),
    enqueued_at      timestamptz NOT NULL DEFAULT now(),
    called_at        timestamptz
);
CREATE INDEX IF NOT EXISTS ix_queue_scope ON emr.appointment_queue (location_id, provider_id, state);
-- One active ticket per appointment (Waiting/InConsultation).
CREATE UNIQUE INDEX IF NOT EXISTS ux_queue_active_appt
    ON emr.appointment_queue (appointment_id) WHERE state IN ('Waiting','InConsultation');
