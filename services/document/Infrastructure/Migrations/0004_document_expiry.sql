-- ADR-0035 §6 — a document may carry an expiry, and we record whether we were TOLD it or derived it.
--
-- EXPAND ONLY. Both columns are nullable with no default and no backfill, so every existing row keeps
-- reading exactly as it did and no deploy ordering matters.
--
-- Why two columns and not one. `expires_on` alone cannot distinguish "the card says it lapses in March" from
-- "nobody recorded one, so the policy's renewal cadence puts a review in March". Those are different facts
-- about a refugee's papers, and the platform must never present the second as the first — it does not decide
-- when a government-issued card lapses. `expiry_source` carries which it is, and NULL means neither: no
-- expiry is known at all, which is UNKNOWN and must never be rendered as valid.
--
-- The constraints are added through a guard rather than DROP-then-ADD: a bare `DROP CONSTRAINT IF EXISTS` is
-- indistinguishable from a contract-phase drop to the migration-compat gate, and it would be one if this file
-- were ever replayed against a live table mid-rollout.

ALTER TABLE document.document
    ADD COLUMN IF NOT EXISTS expires_on date NULL,
    ADD COLUMN IF NOT EXISTS expiry_source text NULL;

-- A closed vocabulary. Free text would let 'recorded', 'Recorded' and 'from-doc' mean the same thing to three
-- writers and nothing to any reader.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_document_expiry_source') THEN
        ALTER TABLE document.document ADD CONSTRAINT ck_document_expiry_source CHECK (expiry_source IS NULL OR expiry_source IN ('recorded', 'derived'));
    END IF;
END $$;

-- A source without a date, or a date without a source, is a half-written fact. Either both or neither.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_document_expiry_paired') THEN
        ALTER TABLE document.document ADD CONSTRAINT ck_document_expiry_paired CHECK ((expires_on IS NULL) = (expiry_source IS NULL));
    END IF;
END $$;

-- The sweeper reads "what lapses between now and the furthest warning threshold", so it filters on the date
-- and never scans the table. Partial: rows with no expiry are the ones it must never consider.
CREATE INDEX IF NOT EXISTS ix_document_expires_on
    ON document.document (tenant_id, expires_on)
    WHERE expires_on IS NOT NULL AND is_deleted = false;
