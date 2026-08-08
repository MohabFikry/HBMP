-- identity-service — 0022 the reference-catalogue scope. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- masterdata-service serves ICD-10, CPT, LOINC, ATC, drugs, indications, interactions, allergens and
-- examination types behind a bare `RequireAuthorization()` — any valid token, any holder, the whole
-- catalogue. Phase 26 adds a drug typeahead over 22,653 products to that surface, which is a good moment to
-- stop leaving it unbounded.
--
-- This REVERSES a position recorded in MasterDataAuthzTests, so the reasoning belongs here rather than in a
-- commit message. That test argued a scope every role holds is a control in name only. As a statement about
-- restricting clinicians it is correct, and nothing here contradicts it: the grant below is deliberately
-- broad, because a diagnosis code means the same thing to a doctor, a pharmacist and a claims officer, and
-- withholding it would break their screens while protecting nothing.
--
-- What the scope buys is not restriction:
--   * reference-data reach becomes a LINE IN THE ROLE MATRIX — stated, reviewable, and withdrawable from a
--     role — instead of an unstated consequence of holding any token at all;
--   * a service, integration or partner token must now REQUEST the catalogue. Under a bare authorization
--     check it received it by default, and the set of codes a platform carries is a fingerprint of what it
--     treats;
--   * it gives phase 27's `approval_supervisor` a real thing to be granted, rather than inheriting the
--     catalogue silently.
--
-- There is deliberately NO masterdata:write. Master data changes through admin-service's governed,
-- effective-dated, audited path (8b.2), never through this service.

INSERT INTO identity.scope (name, domain, description, service_only, deprecated, is_platform_admin_key)
VALUES ('masterdata:read', 'masterdata',
        'Read the reference catalogue — ICD-10, CPT, LOINC, ATC, drugs, drug indications, interactions, '
        'allergens and examination types. Public medical reference data, held by nearly every clinical '
        'role; it carries no PHI and no tenant-specific content.',
        false, false, false)
ON CONFLICT (name) DO NOTHING;

-- Granted to EVERY role that already holds any scope, rather than an enumerated list. A role omitted here is
-- a clinical screen that stops resolving codes, and the failure would surface as a blank dropdown rather than
-- an authorization error — so the safe construction is "everyone who has a token today keeps what they had".
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT DISTINCT role_name, 'masterdata:read'
FROM identity.role_scope
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, rs.role_name, 'masterdata:read'
FROM identity.role_scope rs
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;
