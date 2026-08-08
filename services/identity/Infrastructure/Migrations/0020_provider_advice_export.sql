-- identity-service — 0020 the payee's copy of its own settlement advice. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 11-permission-matrix §3.4 gives the Provider Admin row `settlement_advice R🔒🟠PO (own advice) E🔒🟠PO`.
-- A settlement advice is the document that tells a provider what it is owed for a period; the payee holding
-- a copy is the whole point of issuing one. `SettlementService.ExportAsync` was written for that caller —
-- it takes the caller's provider id, compares it to the batch's payee, and audits the mismatch as
-- EXPORT_CROSS_PROVIDER at High severity — and no provider could hold the scope to reach it. 0019's finding,
-- one surface along.
--
-- `claims:export` is the export authority the platform already has; §3.3 calls Export a distinct, elevated,
-- always-audited action, and this is exactly that act performed by the party the document is addressed to.
-- No new scope: what a provider may do with it is decided by the `claims:export:own` policy rule, which
-- requires ABAC provider-ownership against the batch's payee.
--
-- WHAT THIS DELIBERATELY DOES NOT GRANT
-- -------------------------------------
-- The same scope guards POST /claim-batches/{id}/settlement-advice — GENERATING an advice, which is the
-- release step: the last human control before money moves (18.A4, 36 §9), and Mersal's act, never the
-- payee's. That endpoint stays on the `claims:export` ACTION, whose role set names no provider, so a
-- provider token passes the coarse scope check and is refused by the policy rule. That is the intended
-- two-layer shape, and `A_provider_cannot_generate_a_settlement_advice_even_holding_the_export_scope`
-- fails the build if the rule is ever widened to match the scope.
--
-- Only provider_admin, as in 0019.

INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'claims:export' FROM (VALUES
    ('provider_admin')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name).
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, 'claims:export'
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES
    ('provider_admin')
) AS r(role)
ON CONFLICT DO NOTHING;
