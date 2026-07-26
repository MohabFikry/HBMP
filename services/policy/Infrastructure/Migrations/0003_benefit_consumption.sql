-- policy-service — 0003 benefit-consumption accumulator ledger (phase 18.A1 / audit R2 X1).
--
-- Before this migration nothing in the platform incremented policy.coverage_limit.consumed_value: the
-- consume/dispense events were emitted but had no consumer here, so remaining always equalled the full
-- limit and LIMIT_EXCEEDED could never fire. This adds the two tables the accumulator consumer needs:
--
--   processed_event      — the consumer's dedupe ledger (mirrors eligibility.processed_event).
--                          Intentionally RLS-FREE: it is a transport-level ledger with no tenant data
--                          and is written by the background consumer.
--   benefit_consumption  — the append-only record of every accumulator MOVE and every deliberate
--                          NO-move (NoCoverage / NoBenefitCategory / …). source_ref is UNIQUE, so a
--                          redelivered event or a retried void can never double-count. Nothing here is
--                          ever updated or deleted; it is the reconciliation trail between the
--                          fulfillment ledger (orders/pharmacy) and the accumulator.
--
-- Additive + idempotent (expand/contract). consumed_value keeps its CHECK (>= 0) only: a limit reduced
-- mid-period can legitimately leave consumed > limit, and the accumulator must stay truthful rather
-- than reject the fulfillment that already happened.

CREATE TABLE IF NOT EXISTS policy.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS policy.benefit_consumption (
    consumption_id   uuid PRIMARY KEY,
    tenant_id        text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    event_id         uuid NOT NULL,
    event_type       text NOT NULL,
    source_ref       text NOT NULL,
    beneficiary_id   uuid NOT NULL,
    benefit_category text,
    coverage_id      uuid,
    quantity         numeric(14,3) NOT NULL CHECK (quantity >= 0),
    direction        text NOT NULL CHECK (direction IN ('Applied','Reversed')),
    outcome          text NOT NULL CHECK (outcome IN
                       ('Applied','Reversed','Replayed','NoBenefitCategory','NoCoverage','NoAccumulatingLimit','WouldGoNegative')),
    moved_limits     integer NOT NULL DEFAULT 0,
    applied_at       timestamptz NOT NULL DEFAULT now()
);

-- The duplicate-proof anchor: one row per (fulfillment line, direction). A second delivery of the same
-- event loses this insert and the whole transaction rolls back, leaving consumed_value untouched.
CREATE UNIQUE INDEX IF NOT EXISTS ux_benefit_consumption_source_ref
    ON policy.benefit_consumption (source_ref);
CREATE INDEX IF NOT EXISTS ix_benefit_consumption_beneficiary
    ON policy.benefit_consumption (beneficiary_id, applied_at DESC);
CREATE INDEX IF NOT EXISTS ix_benefit_consumption_coverage
    ON policy.benefit_consumption (coverage_id) WHERE coverage_id IS NOT NULL;

GRANT USAGE ON SCHEMA policy TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON policy.processed_event TO hbmp_app;
GRANT SELECT, INSERT ON policy.benefit_consumption TO hbmp_app;

-- Tenant isolation for the ledger (ADR-0011). Same shape as 0002; the background consumer binds
-- app.tenant_id from the event envelope before writing.
ALTER TABLE policy.benefit_consumption ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.benefit_consumption FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_benefit_consumption ON policy.benefit_consumption;
CREATE POLICY rls_benefit_consumption ON policy.benefit_consumption
    USING (tenant_id = current_setting('app.tenant_id', true));
