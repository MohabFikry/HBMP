-- claims-service — Phase 10b.8 settlement advice + exports (36-claims-management §8, 22-data-dictionary §10A.5,
-- 0C-OPEN-SOURCE-STACK MinIO object-lock/WORM).
--
-- THE PLATFORM NEVER MOVES MONEY. There is no payment execution, no bank/payment-rail integration, and no payout
-- endpoint anywhere. The settlement advice is the immutable hand-off artifact to Finance; settlement_payment_reference
-- RECORDS an external payment fact only. Both tables are APPEND-ONLY (reuse claims.reject_mutation() + no UPDATE/DELETE
-- grant). Regeneration writes a NEW version referencing the superseded advice — it never overwrites. The document
-- itself is WORM in document-service (object-lock); the content_hash here detects any change to the stored bytes.

CREATE TABLE IF NOT EXISTS claims.settlement_advice (
    advice_id             uuid PRIMARY KEY,
    batch_id              uuid NOT NULL REFERENCES claims.claim_batch(batch_id),
    tenant_id             text NOT NULL,
    batch_no              varchar(20) NOT NULL,
    payee_provider_id     uuid,
    provider_location_id  uuid,
    period_from           date NOT NULL,
    period_to             date NOT NULL,
    version               integer NOT NULL DEFAULT 1 CHECK (version >= 1),
    supersedes_advice_id  uuid REFERENCES claims.settlement_advice(advice_id),
    document_id           uuid,
    content_hash          text NOT NULL,
    total_claimed         numeric(16,2) NOT NULL,
    total_priced          numeric(16,2) NOT NULL,
    total_approved        numeric(16,2) NOT NULL,
    total_adjusted        numeric(16,2) NOT NULL,
    total_denied          numeric(16,2) NOT NULL,
    net_payable           numeric(16,2) NOT NULL,
    generated_by          text NOT NULL,
    generated_at          timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_settlement_advice_version ON claims.settlement_advice (batch_id, version);
CREATE INDEX IF NOT EXISTS ix_settlement_advice_batch ON claims.settlement_advice (batch_id);

CREATE TABLE IF NOT EXISTS claims.settlement_payment_reference (
    payment_reference_id uuid PRIMARY KEY,
    batch_id             uuid NOT NULL REFERENCES claims.claim_batch(batch_id),
    tenant_id            text NOT NULL,
    reference            text NOT NULL,
    payment_date         date NOT NULL,
    recorded_by          text NOT NULL,
    recorded_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_payment_reference_batch ON claims.settlement_payment_reference (batch_id);

-- APPEND-ONLY: reject any UPDATE/DELETE (reuse the 0003 trigger function).
DROP TRIGGER IF EXISTS trg_settlement_advice_append_only ON claims.settlement_advice;
CREATE TRIGGER trg_settlement_advice_append_only
    BEFORE UPDATE OR DELETE ON claims.settlement_advice
    FOR EACH ROW EXECUTE FUNCTION claims.reject_mutation();
DROP TRIGGER IF EXISTS trg_payment_reference_append_only ON claims.settlement_payment_reference;
CREATE TRIGGER trg_payment_reference_append_only
    BEFORE UPDATE OR DELETE ON claims.settlement_payment_reference
    FOR EACH ROW EXECUTE FUNCTION claims.reject_mutation();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT ON claims.settlement_advice, claims.settlement_payment_reference TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.settlement_advice ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.settlement_advice FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_settlement_advice ON claims.settlement_advice;
CREATE POLICY rls_settlement_advice ON claims.settlement_advice USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR payee_provider_id::text = current_setting('app.provider_id', true)
    )
);

ALTER TABLE claims.settlement_payment_reference ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.settlement_payment_reference FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_payment_reference ON claims.settlement_payment_reference;
CREATE POLICY rls_payment_reference ON claims.settlement_payment_reference USING (
    tenant_id = current_setting('app.tenant_id', true)
);
