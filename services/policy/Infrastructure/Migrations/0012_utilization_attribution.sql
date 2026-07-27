-- policy-service — 0012 tier attribution for the consumption ledger (phase 19.4). Additive + idempotent.
--
-- ============================================================================================================
-- WHY THE LEDGER NEEDS A PROVIDER AND A SERVICE DATE
-- ============================================================================================================
-- 19.4 must split utilization by NETWORK TIER, because steering volume from out-of-network to a contracted
-- tier is the largest single cost lever the Network Team and Finance have — and they cannot pull it without
-- seeing where the volume currently sits.
--
-- A tier is a property of (provider, service date): 19.1b resolves it from provider_network_assignment as it
-- stood on the day the care happened. The ledger recorded neither, so until now every movement was
-- tier-anonymous and the split was unanswerable.
--
-- TWO CHOICES MADE HERE, BOTH DELIBERATE:
--
-- 1. We store the PROVIDER, not the resolved tier code. Resolving at consume time would freeze a tier that a
--    later 19.1b *correction* (an assignment made against the wrong provider) is supposed to fix. Storing the
--    provider and resolving at report time means a corrected assignment corrects every report that follows,
--    which is the whole point of having a correction verb. It also keeps an HTTP call off the consume path,
--    where a provider-service outage must never be able to stall the accumulator.
--
-- 2. We store the SERVICE DATE, not just applied_at. applied_at is when the accumulator moved, which lags the
--    care by however long the broker, the outbox and any retry took. Resolving a tier at applied_at would
--    price February's care against March's network — the exact error 19.1b's service-date rule exists to
--    prevent.
--
-- Rows written before this migration keep NULL in both. They report in an explicit UNATTRIBUTED bucket and are
-- never folded into in-network: understating out-of-network biases the error in the direction that flatters
-- the network, on the very number the network is judged by.

ALTER TABLE policy.benefit_consumption ADD COLUMN IF NOT EXISTS provider_id uuid;
ALTER TABLE policy.benefit_consumption ADD COLUMN IF NOT EXISTS provider_location_id uuid;
ALTER TABLE policy.benefit_consumption ADD COLUMN IF NOT EXISTS service_date date;

-- Window-scoped ledger reads: "this member's movements between two dates", the shape every utilization
-- endpoint asks for. service_date rather than applied_at, for the reason above.
CREATE INDEX IF NOT EXISTS ix_benefit_consumption_service_window
    ON policy.benefit_consumption (beneficiary_id, service_date DESC)
    WHERE service_date IS NOT NULL;

-- The tier-resolution fan-out: distinct (provider, service_date) pairs for a scope, so a report resolves each
-- pair once instead of once per movement.
CREATE INDEX IF NOT EXISTS ix_benefit_consumption_provider
    ON policy.benefit_consumption (provider_id, service_date)
    WHERE provider_id IS NOT NULL;

-- ---- Read paths the utilization scopes walk ------------------------------------------------------------------
-- Group / plan / policy / payer all funnel through enrollment, and every one of them reads "the live members
-- of this scope" before touching a single accumulator row.
CREATE INDEX IF NOT EXISTS ix_enrollment_group_status
    ON policy.enrollment (group_id, status) WHERE is_deleted = false AND group_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_enrollment_plan_status
    ON policy.enrollment (policy_plan_id, status) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_enrollment_policy_status
    ON policy.enrollment (policy_id, status) WHERE is_deleted = false;

-- The accumulator join: coverage by beneficiary, then its limits.
CREATE INDEX IF NOT EXISTS ix_coverage_beneficiary_active
    ON policy.coverage (beneficiary_id, benefit_category_id) WHERE is_deleted = false;
