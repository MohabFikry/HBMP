-- provider-service — 0003 Row-Level Security: the datastore layer of provider isolation (2b.3, layer 4).
-- Every provider-schema row is filtered by two session GUCs the app binds per request:
--   app.tenant_id   — always set; enforces tenant separation (no cross-tenant read without break-glass).
--   app.provider_id — set for provider-scoped users; empty/unset ⇒ tenant-wide (the Network Team).
-- FORCE ROW LEVEL SECURITY is used so even the table owner (the app's role) is subject to the predicate —
-- a buggy application query still cannot return another provider's (or tenant's) rows. This is an
-- INDEPENDENT guarantee: it holds even if the ABAC layer (0-layer 3) is bypassed.
--
-- contract_service_line has no provider_id column (22 §5.3), so it is tenant-scoped here and
-- provider-scoped via its parent contract at the ABAC layer.

-- Tables carrying provider_id: tenant AND provider predicate.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['provider_location','provider_contract','provider_credential','provider_user']
    LOOP
        EXECUTE format('ALTER TABLE provider.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE provider.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON provider.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON provider.%1$s USING (
                tenant_id = current_setting('app.tenant_id', true)
                AND (
                    coalesce(current_setting('app.provider_id', true), '') = ''
                    OR provider_id::text = current_setting('app.provider_id', true)
                )
            )$p$, t);
    END LOOP;
END $$;

-- provider: its identity IS provider_id (the PK).
ALTER TABLE provider.provider ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.provider FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_provider ON provider.provider;
CREATE POLICY rls_provider ON provider.provider USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR provider_id::text = current_setting('app.provider_id', true)
    )
);

-- contract_service_line: tenant-scoped (provider isolation via parent contract + ABAC).
ALTER TABLE provider.contract_service_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.contract_service_line FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_contract_service_line ON provider.contract_service_line;
CREATE POLICY rls_contract_service_line ON provider.contract_service_line USING (
    tenant_id = current_setting('app.tenant_id', true)
);
