-- ADR-0035 §5.3 — bounded auto-approval, behind a kill switch.
--
-- EXPAND ONLY. One column widens from NOT NULL to NULL, one column is added, one CHECK is added over the new
-- pair, one table is created, one CHECK widens. A service running the previous build keeps working: it always
-- writes a reviewer_id, which still satisfies everything below.

-- ---- a decision the ENGINE made ------------------------------------------------------------------------
--
-- `reviewer_id` becomes nullable and `decided_by_rule` appears beside it, with a CHECK that EXACTLY ONE is
-- set. That pair is the whole point.
--
-- Attributing a machine decision to a human is a falsified audit record, and this ledger is hash-chained
-- precisely so that cannot happen. The alternative — a sentinel Guid meaning "the system" — is worse than it
-- looks: it is a value that reads as a person's id everywhere it is joined, and every report, export and
-- investigation downstream would have to know the magic number to avoid saying somebody approved something
-- they never saw.
--
-- The CHECK also forbids NEITHER being set. A decision with no author at all is not an improvement on one
-- with the wrong author.
ALTER TABLE approvals.authorization_decision
    ALTER COLUMN reviewer_id DROP NOT NULL;

ALTER TABLE approvals.authorization_decision
    ADD COLUMN IF NOT EXISTS decided_by_rule uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_decision_has_exactly_one_author') THEN
        ALTER TABLE approvals.authorization_decision
            ADD CONSTRAINT ck_decision_has_exactly_one_author
            CHECK ((reviewer_id IS NULL) <> (decided_by_rule IS NULL));
    END IF;
END $$;

-- ---- the kill switch -----------------------------------------------------------------------------------
--
-- Its own table in THIS schema, not a system_config row in admin-service. The switch you reach for at 02:00
-- because a rule is misbehaving must not depend on another service being reachable — and if it could not be
-- read, the safe reading is "off", which a local row makes deterministic rather than a matter of whether an
-- HTTP call timed out.
--
-- NO ROW MEANS OFF. Auto-approval is opt-in per tenant and stays opt-in: a new tenant, a restored database or
-- a failed migration all produce "no row", and every one of those must mean nobody is being paid without a
-- human having looked.
CREATE TABLE IF NOT EXISTS approvals.auto_decision_switch (
    tenant_id  text PRIMARY KEY,
    enabled    boolean NOT NULL DEFAULT false,
    -- Why it is in whatever state it is in. Turning this ON is a decision somebody owns; turning it off in a
    -- hurry is one somebody should be able to explain afterwards.
    reason     text NOT NULL,
    updated_by text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_auto_switch_reason_not_blank') THEN
        ALTER TABLE approvals.auto_decision_switch ADD CONSTRAINT ck_auto_switch_reason_not_blank
            CHECK (length(btrim(reason)) > 0);
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON approvals.auto_decision_switch TO hbmp_app;
    END IF;
END $$;

ALTER TABLE approvals.auto_decision_switch ENABLE ROW LEVEL SECURITY;
ALTER TABLE approvals.auto_decision_switch FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_auto_decision_switch ON approvals.auto_decision_switch;
CREATE POLICY rls_auto_decision_switch ON approvals.auto_decision_switch
    USING (tenant_id = current_setting('app.tenant_id', true));

-- ---- the fourth rule family ------------------------------------------------------------------------------
--
-- There is deliberately no 'AutoReject'. The two failure modes are not symmetric: a wrong auto-approval costs
-- the payer money and a human reviews the claim later, while a wrong auto-rejection denies care to a refugee
-- with nobody having looked — and per libs/benefit-pricing's own header, they have "no reviewer in the loop
-- and no recovery path". The throughput is available without the harm by routing to a priority queue with a
-- stated reason, which the Routing family already does.
ALTER TABLE approvals.rule DROP CONSTRAINT IF EXISTS ck_rule_family;  -- migrate-compat: contract-ok (widening a CHECK in place; the old set is a strict subset of the new one, so no row can fail and no deploy order matters)
ALTER TABLE approvals.rule ADD CONSTRAINT ck_rule_family
    CHECK (family IN ('Routing', 'Sla', 'Preauth', 'AutoApprove'));
