-- identity-service — 0029 let the approvals roles READ authored cost share. Additive + idempotent.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- The second half of the pre-authorization question. Resolving the provider's tier (0028) only gets you as far
-- as asking policy-service what the plan says AT that tier, and `GET /plan-versions/{id}/cost-share` is gated
-- on `policy:read` OR `eligibility:check` — the narrow pricing-lookup pair added in 26.x precisely so a
-- counter could ask "what does this cost" without being handed the whole policy book.
--
-- The approval team held neither, so the read 403'd and the pre-auth answer degraded to "we could not tell,
-- so authorization is required". That is the correct fail-closed behaviour and it is NOT a substitute for the
-- real answer: a permanently indeterminate gate requires authorization for everything, which is the outcome
-- ADR-0035 §5.2 refuses to let a rule produce and should not arrive by accident either.
--
-- `eligibility:check` rather than `policy:read`, deliberately. It is the narrower of the two: it buys the
-- cost-share lookup and the eligibility check, not the authored policy book, the plan editor or the version
-- history. An approver needs to know what a plan says about THIS care at THIS tier; they do not need to read
-- every plan the payer has authored.
--
-- PER TENANT — see 0027 and 0028 for why the platform-default row alone is not enough.

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role, 'eligibility:check'
FROM (VALUES ('medical_approval'), ('medical_director')) AS r(role)
CROSS JOIN (SELECT DISTINCT tenant_id FROM identity.role_scope) rs
WHERE EXISTS (SELECT 1 FROM identity.role WHERE name = r.role)
ON CONFLICT DO NOTHING;
