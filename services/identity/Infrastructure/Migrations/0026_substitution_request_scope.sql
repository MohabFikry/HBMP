-- identity-service — 0026 a lab / imaging technician may ASK whether another examination can stand in for
-- the one that was ordered. Additive + idempotent.
--
-- WHY THIS IS A REQUEST AND NOT A CHOICE (ADR-0034 Decision 4).
--
-- The pharmacy counter has a real equivalence set: a drug's ATC-5 class is a clinically-sound
-- generic-substitution set held in master data, so a pharmacist picks between equivalents and the server
-- refuses anything outside the list. Examinations have no such set anywhere — `examination_type` carries a
-- category and a sensitivity and nothing that says "this test may stand in for that one". Deriving one from
-- the category would put "any radiology procedure" behind a button, which is a technician prescribing.
--
-- So the technician RAISES THE QUESTION, and someone qualified answers it. `auth:request-substitution`
-- authorizes exactly one endpoint — POST /authorizations/substitution-requests — whose body names an order
-- line, a reason, and optionally a proposed code. It carries no decision authority: the request lands
-- Submitted in the approval team's normal queue with the normal SLA clock, and `auth:decide` is held by
-- medical_approval and medical_director and is not granted here.
--
-- Pharmacists are NOT granted it. They already have a formulary path that resolves the same question at the
-- counter in seconds, and the pharmacy service already routes an off-formulary request to approvals on its
-- own. A second way to ask would be a second answer to keep in step with the first.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('auth:request-substitution', 'auth', false)
ON CONFLICT DO NOTHING;

INSERT INTO identity.role_scope (role_name, scope_name)
VALUES ('lab_tech', 'auth:request-substitution'),
       ('imaging_tech', 'auth:request-substitution')
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role_name, 'auth:request-substitution'
FROM identity.role_scope rs
CROSS JOIN (VALUES ('lab_tech'), ('imaging_tech')) AS r(role_name)
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;

-- ============================================================================================================
-- AND: the bench may ask what the member pays, on the same argument 0025 made for the pharmacist.
-- ============================================================================================================
-- A lab or imaging bench is the same situation as the dispensing counter — someone in front of a patient who
-- is about to be told what they owe — and the investigation order page shows the same three figures for the
-- same reason. `eligibility:check` is the EXISTING scope for "what does this member pay for this benefit
-- category at this provider", asked by whoever is asking; a second scope for the same question would be two
-- grants to reason about and two places to revoke.
--
-- orders-service forwards the TECHNICIAN's own token, never a service account: a service-account read is an
-- unattributable read, and this one touches a member's coverage.

INSERT INTO identity.role_scope (role_name, scope_name)
VALUES ('lab_tech', 'eligibility:check'),
       ('imaging_tech', 'eligibility:check')
ON CONFLICT DO NOTHING;

INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role_name, 'eligibility:check'
FROM identity.role_scope rs
CROSS JOIN (VALUES ('lab_tech'), ('imaging_tech')) AS r(role_name)
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;
