-- ADR-0035 §6 — a beneficiary identifier (refugee card, national id, passport, residency permit) may carry an
-- expiry. A lapsed one is what stops somebody being seen at reception.
--
-- EXPAND ONLY: nullable, no default, no backfill. Existing rows are untouched and read as they always did.
--
-- The same two-column shape as document.document, and for the same reason: "the card says March" and "nobody
-- recorded one so the cadence suggests March" are different facts, and only the first is a fact about the
-- document. NULL in both means UNKNOWN, which is never rendered as valid.

ALTER TABLE patient.beneficiary_identifier
    ADD COLUMN IF NOT EXISTS expires_on date NULL,
    ADD COLUMN IF NOT EXISTS expiry_source text NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_beneficiary_identifier_expiry_source') THEN
        ALTER TABLE patient.beneficiary_identifier ADD CONSTRAINT ck_beneficiary_identifier_expiry_source CHECK (expiry_source IS NULL OR expiry_source IN ('recorded', 'derived'));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_beneficiary_identifier_expiry_paired') THEN
        ALTER TABLE patient.beneficiary_identifier ADD CONSTRAINT ck_beneficiary_identifier_expiry_paired CHECK ((expires_on IS NULL) = (expiry_source IS NULL));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_beneficiary_identifier_expires_on
    ON patient.beneficiary_identifier (tenant_id, expires_on)
    WHERE expires_on IS NOT NULL;
