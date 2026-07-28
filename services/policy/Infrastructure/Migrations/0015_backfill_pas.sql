-- ============================================================================================================
-- Phase 19.7 — backfill existing policies into the 19.x policy-administration structures.
--
-- Before phase 19 a policy was a row with a free-text `sponsor` and a set of coverages hanging directly off
-- the beneficiary. Phase 19 gave the same facts a spine: a PAYER the contract is with, a PLAN VERSION that
-- says what is covered, a POLICY_PLAN electing that version onto the policy, and an ENROLMENT that puts a
-- member on it. Every read path built in 19.4–19.6b assumes that spine exists.
--
-- ============================================================================================================
-- WHY THIS IS A BACKFILL AND NOT A DEFAULT
-- ============================================================================================================
-- The alternative was to let the new code treat a missing payer/plan/enrolment as "unknown" forever. That
-- reads fine on a screen and is wrong everywhere it matters: a payer-scoped user cannot see an unattributed
-- policy at all (by design — see PayerScope), utilization cannot be attributed, and the 19.6b outlier view
-- would report every legacy member as a data-quality finding in perpetuity. A permanent "unknown" is not a
-- migration, it is a second data model.
--
-- ============================================================================================================
-- REVERSIBLE BY BATCH
-- ============================================================================================================
-- Every row this creates is stamped with one `migration.batch` id, reusing the toolkit's existing
-- reversibility contract (tools/migration: rollback soft-reverts exactly the rows a batch touched). The
-- reversal is at the bottom of this file, commented, because a backfill you cannot undo is a decision you can
-- only make once — and this one reverse-engineers a plan version from consumption data, which is an inference,
-- not a fact.
--
-- IDEMPOTENT: re-running finds its own rows by provenance and does nothing. It is safe to run twice, and the
-- reconciliation report is regenerated each time.
-- ============================================================================================================

-- Provenance. A dedicated table rather than a column on each entity: the entities are owned by the domain and
-- adding a `backfill_batch_id` to `policy_plan` would put a migration concern in the benefit model forever.
CREATE TABLE IF NOT EXISTS policy.backfill_provenance (
    entity_type  text        NOT NULL,
    entity_id    uuid        NOT NULL,
    batch_id     uuid        NOT NULL,
    tenant_id    text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_type, entity_id),
    CONSTRAINT ck_backfill_entity CHECK (entity_type IN ('payer','plan','plan_version','benefit_rule','policy_plan','enrollment'))
);
CREATE INDEX IF NOT EXISTS ix_backfill_batch ON policy.backfill_provenance (batch_id);

-- The reconciliation report. Written by the backfill, read by a human before anyone trusts the result — a
-- migration that reports only "done" has told you nothing you can act on.
CREATE TABLE IF NOT EXISTS policy.backfill_reconciliation (
    batch_id      uuid        NOT NULL,
    measure       text        NOT NULL,
    expected      bigint      NOT NULL,
    actual        bigint      NOT NULL,
    reconciled    boolean     GENERATED ALWAYS AS (expected = actual) STORED,
    detail        text,
    computed_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (batch_id, measure)
);

DO $backfill$
DECLARE
    v_batch      uuid := gen_random_uuid();
    v_tenant     text;
    v_payer      uuid;
    v_plan       uuid;
    v_version    uuid;
    v_now        timestamptz := now();
    v_policies   bigint := 0;
    v_plans      bigint := 0;
    v_enrolments bigint := 0;
    v_expected   bigint := 0;
