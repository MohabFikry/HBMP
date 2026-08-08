-- identity-service — 0016 grant `document:write` to the roles that file a member's paperwork.
-- Additive + idempotent.
--
-- ============================================================================================================
-- WHAT WAS BROKEN
-- ============================================================================================================
-- `document:write` has existed in the scope contract since the document service was built, and was granted to
-- NO ROLE AT ALL. Every path that stores a file therefore answered 403 for every user:
--
--   * attaching a document to a member (policy-service → document-service),
--   * the identification photograph,
--   * and every BULK upload, because the engine stores the file behind the same fail-closed scan before it
--     parses a single row.
--
-- The bulk failure was the least legible of the three: the engine cannot tell "document-service refused me"
-- from "document-service is down", so it reported the 403 as `SCAN_UNAVAILABLE — could not be reached`, and
-- the true cause was a missing grant rather than an outage. (That error mapping is worth narrowing separately;
-- this file removes the cause rather than improving the symptom.)
--
-- ============================================================================================================
-- WHY THESE TWO ROLES
-- ============================================================================================================
-- Beneficiary management files the registration paperwork — the card copy, the case documents, the consent and
-- the identification photograph — so it is the role the gap actually blocks. The supervisor gets it for the
-- same reason: they work the same records.
--
-- The scope only opens the ENDPOINT. What may actually be filed is still decided per document CLASS by
-- `DocumentAccess.MayUpload`, which is where the real rule lives: a finance user holding this scope still
-- cannot file a past medical history, and only reception and beneficiary management may store a photograph.
-- So this is not a widening of what anybody may do — it is the grant that lets them do what the class rules
-- already say they may.
--
-- DELIBERATELY NOT GRANTED HERE: reception and the clinical roles. `MayUpload` permits several of them, and
-- they very likely need this too — but which of them should be able to store files is a decision for whoever
-- owns the access model, not a side effect of building the registration form. Left for that review.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('document:write', 'document', false)
ON CONFLICT (name) DO NOTHING;

-- The platform default bucket ('').
INSERT INTO identity.role_scope (role_name, scope_name)
SELECT role, 'document:write' FROM (VALUES
    ('beneficiary_mgmt'), ('beneficiary_mgmt_supervisor')
) AS r(role)
-- Bare conflict target: 0011 widened role_scope's PK to (tenant_id, role_name, scope_name), and an
-- ON CONFLICT target is resolved against the constraints that exist when the statement RUNS.
ON CONFLICT DO NOTHING;

-- Every tenant already provisioned by 0012 owns its OWN copy of the grants and does not inherit live, so a
-- new default row alone would reach nobody who is actually signed in. Fan it out, same shape as 0012.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, r.role, 'document:write'
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) AS t
CROSS JOIN (VALUES ('beneficiary_mgmt'), ('beneficiary_mgmt_supervisor')) AS r(role)
ON CONFLICT DO NOTHING;
