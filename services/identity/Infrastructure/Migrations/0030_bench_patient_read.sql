-- identity-service — 0030 let the lab and imaging benches resolve a patient's identifiers. Additive.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 27.8 made the benches search-first, the way the dispensing counter already was: a technician asks "what do
-- I have for THIS patient" instead of scrolling every open order in the tenant. Searching by card number,
-- passport or member number resolves those identifiers through patient-service, and orders-service forwards
-- the CALLER's token — the platform has no service accounts, so every directory read is attributable.
--
-- `pharmacist` has held `patient:read` since the counter was built. The benches never did, because they never
-- asked. The result was a 503 on every identifier search: the honest fail-safe ("the directory could not be
-- reached, this is NOT a report that the patient has no orders"), which is the correct thing to say and not a
-- substitute for being able to ask.
--
-- `patient:read` is READ of the beneficiary directory. It carries no ability to register, amend or merge a
-- record — that is beneficiary management's. A technician resolving two identifiers to one person is doing
-- exactly what the pharmacist at the next counter already does.
--
-- PER TENANT — see 0027 for what the platform-default row alone leaves broken.

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role, 'patient:read'
FROM (VALUES ('lab_tech'), ('imaging_tech')) AS r(role)
CROSS JOIN (SELECT DISTINCT tenant_id FROM identity.role_scope) rs
WHERE EXISTS (SELECT 1 FROM identity.role WHERE name = r.role)
ON CONFLICT DO NOTHING;
