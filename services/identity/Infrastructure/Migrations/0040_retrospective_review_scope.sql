-- identity-service — 0040: `auth:retrospective`, the scope that closes a break-glass review.
--
-- Emergency approval, director override and manual authorization all mark the case for post-hoc review. Until
-- approvals/0016 there was nothing to grant, because nothing could complete one — no endpoint anywhere set
-- `retrospective_reviewed`. The queue only ever grew.
--
-- WHO GETS IT, AND WHO POINTEDLY DOES NOT.
--
-- `medical_approval` holds `auth:manual` and `auth:emergency`: they RAISE break-glass authorizations. Granting
-- them the review as well would make one team both the actor and the auditor as a class — colleagues signing
-- off each other's overrides, which is the arrangement the control exists to replace rather than to formalise.
-- The per-person check in the handler (a reviewer may not be the actor) does not cover that; it stops somebody
-- reviewing their own, not a team reviewing its own.
--
-- So: Medical Director and Super Admin. A director's own override is therefore reviewable only by another
-- director or by Super Admin, which is correct — SoD binds the person, and the pool being small is a staffing
-- fact, not a reason to widen the grant.
-- Bare conflict targets throughout: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS. Naming a column
-- pair here would make this file un-re-runnable the moment a later migration moves a key again — the failure
-- mode `apply-migrations.sh` turns into "every service after `identity/` silently stops being migrated".
INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('auth:retrospective','auth',false)
ON CONFLICT DO NOTHING;

-- The rows land in the tenant_id = '' bucket via the column default, which is where every other built-in
-- role grant in this catalogue lives: a built-in role's scopes are platform-wide, not per-tenant.
INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('medical_director','auth:retrospective'),
    ('super_admin','auth:retrospective')
ON CONFLICT DO NOTHING;
