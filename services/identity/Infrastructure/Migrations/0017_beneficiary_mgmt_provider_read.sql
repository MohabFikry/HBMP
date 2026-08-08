-- identity-service — 0017 let beneficiary management READ the network-tier catalogue. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- Registering a member requires choosing the network tier they sit on — it is a mandatory field on the form
-- and a column in the intake file. Both resolve against provider-service's tier catalogue, which is gated on
-- `provider:read`, and beneficiary management held no provider scope at all. The result:
--
--   * the registration form's Network Tier droplist came back empty, and
--   * every bulk intake row failed while trying to resolve its tier.
--
-- `provider:read` is READ of the provider directory and its tiers. It carries no ability to move a provider
-- between tiers or to create or retire one — that is `provider:admin`, which stays with the network team.
-- This is the same split design 38 draws for the shared tier screen: the network team decides WHICH tier a
-- provider sits in, everyone else only reads the vocabulary in order to price or elect against it.

INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'provider:read' FROM (VALUES
    ('beneficiary_mgmt'), ('beneficiary_mgmt_supervisor')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name).
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, 'provider:read'
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES ('beneficiary_mgmt'), ('beneficiary_mgmt_supervisor')) AS r(role)
ON CONFLICT DO NOTHING;
