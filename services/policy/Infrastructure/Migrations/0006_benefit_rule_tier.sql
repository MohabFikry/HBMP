-- policy-service — 0006 tier-aware cost share (phase 19.1b, design 38 §3 "benefit_rule_tier", §4.1b).
--
-- WHAT THIS CORRECTS. 0005 (phase 19.1) put co-pay and a free-text `network_tier` label ON the benefit rule, so
-- a rule could say "Lab, 10% co-pay, tier T1" — one cost share, one tier, as a string. Real benefit design does
-- not work that way: the SAME category is priced differently per tier ("in-network 10%, out-of-network 40%, or
-- not covered at all"), and the tier itself is not a label the plan invents — it is a row the Network Team owns
-- in provider.network_tier. Cost share therefore moves to a child keyed on (rule, tier), and the free-text
-- label goes away entirely.
--
-- 0005 shipped in the same unreleased phase and no environment carries data in these columns, so the contract
-- step is taken here rather than deferred; each drop is acknowledged for the expand/contract gate below.
--
-- CROSS-SERVICE REFERENCE. network_tier_id is a VALUE, not a foreign key — provider.network_tier lives in
-- another service's schema and the repo forbids cross-schema FKs (15-database-erd). It is validated at write
-- time against the tier catalogue and, because a plan version is immutable and must stay explainable for as
-- long as claims can reference it, the tier's CODE is snapshotted alongside the id so reading a five-year-old
-- version does not require a live call into provider-service.

-- ---- contract: the 19.1 cost-share columns -----------------------------------------------------------------
ALTER TABLE policy.benefit_rule DROP CONSTRAINT IF EXISTS ck_benefit_rule_copay;  -- migrate-compat: contract-ok (cost share moved to benefit_rule_tier in the same unreleased phase; no data exists)
ALTER TABLE policy.benefit_rule DROP COLUMN IF EXISTS copay_fixed;                -- migrate-compat: contract-ok (superseded by benefit_rule_tier.copay_fixed, 19.1b)
ALTER TABLE policy.benefit_rule DROP COLUMN IF EXISTS copay_percent;              -- migrate-compat: contract-ok (superseded by benefit_rule_tier.copay_percent, 19.1b)
ALTER TABLE policy.benefit_rule DROP COLUMN IF EXISTS network_tier;               -- migrate-compat: contract-ok (free-text label replaced by a real reference to provider.network_tier, 19.1b)

-- ---- benefit_rule_tier -------------------------------------------------------------------------------------
-- What the member pays for THIS category at THIS tier. Deductible, waiting period, limits, exclusions and the
-- pre-auth default stay on the rule: they are properties of the benefit, not of where it was delivered.
CREATE TABLE IF NOT EXISTS policy.benefit_rule_tier (
    rule_tier_id              uuid PRIMARY KEY,
    tenant_id                 text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
    benefit_rule_id           uuid NOT NULL REFERENCES policy.benefit_rule(rule_id) ON DELETE CASCADE,

    -- provider.network_tier — a cross-service VALUE plus a snapshot of its code (see the header note).
    network_tier_id           uuid NOT NULL,
    tier_code                 varchar(12) NOT NULL,

    -- An explicit "not covered at this tier" row is a real, useful statement (an HMO plan that pays nothing
    -- out-of-network), and is NOT the same as the tier being unconfigured — which activation rejects.
    is_covered                boolean NOT NULL DEFAULT true,

    copay_fixed               numeric(14,2) CHECK (copay_fixed IS NULL OR copay_fixed >= 0),
    copay_percent             numeric(5,2)  CHECK (copay_percent IS NULL OR (copay_percent >= 0 AND copay_percent <= 100)),
    coinsurance_percent       numeric(5,2)  CHECK (coinsurance_percent IS NULL OR (coinsurance_percent >= 0 AND coinsurance_percent <= 100)),

    -- Overrides the rule's requires_preauth for this tier only; NULL = inherit. An out-of-network provider
    -- commonly needs pre-authorization for a service that is open-access in-network.
    requires_preauth_override boolean,

    -- Scales the rule's limit at this tier (0.5 = half the annual ceiling out-of-network); NULL = inherit.
    limit_multiplier          numeric(5,2) CHECK (limit_multiplier IS NULL OR limit_multiplier >= 0),

    created_at                timestamptz NOT NULL DEFAULT now(),
    created_by                uuid,
    updated_at                timestamptz NOT NULL DEFAULT now(),
    updated_by                uuid,

    -- Fixed and percentage co-pay are alternatives; carrying both leaves the member's share undefined at
    -- adjudication, which is a silent overcharge waiting to happen (this CHECK is the one that used to live
    -- on benefit_rule).
    CONSTRAINT ck_brt_copay CHECK (copay_fixed IS NULL OR copay_percent IS NULL),
    -- A tier that covers nothing carries no cost share: there is no amount to take a share OF, and a stored
    -- co-pay under a not-covered row reads as an entitlement in every UI that renders it.
    CONSTRAINT ck_brt_uncovered_has_no_cost_share CHECK (
        is_covered
        OR (copay_fixed IS NULL AND copay_percent IS NULL AND coinsurance_percent IS NULL
            AND limit_multiplier IS NULL)
    ),
    CONSTRAINT uq_brt_rule_tier UNIQUE (benefit_rule_id, network_tier_id)
);
CREATE INDEX IF NOT EXISTS ix_brt_rule ON policy.benefit_rule_tier (benefit_rule_id);
CREATE INDEX IF NOT EXISTS ix_brt_tier ON policy.benefit_rule_tier (network_tier_id);

-- ---- Immutability, enforced by the database ----------------------------------------------------------------
-- The cost-share grid is now where a large part of the benefit configuration actually lives, so 0005's
-- reasoning applies to it verbatim: freezing plan_version and benefit_rule while leaving the per-tier amounts
-- writable would freeze the shape of the plan and none of its prices.
CREATE OR REPLACE FUNCTION policy.guard_benefit_rule_tier_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE parent_status text;
        rule_id_v uuid;
BEGIN
    rule_id_v := COALESCE(NEW.benefit_rule_id, OLD.benefit_rule_id);
    SELECT v.status INTO parent_status
      FROM policy.benefit_rule r
      JOIN policy.plan_version v ON v.plan_version_id = r.plan_version_id
     WHERE r.rule_id = rule_id_v;
    -- A NULL status means the parent rule is already gone: this is a cascade from deleting the rule itself,
    -- which the benefit_rule trigger has already authorized. Blocking here would make a Draft's rule set
    -- impossible to replace.
    IF parent_status IS NOT NULL AND parent_status <> 'Draft' THEN
        RAISE EXCEPTION 'cost share for benefit_rule % belongs to a % plan version and is immutable: amend the plan to create a new version', rule_id_v, parent_status
            USING ERRCODE = 'raise_exception';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;
DROP TRIGGER IF EXISTS trg_benefit_rule_tier_immutable ON policy.benefit_rule_tier;
CREATE TRIGGER trg_benefit_rule_tier_immutable BEFORE INSERT OR UPDATE OR DELETE ON policy.benefit_rule_tier
    FOR EACH ROW EXECUTE FUNCTION policy.guard_benefit_rule_tier_immutable();

-- ---- Grants + tenant RLS (ADR-0011, same shape as 0005) ----------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON policy.benefit_rule_tier TO hbmp_app;

ALTER TABLE policy.benefit_rule_tier ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.benefit_rule_tier FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_benefit_rule_tier ON policy.benefit_rule_tier;
CREATE POLICY rls_benefit_rule_tier ON policy.benefit_rule_tier USING (
    tenant_id = current_setting('app.tenant_id', true)
);
