-- admin-service — 0010 FORCE row-level security on the tables added after 0005. ADDITIVE / idempotent.
--
-- 0005 made admin's RLS fail-closed and wrote down exactly why FORCE matters: without it the table OWNER
-- bypasses the policy, so any migration or maintenance session silently sees every tenant, and an isolation
-- test run as the owner passes while proving nothing.
--
-- It applied that to the ten tables that existed at the time, over a hard-coded array. Every table added
-- since — 0006's payer assignment, 0007's branch scope grants, 0008's programme enablement, and the three
-- *_history twins that came with them — enabled RLS and stopped there. Seven tables, each carrying tenant
-- data, each readable in full by the owning role.
--
-- Found by tools/ci/check-tenant-isolation.py, which asks the DATABASE what tables carry a tenant_id rather
-- than trusting a list someone remembered to update. It has never run in CI: the pipeline died at an earlier
-- gate on every push, so the fuzzer built precisely to catch "a table added in a service nobody is thinking
-- about" had never once been reached.
--
-- Policies themselves are already correct on these tables; only the FORCE was missing. Idempotent, so it is
-- safe against the live database as well as a fresh one.

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'user_payer_assignment',
        'branch_scope_grant', 'branch_scope_grant_history',
        'tenant_feature', 'tenant_feature_history',
        'tenant_limit', 'tenant_limit_history']
    LOOP
        -- ENABLE first: FORCE on a table without RLS enabled is accepted but inert, and these were all
        -- enabled by their own migrations. Re-stating it costs nothing and removes the ordering assumption.
        EXECUTE format('ALTER TABLE admin.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE admin.%I FORCE ROW LEVEL SECURITY', t);
    END LOOP;
END $$;
