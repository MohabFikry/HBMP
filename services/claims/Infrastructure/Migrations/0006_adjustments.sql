-- claims-service — Phase 10b.7 reconciliation + append-only adjustments (36-claims-management §7, 22-data-dictionary
-- §10A.4, 07-functional-requirements FR-INV-008 reversal-only-via-compensating-action).
--
-- claim_adjustment is APPEND-ONLY (reuses claims.reject_mutation() from 0003 + no UPDATE/DELETE grant): a correction is
-- a NEW signed entry that nets into the batch rollup, never an edit or delete of the original decision. Each row carries
-- the BEFORE and AFTER payable amounts for a hash-chained audit trail. A Recovery/Clawback must reference the original
-- line it recovers against; a negative batch net requires a dual-control second approver before it takes effect.

CREATE TABLE IF NOT EXISTS claims.claim_adjustment (
    adjustment_id            uuid PRIMARY KEY,
    claim_line_id            uuid NOT NULL REFERENCES claims.claim_line(claim_line_id),
    claim_id                 uuid NOT NULL REFERENCES claims.claim(claim_id),
    tenant_id                text NOT NULL,
    adjustment_type          varchar(20) NOT NULL CHECK (adjustment_type IN
        ('PriceCorrection','QuantityCorrection','Deduction','Recovery','Clawback','Writeoff','Reversal','Void','Reallocation')),
    amount_delta             numeric(14,2) NOT NULL CHECK (amount_delta <> 0),
    reason_code              varchar(40) NOT NULL,
    rationale                text NOT NULL,
    recovers_claim_line_id   uuid REFERENCES claims.claim_line(claim_line_id),
    before_amount            numeric(14,2) NOT NULL,
    after_amount             numeric(14,2) NOT NULL,
    adjusted_by              text NOT NULL,
    adjusted_at              timestamptz NOT NULL DEFAULT now(),
    correlation_id           varchar(64) NOT NULL DEFAULT '',
    pending_second_approval  boolean NOT NULL DEFAULT false,
    confirms_adjustment_id   uuid REFERENCES claims.claim_adjustment(adjustment_id),
    idempotency_key          text,
    -- a Recovery/Clawback MUST reference the original line it recovers against.
    CHECK (adjustment_type NOT IN ('Recovery','Clawback') OR recovers_claim_line_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_adjustment_line ON claims.claim_adjustment (claim_line_id, adjusted_at);
CREATE INDEX IF NOT EXISTS ix_adjustment_type ON claims.claim_adjustment (adjustment_type);
CREATE INDEX IF NOT EXISTS ix_adjustment_recovers ON claims.claim_adjustment (recovers_claim_line_id)
    WHERE recovers_claim_line_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_adjustment_idempotency ON claims.claim_adjustment (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- APPEND-ONLY enforcement (reuse the 0003 trigger function): reject any UPDATE/DELETE.
DROP TRIGGER IF EXISTS trg_claim_adjustment_append_only ON claims.claim_adjustment;
CREATE TRIGGER trg_claim_adjustment_append_only
    BEFORE UPDATE OR DELETE ON claims.claim_adjustment
    FOR EACH ROW EXECUTE FUNCTION claims.reject_mutation();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        -- INSERT + SELECT only — no UPDATE/DELETE grant (append-only at the privilege layer too).
        GRANT SELECT, INSERT ON claims.claim_adjustment TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.claim_adjustment ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_adjustment FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_adjustment ON claims.claim_adjustment;
CREATE POLICY rls_claim_adjustment ON claims.claim_adjustment USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND EXISTS (SELECT 1 FROM claims.claim c WHERE c.claim_id = claim_adjustment.claim_id)
);
