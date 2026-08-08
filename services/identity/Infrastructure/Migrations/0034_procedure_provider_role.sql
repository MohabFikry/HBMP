-- identity-service — 0034 the external delivering provider: role `procedure_provider` + its two scopes.
--
-- ============================================================================================================
-- WHY A NEW ROLE RATHER THAN REUSING lab_tech
-- ============================================================================================================
-- 29.2b / design 45 §2b. A physiotherapy centre, a dialysis unit or an outside specialist clinic is NOT a
-- Mersal bench. Three differences make a shared role wrong rather than merely untidy:
--   * REACH — a lab_tech is provider-scoped AND branch-scoped, because Mersal's benches sit inside Mersal's
--     six clinics. An external centre is not a Mersal branch, so branch scope does not apply to it and
--     granting a role that carries one would silently narrow or widen its queue depending on a branch id it
--     does not have.
--   * PROJECTION — libs/authz gives lab_tech the Result field class. An external centre sees no results at
--     all; its projection is the narrowest on the platform (ProcedureQueueItem).
--   * REVOCATION — an organisation outside Mersal can have its access withdrawn as a unit, under a contract.
--     That is a different lifecycle from a staff account, and it needs its own name to be actionable.
--
-- ============================================================================================================
-- WHY procedure:consume IS SEPARATE FROM orders:consume
-- ============================================================================================================
-- `orders:consume` is held by the internal benches and covers Lab and Radiology work. Granting it to an
-- external centre would make the ONLY thing standing between that centre and the whole investigation queue
-- the ProviderCapability role→type map — which is a domain rule in one service, not a token boundary.
-- A distinct scope means an external provider's token cannot even ASK for lab work.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('procedure:read',    'orders', false),
    ('procedure:consume', 'orders', false)
ON CONFLICT (name) DO NOTHING;

-- T3: the centre sees a beneficiary's identity and the service ordered for them. That is clinical data, and
-- it recertifies quarterly like every other T3 grant.
INSERT INTO identity.role (id, name, normalized_name, concurrency_stamp, sensitivity_tier)
VALUES (gen_random_uuid(), 'procedure_provider', 'PROCEDURE_PROVIDER', gen_random_uuid()::text, 'T3')
ON CONFLICT (normalized_name) DO NOTHING;

-- Per tenant — see 0027 for what the platform-default row alone leaves broken.
--
-- patient:read is granted because the centre MUST verify the person at the counter (design 45 §2b "Identity
-- at the counter"). It reaches the beneficiary directory through the phase-26 card-number path, which
-- requires a SECOND identifier and audits every retrieval — a card is shared and photographed, so it is not
-- an authenticator. NOT emr:read, NOT orders:read: the directory, and nothing behind it.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, 'procedure_provider', s.scope
FROM (VALUES ('procedure:read'), ('procedure:consume'), ('patient:read'), ('notification:read')) AS s(scope)
CROSS JOIN (SELECT DISTINCT tenant_id FROM identity.role_scope) rs
ON CONFLICT DO NOTHING;
