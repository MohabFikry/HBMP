-- admin-service — ROLLBACK for 0010. Restores the pre-0010 state: RLS enabled, FORCE off.
--
-- Rehearsed on a restored dump before 0010 was applied anywhere (docs/runbooks/migration-0010-rehearsal.md).
-- A migration whose reverse has never been executed is a migration you cannot back out of at 3am, which is
-- the only time anyone wants to.
--
-- WHAT REVERTING COSTS. With FORCE off, a NON-SUPERUSER table owner reads every tenant's rows: measured on
-- the rehearsal copy, 6 of 6 rows visible with no tenant GUC set and 6 with a deliberately wrong one. Do
-- not run this to make a failing query work. The query is telling you the caller has no tenant bound, and
-- the fix is to bind it.
--
-- It does NOT disable RLS, only FORCE — matching what 0010 changed and nothing more. A rollback with a
-- wider blast radius than its migration is how a revert becomes its own incident.

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'user_payer_assignment',
        'branch_scope_grant', 'branch_scope_grant_history',
        'tenant_feature', 'tenant_feature_history',
        'tenant_limit', 'tenant_limit_history']
    LOOP
        EXECUTE format('ALTER TABLE admin.%I NO FORCE ROW LEVEL SECURITY', t);
    END LOOP;
END $$;
