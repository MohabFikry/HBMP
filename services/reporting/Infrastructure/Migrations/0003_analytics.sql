-- ============================================================================================================
-- Phase 19.6b — the analytical read model over policy & member administration.
--
-- Additive only (expand/contract): three new fact tables and one dimension-label table. Nothing existing is
-- altered, so an older reporting-service binary keeps running against this schema unchanged.
--
-- These tables exist so the dashboard NEVER queries the transactional benefit spine. A six-view dashboard
-- joining policy.enrollment and policy.coverage_limit would put aggregate scans on the same rows a reception
-- desk is checking eligibility against, and would leave row-level PHI one careless SELECT away from a screen
-- whose entire purpose is totals.
--
-- Every table is tenant-scoped and RLS-protected on the same predicate as the phase-8.2 facts.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS reporting.fact_enrolment (
    fact_id             uuid PRIMARY KEY,
    event_id            uuid NOT NULL,
    tenant_id           text NOT NULL,
    payer_id            uuid NULL,
    policy_id           uuid NOT NULL,
    policy_plan_id      uuid NULL,
    group_id            uuid NULL,
    branch_id           uuid NULL,
    relationship        text NOT NULL,
    status              text NOT NULL,
    -- A POINTER for an audited drill-down, never a projection of the person: the outlier views must be able to
    -- hand a member id to a permission-gated, audited read. No name, no identifier, no clinical field.
    beneficiary_id      uuid NOT NULL,
    enrollment_id       uuid NOT NULL,
    movement            text NOT NULL,
    in_waiting_period   boolean NOT NULL DEFAULT false,
    period              date NOT NULL,
    occurred_at         timestamptz NOT NULL,
    CONSTRAINT uq_fact_enrolment_event UNIQUE (event_id),
    CONSTRAINT ck_fact_enrolment_movement
        CHECK (movement IN ('Enrolled','Terminated','Reinstated','PlanChanged','GroupChanged','Cancelled'))
);

CREATE TABLE IF NOT EXISTS reporting.fact_utilization (
    fact_id               uuid PRIMARY KEY,
    event_id              uuid NOT NULL,
    tenant_id             text NOT NULL,
    payer_id              uuid NULL,
    policy_id             uuid NOT NULL,
    policy_plan_id        uuid NULL,
    group_id              uuid NULL,
    branch_id             uuid NULL,
    beneficiary_id        uuid NOT NULL,
    enrollment_id         uuid NOT NULL,
    benefit_category_code text NOT NULL,
    -- Stored, not re-derived. Tier membership is effective-dated; a provider moving tiers must not
    -- retroactively reclassify activity that was delivered while they sat somewhere else.
    network_tier_code     text NULL,
    out_of_network        boolean NOT NULL DEFAULT false,
    limit_value           numeric(18,2) NOT NULL DEFAULT 0,
    consumed_value        numeric(18,2) NOT NULL DEFAULT 0,
    -- NULL = unbounded. Zero would read as "nothing left" on a benefit that was never metered.
    remaining             numeric(18,2) NULL,
    band                  text NOT NULL,
    period                date NOT NULL,
    occurred_at           timestamptz NOT NULL,
    CONSTRAINT uq_fact_utilization_event UNIQUE (event_id),
    CONSTRAINT ck_fact_utilization_band
        CHECK (band IN ('Zero','Low','Medium','High','Exhausted','Unlimited')),
    -- The accumulator is never negative and a limit is never negative; a fact that violates either is a
    -- projection defect, and it should fail at the write rather than surface as a nonsense bar.
    CONSTRAINT ck_fact_utilization_nonneg CHECK (limit_value >= 0 AND consumed_value >= 0)
);

