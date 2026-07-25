-- claims-service — Phase 10b.2 batching + batch lifecycle (36-claims-management §4/§6, 22-data-dictionary §10A.5,
-- 23-state-machines §9).
--
-- THE POINT OF THIS MIGRATION is the single-open-batch guard: ux_claim_one_open_batch makes it IMPOSSIBLE for one
-- claim to sit in two live (Open/UnderReview) batches — so a claim can never be settled twice. The item row carries
-- a materialized batch_status (kept in step with the parent batch on every transition) so the guarantee is a plain
-- partial unique index, not application logic. A violation surfaces as CLAIM_ALREADY_BATCHED (409). Membership rows
-- are recorded, never deleted: removal sets removed_at + removal_reason (append-only history).

CREATE TABLE IF NOT EXISTS claims.batch_seq (
    year       int PRIMARY KEY,
    last_value int NOT NULL
);

CREATE TABLE IF NOT EXISTS claims.claim_batch (
    batch_id                uuid PRIMARY KEY,
    batch_no                varchar(20) NOT NULL UNIQUE,
    batch_type              varchar(16) NOT NULL CHECK (batch_type IN ('Provider','Reimbursement')),
    selection_mode          varchar(16) NOT NULL CHECK (selection_mode IN ('DateRange','ProviderBranch','ProviderGroup','Manual')),
    payee_provider_id       uuid,
    provider_location_id    uuid,
    tenant_id               text NOT NULL,
    period_from             date NOT NULL,
    period_to               date NOT NULL,
    status                  varchar(20) NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','UnderReview','Decided','SettlementIssued','Closed','Cancelled')),
    total_claimed           numeric(16,2) NOT NULL DEFAULT 0 CHECK (total_claimed >= 0),
    total_priced            numeric(16,2) NOT NULL DEFAULT 0 CHECK (total_priced >= 0),
    total_approved          numeric(16,2) NOT NULL DEFAULT 0 CHECK (total_approved >= 0),
    total_adjusted          numeric(16,2) NOT NULL DEFAULT 0,
    total_denied            numeric(16,2) NOT NULL DEFAULT 0 CHECK (total_denied >= 0),
    net_payable             numeric(16,2) NOT NULL DEFAULT 0,
    created_by              text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    decided_at              timestamptz,
    frozen_at               timestamptz,
    settlement_document_id  uuid,
    -- A Provider batch has a payee; a Reimbursement batch has none.
    CHECK ((batch_type = 'Provider') = (payee_provider_id IS NOT NULL)),
    CHECK (period_to >= period_from)
);
CREATE INDEX IF NOT EXISTS ix_batch_payee_period ON claims.claim_batch (payee_provider_id, period_from);
CREATE INDEX IF NOT EXISTS ix_batch_status ON claims.claim_batch (status);

CREATE TABLE IF NOT EXISTS claims.claim_batch_item (
    batch_item_id  uuid PRIMARY KEY,
    batch_id       uuid NOT NULL REFERENCES claims.claim_batch(batch_id),
    claim_id       uuid NOT NULL REFERENCES claims.claim(claim_id),
    added_by       text,
    added_at       timestamptz NOT NULL DEFAULT now(),
    removed_by     text,
    removed_at     timestamptz,
    removal_reason text,
    -- Materialized from the parent batch so the single-open-batch guard is a pure DB constraint.
    batch_status   varchar(20) NOT NULL DEFAULT 'Open'
        CHECK (batch_status IN ('Open','UnderReview','Decided','SettlementIssued','Closed','Cancelled'))
);
CREATE INDEX IF NOT EXISTS ix_batch_item_batch ON claims.claim_batch_item (batch_id);
CREATE INDEX IF NOT EXISTS ix_batch_item_claim ON claims.claim_batch_item (claim_id);

-- THE SINGLE-OPEN-BATCH GUARD (22 §10A.5): a claim may be in at most ONE live batch.
CREATE UNIQUE INDEX IF NOT EXISTS ux_claim_one_open_batch
    ON claims.claim_batch_item (claim_id)
    WHERE removed_at IS NULL AND batch_status IN ('Open','UnderReview');

-- RLS (defense-in-depth) — batches are tenant + payee-provider scoped, items inherit the batch's visibility.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON claims.claim_batch, claims.claim_batch_item, claims.batch_seq TO hbmp_app;
    END IF;
END $$;

ALTER TABLE claims.claim_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_batch FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_batch ON claims.claim_batch;
CREATE POLICY rls_claim_batch ON claims.claim_batch USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR payee_provider_id::text = current_setting('app.provider_id', true)
    )
);

ALTER TABLE claims.claim_batch_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE claims.claim_batch_item FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_claim_batch_item ON claims.claim_batch_item;
CREATE POLICY rls_claim_batch_item ON claims.claim_batch_item USING (
    EXISTS (SELECT 1 FROM claims.claim_batch b WHERE b.batch_id = claim_batch_item.batch_id)
);
