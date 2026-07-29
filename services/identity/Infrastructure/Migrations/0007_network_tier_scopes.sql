-- identity-service — 0007 the network-administration scope (phase 19.1b). Additive + idempotent.
--
-- `provider:write` covered every Network Team write: add a location, record a credential, sign a contract.
-- Phase 19.1b introduces one that is not like the others. Moving a provider between network tiers reprices
-- every plan that references those tiers, for every member enrolled on them, from the assignment's effective
-- date — a commercial act with platform-wide financial reach, sitting in the same scope as editing an address.
--
--   provider:admin   create/retire a network tier, assign or revoke a provider/location/service-line tier.
--   provider:write   unchanged — ordinary provider and contract metadata.
--
-- WHO DOES NOT GET IT, and why that is the point: `policy_admin` (and org_admin acting as one) configures
-- cost-share PER TIER in policy.benefit_rule_tier. That is benefit design. Deciding WHICH tier a hospital sits
-- in is network commercial policy and belongs to the Network Team. Collapsing the two would let one person
-- both set the out-of-network penalty and decide who is out of network. Asserted by an authz test
-- (NetworkTierAuthzTests), not just documented here.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('provider:admin', 'provider', false)
ON CONFLICT (name) DO NOTHING;

INSERT INTO identity.role_scope (role_name, scope_name) VALUES
    ('network_team', 'provider:admin'),
    ('org_admin',    'provider:admin'),
    ('super_admin',  'provider:admin')
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS — so naming
-- the old pair made this file un-re-runnable from 0011 onwards. See 0001 for the full note.
ON CONFLICT DO NOTHING;
