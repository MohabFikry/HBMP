-- pharmacy-service — Phase 4.3 prescriptions + referrals (22-data-dictionary §8, 23-state-machines §3/§4).
-- Create/submit + auto-approve/route here; dispensing (dispense_event) is phase 6. Enum values are CHECK-
-- constrained to the canonical sets exactly; prescription_line carries the dispense accumulator with the
-- 0 ≤ dispensed ≤ prescribed invariant so phase-6 dispense can only move it forward.

CREATE SCHEMA IF NOT EXISTS pharmacy;

CREATE TABLE IF NOT EXISTS pharmacy.rx_seq (
    year int PRIMARY KEY, last_value int NOT NULL
);
CREATE TABLE IF NOT EXISTS pharmacy.referral_seq (
    year int PRIMARY KEY, last_value int NOT NULL
);

CREATE TABLE IF NOT EXISTS pharmacy.prescription (
    prescription_id  uuid PRIMARY KEY,
    rx_no            varchar(20) NOT NULL UNIQUE,
    beneficiary_id   uuid NOT NULL,
    encounter_id     uuid NOT NULL,
    prescriber_id    uuid NOT NULL,
    authorization_id uuid,
    status           text NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Submitted','Approved','Rejected','PartiallyDispensed','Dispensed','Expired','Cancelled')),
    submitted_at     timestamptz,
    expires_at       timestamptz,
    idempotency_key  text,
    created_by       text
);
CREATE INDEX IF NOT EXISTS ix_rx_beneficiary_status ON pharmacy.prescription (beneficiary_id, status);
CREATE UNIQUE INDEX IF NOT EXISTS ux_rx_idempotency ON pharmacy.prescription (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS pharmacy.prescription_line (
    prescription_line_id uuid PRIMARY KEY,
    prescription_id      uuid NOT NULL REFERENCES pharmacy.prescription(prescription_id),
    drug_id              uuid NOT NULL,
    dose                 varchar(40),
    route                varchar(30),
    frequency            varchar(40),
    quantity_prescribed  numeric(14,3) NOT NULL CHECK (quantity_prescribed > 0),
    quantity_dispensed   numeric(14,3) NOT NULL DEFAULT 0
        CHECK (quantity_dispensed >= 0 AND quantity_dispensed <= quantity_prescribed),
    refills_allowed      integer NOT NULL DEFAULT 0 CHECK (refills_allowed >= 0),
    status               text NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','PartiallyDispensed','Dispensed','Cancelled'))
);
CREATE INDEX IF NOT EXISTS ix_rx_line_prescription ON pharmacy.prescription_line (prescription_id);

-- Advisory prescribe-time alerts (drug interaction / allergy), recorded with acknowledgement (non-blocking).
CREATE TABLE IF NOT EXISTS pharmacy.prescription_alert (
    alert_id        uuid PRIMARY KEY,
    prescription_id uuid NOT NULL REFERENCES pharmacy.prescription(prescription_id),
    kind            text NOT NULL CHECK (kind IN ('DrugInteraction','Allergy')),
    severity        text NOT NULL,
    detail          text NOT NULL,
    acknowledged    boolean NOT NULL DEFAULT false,
    raised_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_rx_alert_prescription ON pharmacy.prescription_alert (prescription_id);

CREATE TABLE IF NOT EXISTS pharmacy.referral (
    referral_id          uuid PRIMARY KEY,
    referral_no          varchar(20) NOT NULL UNIQUE,
    beneficiary_id       uuid NOT NULL,
    encounter_id         uuid NOT NULL,
    referring_provider_id uuid NOT NULL,
    target_specialty     varchar(80) NOT NULL,
    target_provider_id   uuid,
    reason               varchar(200),
    status               text NOT NULL DEFAULT 'Requested'
        CHECK (status IN ('Requested','Accepted','Scheduled','Completed','Cancelled','Expired')),
    requested_at         timestamptz NOT NULL DEFAULT now(),
    idempotency_key      text,
    created_by           text
);
CREATE INDEX IF NOT EXISTS ix_referral_beneficiary_status ON pharmacy.referral (beneficiary_id, status);
CREATE UNIQUE INDEX IF NOT EXISTS ux_referral_idempotency ON pharmacy.referral (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS pharmacy.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    entity_id       uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
