-- ============================================================================================================
-- Phase 19.7 — the default network tier set, and every existing provider placed in it.
--
-- 19.1b introduced tiers and made cost-share resolve per tier at the service date. Nothing put the EXISTING
-- providers into a tier, so every one of them resolved to "no tier" — which the resolver treats as
-- unattributed, which means a benefit rule's tier-specific co-pay never applied and the plan-level default
-- silently governed instead. The feature was live and inert.
--
-- WHY T1 AND OON, AND ONLY THOSE TWO
-- A tier structure is a commercial decision the Network Team makes with real contracts in front of them. This
-- backfill must NOT invent one. Two tiers is the minimum that makes the model work at all:
--
--   T1   in-network, rank 1 — where every contracted provider actually is today. A contract IS the network.
--   OON  out-of-network, rank 99 — the bucket that must exist for the resolver to have an answer for a
--        provider Mersal has no contract with, and for 19.6b's leakage rate to have a denominator.
--
-- Splitting T1 into tiers with different cost-share is the Network Team's work, done through the 19.6 screen.
-- Guessing at it here would put commercial terms into a migration.
--
-- REVERSIBLE and IDEMPOTENT on the same contract as the policy backfill: rows are stamped with provenance,
-- re-running is a no-op, and the reversal is documented at the bottom.
-- ============================================================================================================

CREATE TABLE IF NOT EXISTS provider.backfill_provenance (
    entity_type text        NOT NULL,
    entity_id   uuid        NOT NULL,
    batch_id    uuid        NOT NULL,
    tenant_id   text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_type, entity_id),
    CONSTRAINT ck_provider_backfill_entity CHECK (entity_type IN ('network_tier','provider_network_assignment'))
);
CREATE INDEX IF NOT EXISTS ix_provider_backfill_batch ON provider.backfill_provenance (batch_id);

CREATE TABLE IF NOT EXISTS provider.backfill_reconciliation (
    batch_id    uuid        NOT NULL,
    measure     text        NOT NULL,
    expected    bigint      NOT NULL,
    actual      bigint      NOT NULL,
    reconciled  boolean     GENERATED ALWAYS AS (expected = actual) STORED,
    detail      text,
    computed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (batch_id, measure)
);

DO $backfill$
DECLARE
    v_batch     uuid;
    v_tenant    text;
    v_t1        uuid;
    v_oon       uuid;
    v_now       timestamptz := now();
    v_expected  bigint := 0;
    v_actual    bigint := 0;