BEGIN
    -- Already run? Reuse the batch so the report is refreshed rather than duplicated.
    SELECT batch_id INTO v_batch FROM policy.backfill_provenance LIMIT 1;
    IF v_batch IS NULL THEN
        v_batch := gen_random_uuid();
    END IF;

    FOR v_tenant IN SELECT DISTINCT tenant_id FROM policy.policy WHERE NOT is_deleted
    LOOP
        -- ── 1. The default payer ──────────────────────────────────────────────────────────────────────────
        --
        -- "Mersal — self-funded" is the truthful answer for every pre-19.2 policy: Mersal WAS the payer, the
        -- model just had nowhere to record it. Naming it explicitly beats leaving payer_id NULL, which reads
        -- as "we do not know" and hides the ones that genuinely are unknown.
        SELECT payer_id INTO v_payer FROM policy.payer
        WHERE tenant_id = v_tenant AND payer_code = 'MERSAL-SF' AND NOT is_deleted;

        IF v_payer IS NULL THEN
            v_payer := gen_random_uuid();
            INSERT INTO policy.payer (payer_id, tenant_id, payer_code, name_en, name_ar, payer_type,
                                      contact, status, is_deleted, row_version, created_at, updated_at)
            VALUES (v_payer, v_tenant, 'MERSAL-SF', 'Mersal — self-funded', 'مرسال — تمويل ذاتي',
                    'SelfFunded', '{}'::jsonb, 'Active', false, 1, v_now, v_now);
            INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            VALUES ('payer', v_payer, v_batch, v_tenant) ON CONFLICT DO NOTHING;
        END IF;

        UPDATE policy.policy SET payer_id = v_payer, updated_at = v_now
        WHERE tenant_id = v_tenant AND payer_id IS NULL AND NOT is_deleted;

        -- ── 2. The legacy plan + one Active version ───────────────────────────────────────────────────────
        --
        -- REVERSE-ENGINEERED, and the name says so. The version's benefit rules are derived from the coverage
        -- rows that actually exist: for each benefit category ever covered, the MOST COMMON limit becomes the
        -- rule's limit. That is an inference about what somebody intended, and calling the plan "Legacy" is
        -- what stops it being mistaken for an authored product later.
        SELECT plan_id INTO v_plan FROM policy.plan
        WHERE tenant_id = v_tenant AND plan_code = 'LEGACY-BF' AND NOT is_deleted;

        IF v_plan IS NULL THEN
            v_plan := gen_random_uuid();
            INSERT INTO policy.plan (plan_id, tenant_id, plan_code, name_en, name_ar, description, category,
                                     status, is_deleted, row_version, created_at, updated_at)
            VALUES (v_plan, v_tenant, 'LEGACY-BF', 'Legacy coverage (reconstructed)',
                    'التغطية السابقة (مُعاد بناؤها)',
                    'Reverse-engineered from coverage rows that predate phase 19. Amend to a real plan version '
                    || 'before authoring against it.', 'Primary', 'Active', false, 1, v_now, v_now);
            INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            VALUES ('plan', v_plan, v_batch, v_tenant) ON CONFLICT DO NOTHING;

            v_version := gen_random_uuid();
            -- Effective from the EARLIEST coverage in the tenant: a version that started later than the
            -- coverage it describes would make every historical claim fall outside its own plan.
            INSERT INTO policy.plan_version (plan_version_id, tenant_id, plan_id, version_no, effective_from,
                                             status, activated_at, row_version, created_at, updated_at)
            SELECT v_version, v_tenant, v_plan, 1,
                   COALESCE(MIN(c.effective_from), CURRENT_DATE), 'Active', v_now, 1, v_now, v_now
            FROM policy.coverage c WHERE c.tenant_id = v_tenant AND NOT c.is_deleted;

            INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            VALUES ('plan_version', v_version, v_batch, v_tenant) ON CONFLICT DO NOTHING;

            -- One rule per category that actually has coverage, with the modal limit. Categories nobody was
            -- ever covered for are left OUT rather than added as not-covered: an absent rule is honest about
            -- never having been configured, while `is_covered = false` asserts a decision nobody made.
            INSERT INTO policy.benefit_rule (rule_id, tenant_id, plan_version_id, benefit_category_id,
                                             is_covered, limit_type, limit_value, reset_period,
                                             waiting_period_days, requires_preauth, exclusions,
                                             deductible_waived, notes, created_at, updated_at)
            SELECT gen_random_uuid(), v_tenant, v_version, s.benefit_category_id, true,
                   s.limit_type, s.limit_value, COALESCE(s.reset_period, 'Yearly'), 0, false, '[]'::jsonb,
                   false, 'Backfilled from ' || s.observations || ' coverage row(s).', v_now, v_now
            FROM (
                SELECT DISTINCT ON (c.benefit_category_id)
                       c.benefit_category_id, cl.limit_type, cl.limit_value, cl.reset_period,
                       COUNT(*) OVER (PARTITION BY c.benefit_category_id) AS observations
                FROM policy.coverage c
                JOIN policy.coverage_limit cl ON cl.coverage_id = c.coverage_id
                WHERE c.tenant_id = v_tenant AND NOT c.is_deleted
                ORDER BY c.benefit_category_id, COUNT(*) OVER (PARTITION BY c.benefit_category_id, cl.limit_value) DESC
            ) s;

            INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
            SELECT 'benefit_rule', r.rule_id, v_batch, v_tenant
            FROM policy.benefit_rule r WHERE r.plan_version_id = v_version
            ON CONFLICT DO NOTHING;
        ELSE
            SELECT plan_version_id INTO v_version FROM policy.plan_version
            WHERE plan_id = v_plan ORDER BY version_no LIMIT 1;
        END IF;

        -- ── 3. One default policy_plan per policy ─────────────────────────────────────────────────────────
        --
        -- SINGLE and DEFAULT, both deliberate. 0008's exclusion constraint allows at most one default per
        -- policy; making it the default is what lets an enrolment that names no plan still resolve, which is
        -- every enrolment that existed before 19.2b.
        INSERT INTO policy.policy_plan (policy_plan_id, tenant_id, policy_id, plan_version_id, plan_label,
                                        effective_from, is_default, status, is_deleted, row_version,
                                        created_at, updated_at)
        SELECT gen_random_uuid(), p.tenant_id, p.policy_id, v_version, 'Standard',
               p.effective_from, true, 'Active', false, 1, v_now, v_now
        FROM policy.policy p
        WHERE p.tenant_id = v_tenant AND NOT p.is_deleted
          AND NOT EXISTS (SELECT 1 FROM policy.policy_plan pp
                          WHERE pp.policy_id = p.policy_id AND NOT pp.is_deleted);

        INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
        SELECT 'policy_plan', pp.policy_plan_id, v_batch, v_tenant
        FROM policy.policy_plan pp
        JOIN policy.policy p ON p.policy_id = pp.policy_id
        WHERE p.tenant_id = v_tenant AND pp.plan_version_id = v_version
        ON CONFLICT DO NOTHING;

        -- ── 4. Enrolments from existing coverage ──────────────────────────────────────────────────────────
        --
        -- One per (beneficiary, policy) that holds coverage and has no enrolment yet. Relationship is
        -- 'Principal' because the pre-19.2 model had no dependants concept at all — recording a guess would
        -- be worse than recording the only thing that was ever true.
        --
        -- Coverage rows are then POINTED at their new enrolment. Without that step the member exists and
        -- their entitlement does not belong to them, which every 19.4 utilization query reads as zero.
        WITH candidates AS (
            SELECT c.tenant_id, c.beneficiary_id, c.policy_id,
                   MIN(c.effective_from) AS from_date,
                   MAX(c.effective_to)   AS to_date
            FROM policy.coverage c
            WHERE c.tenant_id = v_tenant AND NOT c.is_deleted AND c.enrollment_id IS NULL
            GROUP BY c.tenant_id, c.beneficiary_id, c.policy_id
        ), created AS (
            INSERT INTO policy.enrollment (enrollment_id, tenant_id, beneficiary_id, policy_id, policy_plan_id,
                                           member_no, relationship, effective_from, effective_to, status,
                                           source_plan_version_id, is_deleted, row_version, created_at, updated_at,
                                           idempotency_key)
            SELECT gen_random_uuid(), k.tenant_id, k.beneficiary_id, k.policy_id, pp.policy_plan_id,
                   'MRS-M-BF-' || substr(replace(k.beneficiary_id::text, '-', ''), 1, 12),
                   'Principal', k.from_date, k.to_date, 'Active', v_version, false, 1, v_now, v_now,
                   -- The idempotency key IS the natural key of the backfill: re-running cannot create a
                   -- second membership for the same person on the same policy.
                   'backfill:' || k.policy_id::text || ':' || k.beneficiary_id::text
            FROM candidates k
            JOIN policy.policy_plan pp ON pp.policy_id = k.policy_id AND pp.is_default AND NOT pp.is_deleted
            WHERE NOT EXISTS (
                SELECT 1 FROM policy.enrollment e
                WHERE e.beneficiary_id = k.beneficiary_id AND e.policy_id = k.policy_id AND NOT e.is_deleted)
            RETURNING enrollment_id, beneficiary_id, policy_id, tenant_id
        )
        INSERT INTO policy.backfill_provenance (entity_type, entity_id, batch_id, tenant_id)
        SELECT 'enrollment', c.enrollment_id, v_batch, c.tenant_id FROM created c
        ON CONFLICT DO NOTHING;

        UPDATE policy.coverage c
        SET enrollment_id = e.enrollment_id,
            source_plan_version_id = COALESCE(c.source_plan_version_id, v_version)
        FROM policy.enrollment e
        WHERE c.tenant_id = v_tenant AND c.enrollment_id IS NULL AND NOT c.is_deleted
          AND e.beneficiary_id = c.beneficiary_id AND e.policy_id = c.policy_id AND NOT e.is_deleted;
    END LOOP;

    -- ── 5. The reconciliation report ──────────────────────────────────────────────────────────────────────
    --
    -- Counts, not a status. "Backfill complete" is not a reconciliation; "every policy that existed has
    -- exactly one default plan, and 4 do not" is.
    DELETE FROM policy.backfill_reconciliation WHERE batch_id = v_batch;

    SELECT COUNT(*) INTO v_policies FROM policy.policy WHERE NOT is_deleted;
    SELECT COUNT(*) INTO v_plans FROM policy.policy p
    WHERE NOT p.is_deleted
      AND EXISTS (SELECT 1 FROM policy.policy_plan pp
                  WHERE pp.policy_id = p.policy_id AND pp.is_default AND NOT pp.is_deleted);
    INSERT INTO policy.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'policies_with_a_default_plan', v_policies, v_plans,
            'Every live policy must have exactly one default policy_plan, or enrolment without a named plan cannot resolve.');

    SELECT COUNT(*) INTO v_policies FROM policy.policy WHERE NOT is_deleted;
    SELECT COUNT(*) INTO v_plans FROM policy.policy WHERE NOT is_deleted AND payer_id IS NOT NULL;
    INSERT INTO policy.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'policies_with_a_payer', v_policies, v_plans,
            'An unattributed policy is invisible to every payer-scoped user, by design.');

    -- LIKE FOR LIKE. The first cut of this measure compared "people holding coverage" against the TOTAL
    -- enrolment count and reported a mismatch on a healthy database: an enrolment may legitimately exist
    -- with no coverage (a plan version that covers nothing, a membership created and later emptied), and
    -- counting those as a backfill failure would train whoever reads this report to ignore it. The invariant
    -- is one-directional — everyone WITH coverage is enrolled — so both sides are drawn from the same set.
    SELECT COUNT(*) INTO v_expected FROM (
        SELECT DISTINCT c.beneficiary_id, c.policy_id
        FROM policy.coverage c WHERE NOT c.is_deleted) k;
    SELECT COUNT(*) INTO v_enrolments FROM (
        SELECT DISTINCT c.beneficiary_id, c.policy_id
        FROM policy.coverage c
        WHERE NOT c.is_deleted
          AND EXISTS (SELECT 1 FROM policy.enrollment e
                      WHERE e.beneficiary_id = c.beneficiary_id AND e.policy_id = c.policy_id
                        AND NOT e.is_deleted)) k;
    INSERT INTO policy.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'memberships_for_every_covered_person', v_expected, v_enrolments,
            'Every (beneficiary, policy) that holds coverage must have a membership. A shortfall means somebody is covered and not enrolled.');

    SELECT COUNT(*) INTO v_expected FROM policy.coverage WHERE NOT is_deleted;
    SELECT COUNT(*) INTO v_enrolments FROM policy.coverage WHERE NOT is_deleted AND enrollment_id IS NOT NULL;
    INSERT INTO policy.backfill_reconciliation (batch_id, measure, expected, actual, detail)
    VALUES (v_batch, 'coverage_attributed_to_a_membership', v_expected, v_enrolments,
            'Coverage with no enrollment_id is entitlement that belongs to nobody — every 19.4 utilization query reads it as zero.');

    RAISE NOTICE 'PAS backfill batch %: see policy.backfill_reconciliation for the report.', v_batch;
