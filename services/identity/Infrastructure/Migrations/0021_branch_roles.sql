-- identity-service — 0021: the two branch-management roles and the four branch-scoped scopes.
-- Phase 25.1 · design 42 §1 · ADR-0029. ADDITIVE: no existing role, scope or grant is altered.
--
-- THE INVARIANT THIS FILE CREATES, and everything after it depends on:
--
--   branch_coordinator and clinics_manager hold ONE permission set and differ ONLY in reach.
--
-- The rejected alternative was two roles with two capability lists. It fails by drift: someone adds
-- "revoke specialty" to the coordinator, forgets the manager, and the person supervising six clinics can do
-- less than the person running one of them. Nobody notices, because the manager's remedy is to ask a
-- coordinator, and asking works — so the gap is only discovered once it has been true for months.
--
-- The grant below is therefore written as ONE scope list CROSS JOINed onto BOTH roles. Not two lists that a
-- test compares — one list that cannot disagree with itself. `BranchScopeSetEqualityTests` then asserts set
-- equality against the resolved DB state as well, because a later migration can still grant one of them
-- something extra, and that is the drift the test exists to catch.
--
-- REACH is NOT expressed here. It comes from admin.user_branch_assignment rows, per branch, per person:
-- a clinics manager simply holds all six. Reach is grant-derived, never role-derived (design 42 §7 rule 2).

-- ---- 1. The four branch-scoped scopes ---------------------------------------------------------------
--
-- Each one exists because the network-wide scope it replaces is far too wide for someone who runs a clinic.
-- `provider:write` would let a coordinator create branches and edit external labs, pharmacies and tariffs,
-- and it is also the scope that unmasks license_no (provider/Api/Practitioners.cs). NEITHER branch role is
-- granted it, here or anywhere.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('branch:practitioner:write','branch',false),
    ('branch:roster:write','branch',false),
    ('branch:inventory:read','branch',false),
    ('branch:inventory:write','branch',false)
ON CONFLICT (name) DO NOTHING;

-- ---- 2. The two roles -------------------------------------------------------------------------------
--
-- T2, matching policy_admin / beneficiary_mgmt_supervisor: they administer staff licence data and clinic
-- stock across a branch — a real step above the front desk — but hold no clinical read beyond reception's,
-- and never a diagnosis. (T3/T4 grants are recertified quarterly; these are not that tier.)

INSERT INTO identity.role (id, name, normalized_name, concurrency_stamp, sensitivity_tier)
SELECT gen_random_uuid(), r.name, upper(r.name), gen_random_uuid()::text, r.tier
FROM (VALUES
    ('branch_coordinator','T2'), ('clinics_manager','T2')
) AS r(name, tier)
ON CONFLICT (normalized_name) DO NOTHING;

-- ---- 3. The permission set — ONE list, both roles ---------------------------------------------------
--
-- Reception's exact twelve, plus the four above. Sixteen.
--
-- DELIBERATELY ABSENT:
--   emr:read       — they run the clinic; they do not read clinical notes.
--   provider:write — network-wide. See the header, and BranchRoleScopeTests.
--   appointment:reserve — appointment:write already covers reservation AND the arrival decisions
--                         (check-in, no-show), which someone physically at the branch must be able to make.

INSERT INTO identity.role_scope (role_name, scope_name)
SELECT r.role, s.scope
FROM (VALUES
    ('branch_coordinator'), ('clinics_manager')
) AS r(role)
CROSS JOIN (VALUES
    -- reception's twelve, verbatim (identity 0001, 0004, 0005, 0008, 0009_profile, 0018)
    ('reception:search'), ('reception:read'), ('eligibility:check'),
    ('appointment:read'), ('appointment:write'),
    ('patient:read'), ('practitioner:read'), ('note:read'), ('profile:read'),
    ('callcentre:history:read'), ('notification:read'), ('claims:reimburse:submit'),
    -- and the four branch authorities
    ('branch:practitioner:write'), ('branch:roster:write'),
    ('branch:inventory:read'), ('branch:inventory:write')
) AS s(scope)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name).
ON CONFLICT DO NOTHING;

-- ---- 4. Fan out to every provisioned tenant ---------------------------------------------------------
--
-- After 0012 each tenant owns its own grant set and does NOT inherit the platform default live, so a
-- platform-default row alone would leave these two roles scopeless in every real tenant — seeded, assignable,
-- and silently powerless. Same shape as 0019/0020, same single scope list, so the two cannot diverge.

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, s.scope
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES
    ('branch_coordinator'), ('clinics_manager')
) AS r(role)
CROSS JOIN (VALUES
    ('reception:search'), ('reception:read'), ('eligibility:check'),
    ('appointment:read'), ('appointment:write'),
    ('patient:read'), ('practitioner:read'), ('note:read'), ('profile:read'),
    ('callcentre:history:read'), ('notification:read'), ('claims:reimburse:submit'),
    ('branch:practitioner:write'), ('branch:roster:write'),
    ('branch:inventory:read'), ('branch:inventory:write')
) AS s(scope)
ON CONFLICT DO NOTHING;
