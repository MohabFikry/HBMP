-- identity-service — 0027 the approvals-engine authoring scope. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- ADR-0035 §5 gives the approval supervisor an engine: effective-dated rules that decide which queue a
-- request lands on and how long the reviewer has. Authoring those rules needs its own scope, and it is
-- deliberately NOT `auth:decide`.
--
-- Deciding one case and authoring the rule that shapes a thousand are different powers. A reviewer who could
-- edit the rule routing their own work could route it away from themselves, and the change would look like
-- ordinary configuration rather than like avoiding a decision. So `medical_approval` — the role that works
-- the queue — is granted `auth:decide` and NOT this.
--
-- Granted to `medical_director` (who supervises the queue and absorbs the consequence of a bad rule: too
-- broad a pre-auth trigger floods their own desk) and `super_admin`.
--
-- The engine's first families are ROUTING and SLA on purpose — they change who decides and by when, never
-- what is decided. Nothing this scope grants can approve or refuse anything. Auto-approval arrives later,
-- behind its own kill switch; auto-REJECT is not built at all, because a wrong auto-approval costs the payer
-- money and a wrong auto-rejection denies care to a refugee with nobody having looked.

INSERT INTO identity.scope (name, domain, description, service_only, deprecated, is_platform_admin_key)
VALUES ('auth:configure', 'approvals',
        'Author the approvals engine''s routing and SLA rules — which queue a request lands on and how long '
        'the reviewer has. Separate from auth:decide: authoring the rule that shapes a thousand cases is a '
        'different power from deciding one, and a reviewer who could edit their own routing could route work '
        'away from themselves.',
        false, false, false)
ON CONFLICT (name) DO NOTHING;

-- An enumerated grant, unlike 0022's deliberately broad one. This scope CHANGES how work is distributed, so
-- "everyone who has a token today keeps what they had" would be exactly the wrong construction.
--
-- PER TENANT, not once. `role_scope` is tenant-scoped and the platform-default row (tenant_id = '') does NOT
-- stand in for a tenant that has its own grants — the first version of this inserted only the default, and
-- the demo tenant's medical_director got a token that authenticated fine and 403'd on the first request.
-- Nothing was broken: the scope existed, the client could request it, and the token was quietly short. Same
-- shape as 0026, which learned this first.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role, 'auth:configure'
FROM (VALUES ('medical_director'), ('super_admin')) AS r(role)
CROSS JOIN (SELECT DISTINCT tenant_id FROM identity.role_scope) rs
WHERE EXISTS (SELECT 1 FROM identity.role WHERE name = r.role)
ON CONFLICT DO NOTHING;