BEGIN
    SELECT batch_id INTO v_batch FROM provider.backfill_provenance LIMIT 1;
    IF v_batch IS NULL THEN v_batch := gen_random_uuid(); END IF;

    FOR v_tenant IN SELECT DISTINCT tenant_id FROM provider.provider WHERE NOT is_deleted
    LOOP
        SELECT network_tier_id INTO v_t1 FROM provider.network_tier
        WHERE tenant_id = v_tenant AND tier_code = 'T1' AND NOT is_deleted;

        IF v_t1 IS NULL THEN
            v_t1 := gen_random_uuid();
            INSERT INTO provider.network_tier (network_tier_id, tenant_id, tier_code, name_en, name_ar, rank,
                                               description, is_out_of_network, status, is_deleted, row_version,
                                               created_at, updated_at)
            VALUES (v_t1, v_tenant, 'T1', 'Tier 1 — contracted network', 'الشريحة الأولى — الشبكة المتعاقدة', 1,
                    'Every provider Mersal holds a contract with. Split into finer tiers through the network '
                    || 'administration screen when the commercial terms differ.', false, 'Active', false, 1,
                    v_now, v_now);
            INSERT INTO provider.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            VALUES ('network_tier', v_t1, v_batch, v_tenant) ON CONFLICT DO NOTHING;
        END IF;

        SELECT network_tier_id INTO v_oon FROM provider.network_tier
        WHERE tenant_id = v_tenant AND tier_code = 'OON' AND NOT is_deleted;

        IF v_oon IS NULL THEN
            v_oon := gen_random_uuid();
            -- Rank 99, not 2. Rank orders SPECIFICITY for the resolver, and out-of-network must always lose to
            -- any real tier — leaving room between them means a tier added later does not have to renumber.
            INSERT INTO provider.network_tier (network_tier_id, tenant_id, tier_code, name_en, name_ar, rank,
                                               description, is_out_of_network, status, is_deleted, row_version,
                                               created_at, updated_at)
            VALUES (v_oon, v_tenant, 'OON', 'Out of network', 'خارج الشبكة', 99,
                    'No contract. Exists so the resolver has an answer and so leakage has a denominator.',
                    true, 'Active', false, 1, v_now, v_now);
            INSERT INTO provider.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            VALUES ('network_tier', v_oon, v_batch, v_tenant) ON CONFLICT DO NOTHING;
        END IF;

        -- Every contracted provider into T1, FROM THEIR CONTRACT START DATE rather than from today.
        --
        -- That date matters and is easy to get wrong: tier assignment is effective-dated and the resolver
        -- answers "which tier on the SERVICE date". Backdating to the contract start means a claim for a visit
        -- last March resolves to the tier that provider was actually in last March. Assigning from today would
        -- have made every historical service out-of-network overnight — and 19.6b's leakage rate would have
        -- reported a network collapse that never happened.
        -- `scope_ref` IS the provider id at Provider scope (0008 denormalises provider_id from it), so both
        -- columns carry the same value here. At Location scope they diverge: scope_ref is the location and
        -- provider_id is its parent.
        INSERT INTO provider.provider_network_assignment (assignment_id, tenant_id, network_tier_id, provider_id,
                                                          scope, scope_ref, effective_from, status, is_deleted,
                                                          row_version, created_at, updated_at)
        SELECT gen_random_uuid(), p.tenant_id, v_t1, p.provider_id, 'Provider', p.provider_id,
               COALESCE(
                   (SELECT MIN(c.effective_from) FROM provider.provider_contract c
                    WHERE c.provider_id = p.provider_id AND NOT c.is_deleted),
                   -- No contract row at all: the provider predates contract tracking. Their own creation date
                   -- is the earliest defensible claim — it is when Mersal first knew about them.
                   p.created_at::date)
        , 'Active', false, 1, v_now, v_now
        FROM provider.provider p
        WHERE p.tenant_id = v_tenant AND NOT p.is_deleted
          AND NOT EXISTS (SELECT 1 FROM provider.provider_network_assignment a
                          WHERE a.provider_id = p.provider_id AND NOT a.is_deleted);

        INSERT INTO provider.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
        SELECT 'provider_network_assignment', a.assignment_id, v_batch, v_tenant
        FROM provider.provider_network_assignment a
        WHERE a.tenant_id = v_tenant AND a.network_tier_id = v_t1
        ON CONFLICT DO NOTHING;
    END LOOP;

    DELETE FROM provider.backfill_reconciliation WHERE batch_id = v_batch;

    SELECT COUNT(*) INTO v_expected FROM provider.provider WHERE NOT is_deleted;
    SELECT COUNT(DISTINCT a.provider_id) INTO v_actual
    FROM provider.provider_network_assignment a
    JOIN provider.provider p ON p.provider_id = a.provider_id AND NOT p.is_deleted
    WHERE NOT a.is_deleted;
    INSERT INTO provider.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'providers_in_a_tier', v_expected, v_actual,
            'A provider in no tier resolves as unattributed, so tier-specific cost-share never applies to them.');

    SELECT COUNT(DISTINCT tenant_id) * 2 INTO v_expected FROM provider.provider WHERE NOT is_deleted;
    SELECT COUNT(*) INTO v_actual FROM provider.network_tier
    WHERE NOT is_deleted AND tier_code IN ('T1', 'OON');
    INSERT INTO provider.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'default_tier_set_per_tenant', v_expected, v_actual,
            'Both T1 and OON must exist per tenant, or the resolver has no answer for an uncontracted provider.');

    RAISE NOTICE 'Network-tier backfill batch %: see provider.backfill_reconciliation.', v_batch;
END
$backfill$;

GRANT SELECT ON provider.backfill_provenance, provider.backfill_reconciliation TO hbmp_app;

-- The house rule: a table carrying `tenant_id` gets a row-level policy, and provenance is no exception.
-- Isolation resting on the application predicate alone is precisely the drift libs/architecture's
-- Every_tenant_scoped_table_has_an_rls_policy exists to catch — and it caught this one.
ALTER TABLE provider.backfill_provenance ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.backfill_provenance FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_backfill_provenance ON provider.backfill_provenance;
CREATE POLICY rls_backfill_provenance ON provider.backfill_provenance
    USING (tenant_id = current_setting('app.tenant_id', true));


-- ============================================================================================================
-- REVERSAL (by batch), documented rather than executed:
--
--   BEGIN;
--   DELETE FROM provider.provider_network_assignment
--    WHERE assignment_id IN (SELECT entity_id FROM provider.backfill_provenance
--                            WHERE entity_type = 'provider_network_assignment' AND batch_id = :batch);
--   DELETE FROM provider.network_tier
--    WHERE network_tier_id IN (SELECT entity_id FROM provider.backfill_provenance
--                              WHERE entity_type = 'network_tier' AND batch_id = :batch);
--   DELETE FROM provider.backfill_provenance WHERE batch_id = :batch;
--   COMMIT;
--
-- Reverse the ASSIGNMENTS before the TIERS. A tier with assignments still pointing at it is a foreign-key
-- failure at best, and at worst — if the constraint were ever relaxed — an assignment to a tier that no longer
-- exists, which the resolver would read as unattributed with nothing in the data to say why.
-- ============================================================================================================
