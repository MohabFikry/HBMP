-- claims-service — Phase 10b.1 foundation + auto-derived claims (36-claims-management §2/§3.1/§5,
-- 22-data-dictionary §10A.1/§10A.2, 23-state-machines §7/§8).
--
-- THE POINT OF THIS MIGRATION is the partial unique index on claim_line.fulfillment_ref: it makes double-billing
-- IMPOSSIBLE at the database, not merely unlikely — at most one live (non-Void) payable line may reference a given
-- orders.order_fulfillment / pharmacy.dispense_event row. A second attempt fails on SQLSTATE 23505 and the app maps
-- it to a DUPLICATE_CLAIM (409) denial, never a silent second payable line.
--
-- The claims schema carries NO clinical column anywhere (no diagnosis / ICD / EMR note / result value): adjudication
-- is on service CODES and AMOUNTS only (22 §10A minimum-necessary note, 11-permission-matrix §3.2). beneficiary_id
-- is PHI-linking and is treated as PHI for RLS/masking/audit-on-read.

CREATE SCHEMA IF NOT EXISTS claims;

-- Monotonic per-year claim-number sequence backing CLM-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS claims.claim_seq (
    year       int PRIMARY KEY,
    last_value int NOT NULL
);

CREATE TABLE IF NOT EXISTS claims.claim (
    claim_id             uuid PRIMARY KEY,
    claim_no             varchar(20) NOT NULL UNIQUE,
    origin               varchar(20) NOT NULL CHECK (origin IN ('AutoDerived','ProviderSubmitted','Reimbursement')),
    beneficiary_id       uuid NOT NULL,
    provider_id          uuid,
    provider_location_id uuid,
    batch_id             uuid,
    authorization_id     uuid,
    tenant_id            text NOT NULL,
    service_date_from    date NOT NULL,
    service_date_to      date,
    currency_code        char(3) NOT NULL DEFAULT 'EGP',
    claimed_amount       numeric(14,2) NOT NULL DEFAULT 0 CHECK (claimed_amount >= 0),
    priced_amount        numeric(14,2) CHECK (priced_amount IS NULL OR priced_amount >= 0),
    approved_amount      numeric(14,2) CHECK (approved_amount IS NULL OR approved_amount >= 0),
    adjusted_amount      numeric(14,2),
    net_payable          numeric(14,2),
    status               varchar(20) NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Submitted','UnderAdjudication','PendingInfo','ClinicalReview',
                          'Approved','PartiallyApproved','Denied','Settled','Appealed','Void')),
    submitted_at         timestamptz,
    decided_at           timestamptz,
    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by           text,
    -- A payee provider is mandatory for provider claims; reimbursement claims have none.
    CHECK (origin = 'Reimbursement' OR provider_id IS NOT NULL),
    CHECK (service_date_to IS NULL OR service_date_to >= service_date_from)
);
CREATE INDEX IF NOT EXISTS ix_claim_beneficiary_status ON claims.claim (beneficiary_id, status);
CREATE INDEX IF NOT EXISTS ix_claim_provider_period    ON claims.claim (provider_id, service_date_from);
CREATE INDEX IF NOT EXISTS ix_claim_status             ON claims.claim (status);
CREATE INDEX IF NOT EXISTS ix_claim_batch              ON claims.claim (batch_id) WHERE batch_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS claims.claim_line (
    claim_line_id         uuid PRIMARY KEY,
    claim_id              uuid NOT NULL REFERENCES claims.claim(claim_id),
    fulfillment_ref       uuid,
    fulfillment_type      varchar(20) NOT NULL DEFAULT 'None'
        CHECK (fulfillment_type IN ('OrderFulfillment','DispenseEvent','None')),
    code_system           varchar(10) NOT NULL CHECK (code_system IN ('CPT','LOINC','LOCAL','DRUG')),
    code                  varchar(20) NOT NULL,
    description           varchar(200),
    quantity              numeric(14,3) NOT NULL CHECK (quantity > 0),
    billed_amount         numeric(14,2) NOT NULL DEFAULT 0 CHECK (billed_amount >= 0),
    contract_price        numeric(14,2) CHECK (contract_price IS NULL OR contract_price >= 0),
    allowed_amount        numeric(14,2) CHECK (allowed_amount IS NULL OR allowed_amount >= 0),
    member_share          numeric(14,2) CHECK (member_share IS NULL OR member_share >= 0),
    status                varchar(20) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Approved','PartiallyApproved','Denied','Adjusted','Void')),
    system_recommendation varchar(24)
        CHECK (system_recommendation IS NULL OR system_recommendation IN
              ('RecommendApprove','RecommendPartial','RecommendDeny','RequiresManualReview')),
    reason_codes          text[] NOT NULL DEFAULT '{}',
    rule_version          varchar(20),
    authorization_id      uuid,
    -- A discriminated fulfillment reference is required whenever a type is set, and forbidden for 'None'.
    CHECK ((fulfillment_type = 'None') = (fulfillment_ref IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_claim_line_claim ON claims.claim_line (claim_id);
CREATE INDEX IF NOT EXISTS ix_claim_line_status ON claims.claim_line (status);
CREATE INDEX IF NOT EXISTS ix_claim_line_code ON claims.claim_line (code_system, code);

-- THE NO-DOUBLE-BILLING GUARD (22 §10A.2): at most one live payable line per fulfillment/dispense reference.
CREATE UNIQUE INDEX IF NOT EXISTS ux_claim_line_fulfillment
    ON claims.claim_line (fulfillment_ref)
    WHERE fulfillment_ref IS NOT NULL AND status <> 'Void';

-- Dedupe ledger for the idempotent auto-derive event consumers (dedupe on event id).
CREATE TABLE IF NOT EXISTS claims.processed_event (
    event_id    uuid PRIMARY KEY,
    event_type  text NOT NULL,
    consumed_at timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------------------------------------------
-- Row-Level Security (defense-in-depth, layer 4 — independent of the ABAC provider-ownership check). Two session
-- GUCs the app binds per request under the NOBYPASSRLS role hbmp_app:
--   app.tenant_id   — tenant separation (no cross-tenant read).
--   app.provider_id — set for provider-scoped users; empty/unset ⇒ Mersal staff (Claims Officers), tenant-wide.
-- Under the dev superuser (hbmp) RLS is bypassed; it becomes live under hbmp_app in Tier 2/3. FORCE so even the
-- table owner is subject to the predicate.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT USAGE ON SCHEMA claims TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON claims.claim, claims.claim_line, claims.claim_seq TO hbmp_app;
        GRANT SELECT, INSERT ON claims.processed_event TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.claim ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim ON claims.claim;
CREATE POLICY rls_claim ON claims.claim USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR provider_id::text = current_setting('app.provider_id', true)
    )
);

-- claim_line has no tenant/provider column: it inherits its parent claim's visibility (the EXISTS re-applies the
-- claim policy, so a provider can never see a line of a claim they cannot see).
ALTER TABLE claims.claim_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_line FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_line ON claims.claim_line;
CREATE POLICY rls_claim_line ON claims.claim_line USING (
    EXISTS (SELECT 1 FROM claims.claim c WHERE c.claim_id = claim_line.claim_id)
);
