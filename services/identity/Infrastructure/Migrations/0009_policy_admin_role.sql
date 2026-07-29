-- ============================================================================================================
-- Phase 19.7 — the `policy_admin` and `beneficiary_mgmt_supervisor` roles.
--
-- 19.1's own migration (0006) deferred these deliberately: "adding a role changes the frozen role vocabulary,
-- the admin RoleCatalog and the SPA's role→portal map, so it is one reviewed change, not a side effect".
-- This is that change. Until now the authority rested with org_admin/super_admin, which is a platform
-- administrator doing benefit administration — workable for a pilot and wrong as a standing arrangement,
-- because it means the person who can author a benefit is also the person who can grant themselves any role.
--
-- WHY TWO ROLES AND NOT ONE
--
--   policy_admin                 authors the PRODUCT: payers, plans, effective-dated plan versions, benefit
--                                rules and the policies written against them. Reads members (a plan is
--                                meaningless without seeing who is on it) but does not administer them.
--
--   beneficiary_mgmt_supervisor  the supervisory increment OVER member administration: cancelling another
--                                user's note (38 §5.5) and approving a retro-effective enrolment change.
--                                Deliberately not folded into beneficiary_mgmt — a supervisory power every
--                                officer holds is not a supervisory power, and the two acts it guards are
--                                precisely the ones a second pair of eyes exists for.
--
-- Both are T2. Neither may read clinical data: policy administration reads entitlement and money, and the
-- note projection withholds Clinical/Restricted bodies from both by class, not by role list.
-- ============================================================================================================

INSERT INTO identity.role (id, name, normalized_name, concurrency_stamp, sensitivity_tier)
SELECT gen_random_uuid(), r.name, upper(r.name), gen_random_uuid()::text, r.tier
FROM (VALUES
    ('policy_admin','T2'),
    ('beneficiary_mgmt_supervisor','T2')
) AS r(name, tier)
WHERE NOT EXISTS (SELECT 1 FROM identity.role x WHERE x.name = r.name);

-- policy_admin: authors the product, reads the membership book it applies to, and reads network tiers
-- WITHOUT provider:admin — it prices benefits AT a tier while the Network Team decides which tier a provider
-- sits in. Granting provider:admin here would have quietly made one role able to reprice the network by
-- moving a provider, which is the separation 19.1b exists to draw.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('policy_admin', 'policy:read'),
    ('policy_admin', 'policy:admin'),
    ('policy_admin', 'policy:write'),
    ('policy_admin', 'policy:supervise'),
    ('policy_admin', 'provider:read'),
    ('policy_admin', 'note:read'),
    ('policy_admin', 'note:write'),
    ('policy_admin', 'patient:read'),
    ('policy_admin', 'notification:read'),
    -- 19.6b: the analytical dashboard. reporting:read only — the FINANCIAL zone (cost per member, net
    -- payable, provider value) stays with finance, so a benefit author sees enrolment and utilization and
    -- must ask Finance for the money views. That is the same zone split phase 8.2 drew.
    ('policy_admin', 'reporting:read'),
    -- The dashboard's audited CSV export. Same action as every other report export, so it inherits the
    -- audit-event guarantee rather than inventing a second export path.
    ('policy_admin', 'reporting:export')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;

-- beneficiary_mgmt_supervisor: everything the officer role holds, plus the supervisory increment. Listed in
-- full rather than inherited: role inheritance is invisible at the point of audit, and "why could this person
-- cancel that note" must be answerable from one row.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('beneficiary_mgmt_supervisor', 'policy:read'),
    ('beneficiary_mgmt_supervisor', 'policy:write'),
    ('beneficiary_mgmt_supervisor', 'policy:supervise'),
    ('beneficiary_mgmt_supervisor', 'patient:read'),
    ('beneficiary_mgmt_supervisor', 'patient:write'),
    ('beneficiary_mgmt_supervisor', 'eligibility:check'),
    ('beneficiary_mgmt_supervisor', 'note:read'),
    ('beneficiary_mgmt_supervisor', 'note:write'),
    ('beneficiary_mgmt_supervisor', 'notification:read'),
    ('beneficiary_mgmt_supervisor', 'reception:read'),
    ('beneficiary_mgmt_supervisor', 'reporting:read')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;

-- 19.6b — the dashboard's OPERATIONAL views for the roles that already work with this data.
--
-- beneficiary_mgmt administers the membership book; being unable to see the shape of it was an omission, not
-- a boundary. finance held `reporting:read-financial` and NOT `reporting:read`, which would have let them open
-- the analytics section and get a 403 on four of its six views — they have read utilization since phase 10.3,
-- so enrolment and outlier counts are not a new disclosure to them.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('beneficiary_mgmt', 'reporting:read'),
    ('finance', 'reporting:read')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;
