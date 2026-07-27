-- identity-service — 0004 close three gaps between what the policy bundles REQUIRE and what the seed GRANTS
-- (Phase 18.B3, audit R2 S3/S6). Additive + idempotent.
--
-- Authorization here is two independent tables that have to agree: the policy rule in libs/authz names a role
-- AND a scope, and the engine denies unless the principal holds both. The rules are code and were reviewed;
-- the grants are seed data and were not. Where they disagree the result is a silent, permanent 403 —
-- "missing-scope" in a log nobody reads, for a role the design says is entitled. Every one of these three was
-- found by reading the two side by side, not by anything failing.

-- ---------------------------------------------------------------- 1. patient:read (S6)
-- New scope. Reading the beneficiary directory used to require patient:write, so reception — whose whole job
-- is finding the member at the desk — could not read a beneficiary at all, while anyone who could look a
-- person up was equally entitled to rewrite their identity record.
INSERT INTO identity.scope (name, domain, service_only) VALUES ('patient:read', 'patient', false)
ON CONFLICT (name) DO NOTHING;

INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'patient:read' FROM (VALUES
    ('reception'), ('call_center'), ('beneficiary_mgmt'), ('case_manager'),
    ('doctor'), ('nurse'), ('medical_approval'), ('medical_director')
) AS r(role)
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- ---------------------------------------------------------------- 2. medical_director admin scopes
-- AdminPolicies names medical_director on EditMasterData (FR-MDM-008 puts clinical governance in charge of
-- the ICD/CPT/LOINC/drug catalogue), EditTemplate and ReadDashboard — but the seed never granted it admin:read
-- or admin:write, so all three denied. The role lists in those rules still narrow what this unlocks: the
-- grant/revoke/de-provision/configure rules are Roles = (org_admin, super_admin) and stay closed to it.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('medical_director', 'admin:read'),
    ('medical_director', 'admin:write')
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- ---------------------------------------------------------------- 3. admin:break-glass (the emergency path)
-- The BreakGlassRequest rule names seven originating roles — doctor, nurse, medical_approval,
-- medical_director, case_manager, org_admin, super_admin — and only super_admin held the scope. Break-glass
-- is the emergency route to PHI when the ordinary ABAC path denies: a clinician in front of an unconscious
-- patient. It was reachable by exactly one platform administrator, which is the one person not in the room.
-- Approval stays narrower (medical_director, org_admin, super_admin per the rule's own role list), so dual
-- control is unaffected: holding the scope lets you ASK, not decide.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'admin:break-glass' FROM (VALUES
    ('doctor'), ('nurse'), ('medical_approval'), ('medical_director'), ('case_manager'), ('org_admin')
) AS r(role)
ON CONFLICT (role_name, scope_name) DO NOTHING;
