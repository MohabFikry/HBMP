-- identity-service — 0006 the PAS policy-scope split (phase 19.1). Additive + idempotent.
--
-- Until now `policy` had exactly one scope, `policy:write`, and it meant everything: it was the scope on the
-- endpoint that creates a policy and the one that creates a coverage. Phase 19 splits what that single word
-- was covering, because two very different authorities were hiding inside it:
--
--   policy:admin      authoring the benefit PRODUCT — payers, plans, and the effective-dated benefit
--                     configuration a plan version carries. Activating a version decides what thousands of
--                     members are entitled to, retroactively resolvable forever. Policy Administrator.
--   policy:write      administering an individual MEMBER against an already-authored plan — enrol, terminate,
--                     reinstate, move between groups. Beneficiary Management. Unchanged in meaning.
--   policy:supervise  the supervisory increment over member administration: cancelling ANOTHER user's note
--                     (design 38 §5.5) and approving a retro-effective enrollment change.
--   policy:read       reading the benefit configuration. Deliberately broad — the rules are the vocabulary the
--                     whole platform adjudicates against — and safe to be broad, because a plan version carries
--                     no PHI whatsoever. Minimum-necessary bites at the MEMBER level (19.3/19.5), not here.
--
-- NOTE on beneficiary_mgmt: it held no policy scope at all. The role whose entire purpose is member
-- administration could not satisfy the policy rule that names it — the silent-403 class of gap 0004 exists to
-- close, found the same way (reading the rules and the grants side by side).

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('policy:read',      'policy', false),
    ('policy:admin',     'policy', false),
    ('policy:supervise', 'policy', false)
ON CONFLICT (name) DO NOTHING;

-- Reading the configuration: the roles that administer or adjudicate against a benefit today. Reception and
-- Call Centre reach benefit data through the member-level surfaces in 19.5 and are granted there, with the
-- field projection that surface requires — not here, where they would gain nothing they can use.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'policy:read' FROM (VALUES
    ('beneficiary_mgmt'), ('medical_approval'), ('finance'), ('claims_officer'),
    ('org_admin'), ('super_admin')
) AS r(role)
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- Member administration — the capability beneficiary_mgmt was always supposed to have.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('beneficiary_mgmt', 'policy:write')
ON CONFLICT (role_name, scope_name) DO NOTHING;

-- Authoring the product + the supervisory increment. The dedicated `policy_admin` and
-- `beneficiary_mgmt_supervisor` roles land in 19.7 (adding a role changes the frozen role vocabulary, the
-- admin RoleCatalog and the SPA's role→portal map, so it is one reviewed change, not a side effect of this
-- one). Until then the authority rests with the administrator roles that already exist.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('org_admin',   'policy:admin'),
    ('org_admin',   'policy:supervise'),
    ('super_admin', 'policy:admin'),
    ('super_admin', 'policy:supervise')
ON CONFLICT (role_name, scope_name) DO NOTHING;
