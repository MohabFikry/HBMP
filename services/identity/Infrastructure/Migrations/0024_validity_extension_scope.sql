-- identity-service — 0024 the fulfillers may ASK for an expired item to be revalidated. Additive + idempotent.
--
-- WHO IS BEING GIVEN WHAT.
--
-- A pharmacist, or a lab / imaging technician, is holding a prescription or an investigation order that has
-- gone past its validity window, with the patient in front of them. Before this the only recovery was to
-- send that patient back to a doctor for a fresh one — a wasted journey, and often a second appointment
-- someone has to travel to, for a decision the approval team is already constituted to make.
--
-- `auth:request-extension` lets them RAISE that question and nothing else:
--
--   * It authorizes exactly one endpoint, POST /authorizations/validity-extensions, whose body names an
--     expired prescription or order and a reason. It cannot create a general authorization.
--   * It carries NO decision authority. The request lands Submitted in the approval team's normal queue,
--     with the normal SLA clock, and the requester cannot decide their own — `auth:decide` is held by
--     medical_approval and medical_director and is not granted here.
--   * It is not `auth:manual`, which would have let a pharmacist author an arbitrary authorization for any
--     beneficiary and any service code, and not `auth:ingest`, which is the machine seam for the routing
--     saga and would have handed them its reach.
--
-- The scope catalogue entry comes first; a grant referencing an unknown scope is how a role ends up holding
-- a string nothing enforces.

INSERT INTO identity.scope (name, domain, service_only) VALUES
    ('auth:request-extension', 'auth', false)
ON CONFLICT DO NOTHING;

COMMENT ON TABLE identity.scope IS
    'The frozen scope vocabulary. auth:request-extension = ask the approval team to revalidate an expired '
    'prescription or investigation order; raises a request and grants no decision authority.';

INSERT INTO identity.role_scope (role_name, scope_name)
VALUES ('pharmacist', 'auth:request-extension'),
       ('lab_tech', 'auth:request-extension'),
       ('imaging_tech', 'auth:request-extension')
ON CONFLICT DO NOTHING;

-- Fan out to every provisioned tenant — after 0012 each owns its own grant set and does not inherit live.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT DISTINCT rs.tenant_id, r.role_name, 'auth:request-extension'
FROM identity.role_scope rs
CROSS JOIN (VALUES ('pharmacist'), ('lab_tech'), ('imaging_tech')) AS r(role_name)
WHERE rs.tenant_id <> ''
ON CONFLICT DO NOTHING;
