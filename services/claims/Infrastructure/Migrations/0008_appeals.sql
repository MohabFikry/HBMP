-- claims-service — Phase 10b.9 appeals (36-claims-management §6, 23-state-machines §7 Approved|PartiallyApproved|Denied
-- → Appealed → UnderAdjudication).
--
-- claim_appeal is APPEND-ONLY (reuse claims.reject_mutation() + no UPDATE/DELETE grant). An appeal NEVER edits or hides
-- the original decision thread: the prior claim_decision rows are untouched and remain readable; the appeal and its
-- re-decision are new rows linked via appeal_id / original_decision_id. A live claim re-enters UnderAdjudication; an
-- appeal on an already-settled batch is RECORDED as routed_to_adjustment — the settled batch is never reopened, and the
-- correction flows as a compensating adjustment/recovery (10b.7) in a later batch.

CREATE TABLE IF NOT EXISTS claims.claim_appeal (
    appeal_id            uuid PRIMARY KEY,
    claim_id             uuid NOT NULL REFERENCES claims.claim(claim_id),
    claim_line_id        uuid REFERENCES claims.claim_line(claim_line_id),
    tenant_id            text NOT NULL,
    appellant_type       varchar(12) NOT NULL CHECK (appellant_type IN ('Provider','Beneficiary')),
    reason               text NOT NULL,
    acting_for           text,
    original_decision_id uuid REFERENCES claims.claim_decision(decision_id),
    resolution           varchar(20) NOT NULL CHECK (resolution IN ('ReAdjudication','RoutedToAdjustment')),
    created_by           text NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_appeal_claim ON claims.claim_appeal (claim_id, created_at);
CREATE INDEX IF NOT EXISTS ix_appeal_original_decision ON claims.claim_appeal (original_decision_id)
    WHERE original_decision_id IS NOT NULL;

DROP TRIGGER IF EXISTS trg_claim_appeal_append_only ON claims.claim_appeal;
CREATE TRIGGER trg_claim_appeal_append_only
    BEFORE UPDATE OR DELETE ON claims.claim_appeal
    FOR EACH ROW EXECUTE FUNCTION claims.reject_mutation();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT ON claims.claim_appeal TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.claim_appeal ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_appeal FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_appeal ON claims.claim_appeal;
CREATE POLICY rls_claim_appeal ON claims.claim_appeal USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND EXISTS (SELECT 1 FROM claims.claim c WHERE c.claim_id = claim_appeal.claim_id)
);
