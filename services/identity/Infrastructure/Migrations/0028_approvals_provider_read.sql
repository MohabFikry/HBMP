-- identity-service — 0028 let the approvals roles READ the network-tier catalogue. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- "Is pre-authorization required for THIS care, at THIS provider, on THIS date?" cannot be answered without
-- resolving the provider's network tier, because the requirement is a term of the plan AT A TIER. That
-- resolution is `GET /api/v1/network-tiers/resolve` on provider-service, gated on `provider:read`, and
-- approvals-service forwards the CALLER's token rather than using a service account (there isn't one — the
-- platform forbids them, so every read is attributable to the person who asked).
--
-- Neither approvals role held any provider scope, so the resolver answered 403 to every request. Until
-- ADR-0035 §5.2 wired the trigger rules into it, nothing noticed: the endpoint had never been registered, so
-- the gap had no symptom. It has one now.
--
-- This is exactly the shape 0017 settled for beneficiary management, and the same split design 38 draws for
-- the shared tier screen: `provider:read` is READ of the directory and its tiers, carrying no ability to move
-- a provider between tiers or to create or retire one. That is `provider:admin`, which stays with the network
-- team. The approval team reads the vocabulary in order to answer a benefit question against it.
--
-- BOTH roles, not just the supervisor. `auth:read` — which is what the pre-auth endpoint is gated on — is held
-- by medical_approval and medical_director alike, and a reviewer who could ask the question but never get an
-- answer is a 403 that looks like a permissions bug and is one.
--
-- PER TENANT. `role_scope` is tenant-scoped and the platform-default row (tenant_id = '') does NOT stand in
-- for a tenant that has its own grants — 0027 shipped the platform-only form first and produced a token that
-- authenticated fine and 403'd on the first request. Same shape as 0026.

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role, 'provider:read'
FROM (VALUES ('medical_approval'), ('medical_director')) AS r(role)
CROSS JOIN (SELECT DISTINCT tenant_id FROM identity.role_scope) rs
WHERE EXISTS (SELECT 1 FROM identity.role WHERE name = r.role)
ON CONFLICT DO NOTHING;
