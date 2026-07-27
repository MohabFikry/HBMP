-- admin-service — 0005 replace the fail-OPEN tenant policies with fail-CLOSED ones (audit R2 X6/S2 class).
--
-- NOT in the audit's finding list, found while closing S2. The audit named interop as "the only fail-open
-- policy in the repo"; in fact interop 0001 says it is "mirroring the fleet RLS convention in admin 0001",
-- and it is right — every admin policy from 0001, 0002, 0003 and 0004 carries the same escape:
--     USING (tenant_id = current_setting('app.tenant_id', true)
--            OR current_setting('app.tenant_id', true) IS NULL
--            OR current_setting('app.tenant_id', true) = '')
-- admin never bound the GUC either, so the second disjunct was always true. This matters more here than
-- anywhere else on the platform: admin owns role_binding, break_glass_grant, deprovisioned_user and
-- session_policy — the tables that decide who may reach PHI everywhere else. A connection that reaches this
-- schema without a tenant could read and rewrite the platform's entire access-control state.
--
-- The two halves ship together (see 18.B2): the GUC binder in Api/Program.cs + Infrastructure/
-- AdminPersistence.cs, and the connection string moving off the superuser. Closing the policy alone would
-- deny every row; flipping the connection string alone would do the same. That coupling is why X6 was
-- described as a "trap armed" rather than a live breach.
--
-- Additive + idempotent; no column or data change.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA admin TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA admin TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA admin GRANT SELECT, INSERT, UPDATE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'role_binding','deprovisioned_user','access_review_campaign','session_policy','device_policy',
        'notification_template_version','system_config','user_branch_assignment',
        'break_glass_grant','break_glass_access']
    LOOP
        EXECUTE format('ALTER TABLE admin.%I ENABLE ROW LEVEL SECURITY', t);
        -- FORCE was missing throughout: without it the table OWNER bypasses the policy, so any migration or
        -- maintenance session silently saw every tenant and an isolation test run as the owner would pass
        -- while proving nothing.
        EXECUTE format('ALTER TABLE admin.%I FORCE ROW LEVEL SECURITY', t);
        -- Policies are OR-ed, so a surviving permissive policy re-opens the table. Drop by every name the
        -- earlier migrations used before creating the strict one.
        EXECUTE format('DROP POLICY IF EXISTS rls_tenant_isolation ON admin.%I', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON admin.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON admin.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

-- Deliberately NOT tenant-isolated, each for a stated reason:
--   admin.tenant             — the tenant REGISTRY itself. Isolating it on its own primary key would reduce
--                              the Super Admin's platform-wide list to a single row. The control here is the
--                              authorization gate (AdminPolicies.ManageTenant, Super Admin only), not RLS.
--   admin.policy_proposal    — a global ABAC-bundle surface with no tenant_id by design (see 0001).
--   admin.master_data_version— platform-wide master-data catalogue versions, no tenant_id.
--   admin.access_review_item — child rows reached only through their campaign, which IS isolated; the FK
--                              makes an orphan read impossible without first passing the campaign policy.
