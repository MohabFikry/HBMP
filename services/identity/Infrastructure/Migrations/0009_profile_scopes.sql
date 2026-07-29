-- identity-service — 0009 the patient-profile scopes (phase 20, design 39). Additive + idempotent.
--
-- Three scopes, and the split between them is the design:
--
--   profile:read           open a patient profile. Held by EVERY role the design-39 §4 matrix names — because
--                          the scope is the COARSE gate, and what each of them actually receives is decided
--                          per section by ProfilePolicies. A finance officer and a treating doctor hold the
--                          same scope and get profiles with almost nothing in common.
--
--   profile:export         generate the role-projected print summary. NARROWER on purpose: copying a patient
--                          record out of the platform is a different act from looking at it, and the roles
--                          that need a printable handover are not the same set as the roles that need to see
--                          a screen. Finance, claims, labs, pharmacies and platform admins do not hold it.
--
--   callcentre:history:read  read a member's call history. Separate from `callcentre:read` — that scope is the
--                          agent's own workspace (search, the member 360, the verification gate). This one is
--                          held by roles that are not in the call centre at all and would have no business
--                          holding the workspace scope.
--
-- Finance and claims DO hold callcentre:history:read, and resolve to the Meta level server-side: enough to see
-- that a billing call happened, never the narrative (design 39 §5b). The scope grants access to the endpoint;
-- the level decides what comes back. Granting the scope is not granting the summary.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('profile:read',            'profile',    false),
    ('profile:export',          'profile',    false),
    ('callcentre:history:read', 'callcentre', false)
ON CONFLICT (name) DO NOTHING;

-- profile:read — every role named in the design-39 §4 matrix.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('reception',                   'profile:read'),
    ('call_center',                 'profile:read'),
    ('call_center_supervisor',      'profile:read'),
    ('doctor',                      'profile:read'),
    ('nurse',                       'profile:read'),
    ('lab_tech',                    'profile:read'),
    ('imaging_tech',                'profile:read'),
    ('pharmacist',                  'profile:read'),
    ('pharmacy_supervisor',         'profile:read'),
    ('medical_approval',            'profile:read'),
    ('medical_director',            'profile:read'),
    ('case_manager',                'profile:read'),
    ('finance',                     'profile:read'),
    ('claims_officer',              'profile:read'),
    ('beneficiary_mgmt',            'profile:read'),
    ('beneficiary_mgmt_supervisor', 'profile:read'),
    ('org_admin',                   'profile:read'),
    ('super_admin',                 'profile:read')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;

-- profile:export — the roles that need a printable, watermarked handover of a record they can already see.
-- Deliberately NOT reception (the front desk hands over a card, not a clinical summary), not finance or claims
-- (they export through the phase-19.5b extract engine, which has its own controls), and not labs, pharmacies
-- or platform admins.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('doctor',                      'profile:export'),
    ('nurse',                       'profile:export'),
    ('medical_approval',            'profile:export'),
    ('medical_director',            'profile:export'),
    ('case_manager',                'profile:export'),
    ('beneficiary_mgmt',            'profile:export'),
    ('beneficiary_mgmt_supervisor', 'profile:export'),
    ('super_admin',                 'profile:export')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;

-- callcentre:history:read — the roles whose profile carries a callHistory section at any level.
-- Labs, pharmacies and platform admins are absent: their matrix rows have no callHistory cell at all, so the
-- scope would grant them an endpoint that returns nothing. A scope nobody can use is a scope somebody will
-- eventually wire something else to.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('reception',                   'callcentre:history:read'),
    ('call_center',                 'callcentre:history:read'),
    ('call_center_supervisor',      'callcentre:history:read'),
    ('doctor',                      'callcentre:history:read'),
    ('nurse',                       'callcentre:history:read'),
    ('medical_approval',            'callcentre:history:read'),
    ('medical_director',            'callcentre:history:read'),
    ('case_manager',                'callcentre:history:read'),
    ('finance',                     'callcentre:history:read'),
    ('claims_officer',              'callcentre:history:read'),
    ('beneficiary_mgmt',            'callcentre:history:read'),
    ('beneficiary_mgmt_supervisor', 'callcentre:history:read')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;
