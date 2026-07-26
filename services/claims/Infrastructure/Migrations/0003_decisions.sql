-- claims-service — Phase 10b.4 line-level Claims Officer decisions (36-claims-management §6, 22-data-dictionary
-- §10A.3, 23-state-machines §7/§8, 11-permission-matrix §6.7 SoD).
--
-- claim_decision is APPEND-ONLY: a changed outcome is a NEW row, never an edit. Enforced by (a) NO UPDATE/DELETE
-- grants to the app role and (b) a trigger that rejects UPDATE/DELETE even for the owner. Full history is the ledger
-- itself + audit_event. Two columns extend the 22 §10A.3 base for DUAL CONTROL (a high-value decision needs a second
-- distinct approver before it takes effect): pending_second_approval + confirms_decision_id. idempotency_key makes a
-- retried decision safe (one row per key).

CREATE TABLE IF NOT EXISTS claims.claim_decision (
    decision_id            uuid PRIMARY KEY,
    claim_line_id          uuid NOT NULL REFERENCES claims.claim_line(claim_line_id),
    claim_id               uuid NOT NULL REFERENCES claims.claim(claim_id),
    tenant_id              text NOT NULL,
    decision               varchar(24) NOT NULL
        CHECK (decision IN ('Approve','PartiallyApprove','Deny','Adjust','RequestInfo','RouteToClinical')),
    allowed_amount         numeric(14,2) CHECK (allowed_amount IS NULL OR allowed_amount >= 0),
    reason_codes           text[] NOT NULL DEFAULT '{}',
    rationale              text,
    decided_by             text NOT NULL,
    decided_at             timestamptz NOT NULL DEFAULT now(),
    rule_version           varchar(20),
    correlation_id         varchar(64) NOT NULL DEFAULT '',
    -- dual control extension (FR): a decision whose value exceeds the configured threshold waits for a second,
    -- distinct approver. confirms_decision_id points from the confirming row back to the pending one.
    pending_second_approval boolean NOT NULL DEFAULT false,
    confirms_decision_id    uuid REFERENCES claims.claim_decision(decision_id),
    idempotency_key        text
);
CREATE INDEX IF NOT EXISTS ix_decision_line ON claims.claim_decision (claim_line_id, decided_at);
CREATE INDEX IF NOT EXISTS ix_decision_by ON claims.claim_decision (decided_by);
CREATE INDEX IF NOT EXISTS ix_decision_correlation ON claims.claim_decision (correlation_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_decision_idempotency ON claims.claim_decision (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- APPEND-ONLY enforcement: reject any UPDATE/DELETE (corrections are new rows / adjustments / compensating void).
CREATE OR REPLACE FUNCTION claims.reject_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'append-only: % on % is not permitted', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'raise_exception';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_claim_decision_append_only ON claims.claim_decision;
CREATE TRIGGER trg_claim_decision_append_only
    BEFORE UPDATE OR DELETE ON claims.claim_decision
    FOR EACH ROW EXECUTE FUNCTION claims.reject_mutation();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        -- INSERT + SELECT only — no UPDATE/DELETE grant (append-only at the privilege layer too).
        GRANT SELECT, INSERT ON claims.claim_decision TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.claim_decision ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_decision FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_decision ON claims.claim_decision;
CREATE POLICY rls_claim_decision ON claims.claim_decision USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND EXISTS (SELECT 1 FROM claims.claim c WHERE c.claim_id = claim_decision.claim_id)
);