CREATE TABLE IF NOT EXISTS reporting.fact_cost (
    fact_id               uuid PRIMARY KEY,
    event_id              uuid NOT NULL,
    tenant_id             text NOT NULL,
    payer_id              uuid NULL,
    policy_id             uuid NULL,
    policy_plan_id        uuid NULL,
    network_tier_code     text NULL,
    out_of_network        boolean NOT NULL DEFAULT false,
    benefit_category_code text NOT NULL,
    provider_id           uuid NULL,
    claimed_amount        numeric(18,2) NOT NULL DEFAULT 0,
    approved_amount       numeric(18,2) NOT NULL DEFAULT 0,
    adjusted_amount       numeric(18,2) NOT NULL DEFAULT 0,
    net_payable           numeric(18,2) NOT NULL DEFAULT 0,
    currency_code         text NOT NULL DEFAULT 'EGP',
    claim_count           integer NOT NULL DEFAULT 1,
    period                date NOT NULL,
    occurred_at           timestamptz NOT NULL,
    CONSTRAINT uq_fact_cost_event UNIQUE (event_id)
    -- THERE IS DELIBERATELY NO DIAGNOSIS OR CLINICAL COLUMN HERE. The financial view is specified as "No
    -- diagnoses anywhere", and the finance role holds only the financial reporting zone — a clinical column
    -- on this table would sit behind an authorization check that was never designed to guard one. A test in
    -- Mersal.Reporting.Tests asserts the absence rather than trusting this comment.
);

-- One dimension table, not four. dim_payer / dim_plan / dim_group / dim_branch are the same shape — id, kind,
-- bilingual label — and four near-identical tables would each need their own upsert, index and RLS policy for
-- no gain. Labels are denormalised deliberately: renaming a payer must not silently restate last year's report.
CREATE TABLE IF NOT EXISTS reporting.dim_label (
    dimension_id  uuid NOT NULL,
    kind          text NOT NULL,
    tenant_id     text NOT NULL,
    label_en      text NOT NULL,
    label_ar      text NOT NULL,
    code          text NULL,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (dimension_id, kind),
    CONSTRAINT ck_dim_label_kind
        CHECK (kind IN ('payer','policy','policy_plan','group','branch','category','tier'))
);

-- Shaped by the FILTER BAR rather than by the columns: every view narrows on tenant + period first, then on
-- payer or plan. A dashboard that scanned a year of facts to answer "this month, this payer" is precisely the
-- slow live query 19.6b refuses to ship.
CREATE INDEX IF NOT EXISTS ix_fact_enrolment_tenant_period    ON reporting.fact_enrolment (tenant_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_enrolment_payer_period     ON reporting.fact_enrolment (tenant_id, payer_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_enrolment_plan_period      ON reporting.fact_enrolment (tenant_id, policy_plan_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_enrolment_enrollment       ON reporting.fact_enrolment (tenant_id, enrollment_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_utilization_tenant_period  ON reporting.fact_utilization (tenant_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_utilization_payer_period   ON reporting.fact_utilization (tenant_id, payer_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_utilization_cat_period     ON reporting.fact_utilization (tenant_id, benefit_category_code, period);
CREATE INDEX IF NOT EXISTS ix_fact_utilization_band_period    ON reporting.fact_utilization (tenant_id, band, period);
CREATE INDEX IF NOT EXISTS ix_fact_cost_tenant_period         ON reporting.fact_cost (tenant_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_cost_payer_period          ON reporting.fact_cost (tenant_id, payer_id, period);
CREATE INDEX IF NOT EXISTS ix_fact_cost_tier_period           ON reporting.fact_cost (tenant_id, network_tier_code, period);
CREATE INDEX IF NOT EXISTS ix_dim_label_tenant_kind           ON reporting.dim_label (tenant_id, kind);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['fact_enrolment','fact_utilization','fact_cost','dim_label']
    LOOP
        EXECUTE format('ALTER TABLE reporting.%I ALTER COLUMN tenant_id SET DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA reporting TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['fact_enrolment','fact_utilization','fact_cost','dim_label']
    LOOP
        EXECUTE format('ALTER TABLE reporting.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE reporting.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON reporting.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON reporting.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;
