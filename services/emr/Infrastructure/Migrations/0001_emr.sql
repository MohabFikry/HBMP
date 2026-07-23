-- emr-service schema (Phase 2.3). Visit-gating creates only an encounter shell + a clinician queue
-- entry; SOAP / diagnoses / orders / prescriptions arrive in phase 4.
CREATE SCHEMA IF NOT EXISTS emr;

CREATE TABLE IF NOT EXISTS emr.encounter (
    encounter_id    uuid PRIMARY KEY,
    encounter_no    text NOT NULL,
    beneficiary_id  uuid NOT NULL,
    appointment_id  uuid,
    provider_id     uuid,
    status          text NOT NULL DEFAULT 'InProgress',
    started_at      timestamptz NOT NULL DEFAULT now(),
    idempotency_key text,
    created_by      text
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_encounter_no ON emr.encounter (encounter_no);
-- Idempotent creation: at most one encounter per Idempotency-Key.
CREATE UNIQUE INDEX IF NOT EXISTS ux_encounter_idem
    ON emr.encounter (idempotency_key) WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS emr.queue_entry (
    queue_entry_id uuid PRIMARY KEY,
    encounter_id   uuid NOT NULL,
    beneficiary_id uuid NOT NULL,
    provider_id    uuid,
    state          text NOT NULL DEFAULT 'Waiting',
    enqueued_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_queue_entry_encounter ON emr.queue_entry (encounter_id);

-- Per-year monotonic Encounter No counter (ENC-YYYY-NNNNNN).
CREATE TABLE IF NOT EXISTS emr.encounter_seq (
    year       int PRIMARY KEY,
    last_value int NOT NULL
);
