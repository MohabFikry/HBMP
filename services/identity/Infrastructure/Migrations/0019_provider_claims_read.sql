-- identity-service — 0019 the provider portal's claims READ. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 11-permission-matrix §3.4 gives the Provider Admin row `claim R🟠PO`, `claim_line R🟠PO`,
-- `claim_batch R🔒🟠PO (own batches only)` and `claim_document C🟠PO R🟠PO (own submissions)` — a provider
-- sees its own claims and nothing else. The platform said so in four places and granted it in none:
--
--   • ClaimsEndpoints' summary: "Provider users are isolated to their own claims".
--   • The comment on the `claims:read` policy rule: "Provider users may read only their own claims".
--   • The isolation code in the handlers, which forces a provider caller onto its own provider id — and in
--     SettlementService, an entire ProviderDenied outcome written for a caller that could never arrive.
--   • …and this seed, from the other side: 0005 granted provider_admin `claims:submit` and `claims:appeal`
--     and never `claims:read`.
--
-- So a provider could SUBMIT a claim and APPEAL its decision, and could not look at either. Every layer was
-- built for the read; the grant was missing, and a missing grant is a silent 403 that reads as a deliberate
-- restriction (the failure mode 0004/18.B3 exists to catch).
--
-- `claims:read` is the whole authority here — no new scope. What a provider receives under it is decided by
-- the `claims:read:own` policy rule, which requires ABAC provider-ownership against the ROW: its own claims,
-- its own submissions, its own settlement batches, and a denial on anything else. The reimbursement request
-- (the member's own receipts, ❌ for every provider-side role in §3.4) stays on the tenant-wide rule and is
-- refused for a provider holding this scope.
--
-- Only provider_admin. lab_tech / imaging_tech / pharmacist read their own worklists in orders and pharmacy
-- and have no business in the money.

INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'claims:read' FROM (VALUES
    ('provider_admin')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name).
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, 'claims:read'
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES
    ('provider_admin')
) AS r(role)
ON CONFLICT DO NOTHING;