END
$backfill$;

GRANT SELECT ON policy.backfill_provenance, policy.backfill_reconciliation TO hbmp_app;

-- The house rule: a table carrying `tenant_id` gets a row-level policy, and provenance is no exception.
-- Isolation resting on the application predicate alone is precisely the drift libs/architecture's
-- Every_tenant_scoped_table_has_an_rls_policy exists to catch — and it caught this one.
ALTER TABLE policy.backfill_provenance ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.backfill_provenance FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_backfill_provenance ON policy.backfill_provenance;
CREATE POLICY rls_backfill_provenance ON policy.backfill_provenance
    USING (tenant_id = current_setting('app.tenant_id', true));


-- ============================================================================================================
-- REVERSAL (by batch). Not executed here — kept as the documented undo, because reverse-engineering a plan
-- version from consumption data is an INFERENCE, and an inference you cannot withdraw is a fact you invented.
--
--   BEGIN;
--   UPDATE policy.coverage SET enrollment_id = NULL
--    WHERE enrollment_id IN (SELECT entity_id FROM policy.backfill_provenance
--                            WHERE entity_type = 'enrollment' AND batch_id = :batch);
--   DELETE FROM policy.enrollment  WHERE enrollment_id  IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'enrollment'   AND batch_id = :batch);
--   DELETE FROM policy.policy_plan WHERE policy_plan_id IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'policy_plan'  AND batch_id = :batch);
--   DELETE FROM policy.benefit_rule WHERE rule_id       IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'benefit_rule' AND batch_id = :batch);
--   DELETE FROM policy.plan_version WHERE plan_version_id IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'plan_version' AND batch_id = :batch);
--   DELETE FROM policy.plan        WHERE plan_id        IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'plan'         AND batch_id = :batch);
--   UPDATE policy.policy SET payer_id = NULL
--    WHERE payer_id IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'payer' AND batch_id = :batch);
--   DELETE FROM policy.payer       WHERE payer_id       IN (SELECT entity_id FROM policy.backfill_provenance WHERE entity_type = 'payer'        AND batch_id = :batch);
--   DELETE FROM policy.backfill_provenance WHERE batch_id = :batch;
--   COMMIT;
--
-- A hard DELETE rather than a soft one, and only here: these rows never existed as anybody's decision. Soft-
-- deleting them would leave a "Legacy coverage (reconstructed)" plan visible in every plan list forever, which
-- is exactly the confusion the reversal exists to remove. Rows a HUMAN then edited are protected by the
-- row_version check the toolkit applies before reverting a batch.
-- ============================================================================================================
