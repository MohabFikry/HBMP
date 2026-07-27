-- identity-service — 0005 reconcile the identity seed with the policy bundles (Phase 18.B3, audit R2 S3).
-- Additive + idempotent.
--
-- 0004 fixed three gaps found by reading a few rules next to the seed. Writing the test that FINDS such gaps
-- (libs/authz/Tests/ScopeIntegrityTests) turned up 141 rule/role pairs that no token could ever satisfy.
--
-- The cause is structural rather than careless. Authorization is two artefacts that must agree: PolicyRule in
-- libs/authz names a role AND a scope, and the engine denies unless the principal holds both. The rules were
-- written per phase, as code, under review. The grants live in one 200-line seed written in Phase 17, and no
-- phase since has had a reason to revisit it. Nothing detects the drift, because a mismatch produces a DENY —
-- the safe outcome. Nothing crashes; the feature is simply unreachable, and only in a real deployment with a
-- real token, never in a test that constructs its own principal.
--
-- This file closes every gap that has an unambiguous answer. Two categories are deliberately NOT closed here
-- and are recorded as declared exceptions in the test instead:
--   • six roles named by rules that are absent from the frozen role vocabulary (claims_reviewer, manager,
--     network_manager, approvals_team, finance_approver, call_center_supervisor). Adding a role changes the
--     token contract AND the SPA's portal mapping — a product decision, not a seed fix.
--   • the interop `fhir:*` SMART scopes, which are granted per CLIENT to external partners, never to an
--     internal staff role, so their absence from role_scope is correct.

-- ---------------------------------------------------------------- new scopes the rules already require
INSERT INTO identity.scope (name, domain, service_only) VALUES
    -- DefaultPolicies: the eligibility card at the front desk — distinct from reception:search, which finds
    -- the person. Reading their coverage card is the second, separately-audited step.
    ('reception:read',           'reception', false),
    -- PharmacyPolicies: a prescriber reading back a prescription they wrote. Separate from rx:write so a
    -- read-back does not require the authority to issue.
    ('rx:read',                  'rx',        false),
    -- ReportingPolicies: the financial zone. reporting:read is the operational zone; conflating them would
    -- give every operational report reader the cost aggregates.
    ('reporting:read-financial', 'reporting', false),
    -- ClaimsPolicies (10b): the money layer. claims:read is the worklist; each of these is a distinct
    -- AUTHORITY over a claim, which is the entire point of the SoD design — the officer who decides a line is
    -- not the person who releases the settlement, and one scope covering both would erase that.
    ('claims:review',            'claims',    false),
    ('claims:decide',            'claims',    false),
    ('claims:adjudicate',        'claims',    false),
    ('claims:adjust',            'claims',    false),
    ('claims:batch',             'claims',    false),
    ('claims:submit',            'claims',    false),
    ('claims:reimburse:submit',  'claims',    false),
    ('claims:appeal',            'claims',    false),
    ('claims:settle',            'claims',    false),
    ('claims:ingest',            'claims',    true)   -- service-only: the auto-derive event seam, never a human
ON CONFLICT (name) DO NOTHING;

-- ---------------------------------------------------------------- grants: the claims money layer
-- Phase 10b built claim decisions, batching, adjustment and settlement, and 18.A2 fixed the arithmetic. None
-- of it was reachable: claims_officer held claims:read, claims:reconcile and claims:export only, so every
-- decide/adjudicate/adjust/batch call denied with missing-scope.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, scope FROM (VALUES
    ('claims_officer', 'claims:review'),
    ('claims_officer', 'claims:decide'),
    ('claims_officer', 'claims:adjudicate'),
    ('claims_officer', 'claims:adjust'),
    ('claims_officer', 'claims:batch'),
    ('claims_officer', 'claims:submit'),
    ('claims_officer', 'claims:reimburse:submit'),
    ('claims_officer', 'claims:appeal'),
    ('provider_admin', 'claims:submit'),
    ('provider_admin', 'claims:appeal'),
    ('reception',      'claims:reimburse:submit'),
    ('case_manager',   'claims:reimburse:submit'),
    ('case_manager',   'claims:appeal'),
    -- Settlement release is finance's, split from the officer's decide by design (10b.8 SoD).
    ('finance',        'claims:settle'),
    ('finance',        'claims:read'),
    ('finance',        'claims:export')
) AS g(role, scope)
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- ---------------------------------------------------------------- grants: front desk + prescriber + reporting
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, scope FROM (VALUES
    ('reception',        'reception:read'),
    ('beneficiary_mgmt', 'reception:read'),
    ('doctor',           'rx:read'),
    ('finance',          'reporting:read-financial'),
    ('finance',          'reporting:export'),
    ('medical_director', 'reporting:read-financial')
) AS g(role, scope)
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- ---------------------------------------------------------------- grants: clinical oversight
-- medical_director is the largest single gap: the role matrix (§3.10) puts clinical governance in charge of
-- escalations, appeals, overrides and oversight dashboards, and the seed gave it five reporting/notification
-- scopes. Every oversight rule that names it denied. Note what it is still NOT given: patient:write,
-- claims:decide, admin:grant-role — governance reviews decisions, it does not originate them.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT 'medical_director', scope FROM (VALUES
    ('emr:read'), ('orders:read'),
    ('auth:review'), ('auth:decide'), ('auth:override'), ('auth:manual'), ('auth:emergency'),
    ('case:read'), ('case:write'), ('case:manage'),
    ('finance:read'), ('finance:approve'), ('finance:export')
) AS s(scope)
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- medical_approval reviews the clinical record behind an authorization; without emr:read it was deciding
-- blind, which is the opposite of what the review step exists for.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('medical_approval', 'emr:read'),
    ('medical_approval', 'reporting:read'),
    -- A nurse documents vitals and nursing notes on an encounter (EmrPolicies names the role on emr:write).
    ('nurse',            'emr:write'),
    -- Lab/imaging/pharmacy staff resolve the provider and location on a fulfillment; provider:read is
    -- metadata (name, location, capability), never beneficiary data.
    ('lab_tech',         'provider:read'),
    ('imaging_tech',     'provider:read'),
    ('pharmacist',       'provider:read')
ON CONFLICT (role_name, scope_name) DO NOTHING;
