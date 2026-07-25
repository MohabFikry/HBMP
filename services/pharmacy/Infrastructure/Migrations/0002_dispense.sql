-- pharmacy-service — Phase 6 dispensing (22-data-dictionary §8.3, 23-state-machines §3 "Pharmacy-specific guards").
-- The append-only dispense_event table is the duplicate-proof anchor of the atomic dispense: one immutable row per
-- dispense, keyed by a UNIQUE idempotency_key so a replayed dispense is rejected by the DB and mapped to "return
-- prior outcome". Over-dispense is impossible: prescription_line already carries CHECK (0 ≤ dispensed ≤ prescribed)
-- (migration 0001), and dispense additionally guards on the line's xmin (optimistic concurrency) so exactly one racer
-- wins. Batch + expiry are captured on every dispense; §8.3 is extended with expiry_date for lot-expiry enforcement.
-- A policy-approved substitution records substituted_drug_id + reason. Rows are never updated or soft-deleted — full
-- history is in audit_event.

CREATE TABLE IF NOT EXISTS pharmacy.dispense_event (
    dispense_id            uuid PRIMARY KEY,
    prescription_line_id   uuid NOT NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    dispensing_pharmacy_id uuid NOT NULL,
    quantity               numeric(14,3) NOT NULL CHECK (quantity > 0),
    idempotency_key        varchar(80) NOT NULL UNIQUE,
    batch_no               varchar(60) NOT NULL,
    expiry_date            date NOT NULL,
    substituted_drug_id    uuid,
    substitution_reason    varchar(200),
    dispensed_at           timestamptz NOT NULL DEFAULT now(),
    dispensed_by           uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_dispense_line ON pharmacy.dispense_event (prescription_line_id);
-- Fast idempotent-replay lookup by key.
CREATE INDEX IF NOT EXISTS ix_dispense_idem ON pharmacy.dispense_event (idempotency_key varchar_pattern_ops);
