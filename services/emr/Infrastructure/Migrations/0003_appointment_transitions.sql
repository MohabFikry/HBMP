-- emr-service — Phase 3.2 appointment transitions: reschedule / cancel / no-show. Slot "release" is
-- implicit — moving an appointment off a slot (reschedule) or out of an active status (cancel/no-show)
-- frees it, because ux_appointment_active_slot only counts Booked/CheckedIn holds. This migration adds the
-- idempotency store that makes the mutating endpoints replay-safe (Idempotency-Key).

CREATE SCHEMA IF NOT EXISTS emr;

-- Records the outcome of a processed mutation so a replayed Idempotency-Key is a no-op.
CREATE TABLE IF NOT EXISTS emr.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,          -- reschedule | cancel | no-show
    appointment_id  uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
