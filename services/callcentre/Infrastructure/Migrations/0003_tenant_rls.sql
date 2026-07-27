-- callcentre-service — 0003 tenant Row-Level Security (audit R2 X6; ADR-0011). Mirrors document 0002.
--
-- callcentre shipped in Phase 15 with `tenant_id NOT NULL` on every aggregate and ZERO RLS DDL: isolation
-- rested entirely on the application predicate. A missed `.Where(x => x.TenantId == tenant)` on any of the
-- read paths — the member 360, the interaction list, the KPI feed — was a silent cross-tenant disclosure of
-- who called, about whom, and why. This adds the datastore layer underneath, so the predicate becomes
-- defense in depth rather than the only defense.
--
-- Additive + idempotent; no column or data change. Enforced under the NOBYPASSRLS role hbmp_app (the
-- connection string moves in the same commit) and dormant under the migration superuser.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA callcentre TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA callcentre TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA callcentre GRANT SELECT, INSERT, UPDATE ON TABLES TO hbmp_app;

-- The three tenant-scoped aggregates. FORCE so the policy binds even for the table owner — without it a
-- migration/admin session silently sees everything and the isolation test would pass for the wrong reason.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['call_interaction','caller_verification','appointment_link']
    LOOP
        EXECUTE format('ALTER TABLE callcentre.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE callcentre.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON callcentre.%1$s', t);
        -- Fail-CLOSED: an unset or empty app.tenant_id matches nothing. No `OR current_setting(...) IS NULL`
        -- escape (audit R2 S2) — a background/unbound connection must see zero rows, not all of them.
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON callcentre.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

-- callcentre.call_seq (per-year counter) and callcentre.processed_request (idempotency ledger) carry no
-- tenant_id and stay RLS-free by design: the sequence is a platform counter, and the ledger must be readable
-- on the replay path BEFORE the request's tenant is resolved. Neither holds PHI — the ledger stores a key,
-- an operation name and a status code. This mirrors policy.processed_event (18.A1).
