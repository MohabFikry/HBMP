-- identity-service — 0018 the doctor picker scope. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- Booking an appointment means choosing a SPECIALTY and then a DOCTOR. Both come from provider-service
-- (14.5 / design 37 §4), whose reads are gated on `provider:read` — and reception deliberately holds no
-- provider scope at all. That refusal is correct and must stay: `provider:read` is the whole provider
-- DIRECTORY — contracts, onboarding state, network tiers, the commercial relationship with every clinic and
-- lab in the network. None of that is the front desk's business, and emr's own code says so where it explains
-- why `/api/v1/providers` is 403 for reception.
--
-- But the consequence was that the two fields the booking screen filters on could not be read by the people
-- doing the booking. So the choice was between granting reception the entire provider directory, or having
-- emr fetch it under a service account and hand it on — and the latter is forbidden platform-wide (see
-- `NoServiceAccountArchitectureTests`: a privileged aggregator that fetches everything and then filters is
-- the classic aggregation vulnerability).
--
-- Neither. This is the same split `patient:read` already records: an over-broad scope kept an operational
-- role from the narrow thing it legitimately needed, and the answer was a scope sized to the actual need.
--
-- `practitioner:read` is exactly the clinician PICKER — who works at this branch, in which specialty, under
-- what name. It carries no provider directory, no contracts, no tariffs, no tiers, and no licence numbers
-- (`GET /practitioners` omits `license_no` for any caller without `provider:write`, so the projection was
-- already min-necessary before this scope existed — it simply had no holder).

INSERT INTO identity.scope (name, domain, description, service_only, deprecated, is_platform_admin_key)
VALUES ('practitioner:read', 'provider',
        'Read the clinician picker — practitioners by branch and specialty, plus the specialty reference set. '
        'NOT the provider directory, contracts, tariffs or tiers, and never a licence number.',
        false, false, false)
ON CONFLICT (name) DO NOTHING;

-- The booking and clinical roles. Reception and the call centre BOOK; doctors and nurses read the same picker
-- to see who a visit is assigned to. Network/org admin reach it through `provider:read` already.
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'practitioner:read' FROM (VALUES
    ('reception'), ('call_center'), ('doctor'), ('nurse'), ('case_manager')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name).
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, 'practitioner:read'
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES
    ('reception'), ('call_center'), ('doctor'), ('nurse'), ('case_manager')
) AS r(role)
ON CONFLICT DO NOTHING;
