-- identity-service — 0039 the two projection seams become machine keys. Subtractive; see CONTRACT below.
--
-- migrate-compat: contract-ok — this REVOKES `reporting:project` and `finance:project` from human roles. It
-- is a deliberate narrowing, not an expand/contract oversight. Nothing in the platform calls either endpoint
-- with a user token: the SPA has no caller, and the relay that legitimately projects authenticates as a
-- client, not as a person. A token minted before this migration keeps the scope until it expires, which is
-- the normal revocation window and is why this is safe to apply without a deploy dance.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- `POST /api/v1/reports/projections` and `POST /api/v1/finance/projections` write facts into the read models.
-- Both are guarded by a policy rule that names NO roles, and `IAuthorizationEngine` reads an empty role set as
-- "any authenticated principal holding the scope". That construction is correct for a machine seam and only
-- for a machine seam — it is what `auth:ingest`, `claims:ingest` and `notification:ingest` all do, and each of
-- those is marked `service_only` so that no person can ever be the principal in question.
--
-- These two were marked `service_only = false` and granted to people. `medical_director` held
-- `reporting:project`, which meant a Medical Director's own browser token authorized a write into
-- `authorization_fact`, `pending_authorization`, `encounter_fact`, `utilization_fact`, `code_count` and
-- `financial_fact` — the six tables that produce that same director's turnaround, SLA-breach, no-show,
-- rejection and cost figures. `finance` held `finance:project` over its own cost read-model, identically.
--
-- Neither rule's author intended that. `ReportingPolicies.Project` documents itself as "a system projection
-- seam — a domain event refreshes the read-model (NOT a human action)", and `FinancePolicies` says the same.
-- The doc comment and the seed disagreed, and the seed is what runs.
--
-- WHY THE FLAG AND THE GRANT, NOT JUST THE GRANT. Deleting the rows fixes today. `AdminEndpoints` refuses to
-- attach a `service_only` key to a tenant-local role — "a service credential attached to a person, and no
-- review would ever catch it as one" — so setting the flag is what stops an administrator re-granting it
-- through the role editor tomorrow. The built-in roles bypass that check entirely, which is how these two got
-- here; `ProjectionSeamTests` in libs/authz now asserts the pairing directly.

-- ---------------------------------------------------------------- 1. mark both keys machine-only
UPDATE identity.scope
   SET service_only = true,
       description = CASE name
           WHEN 'reporting:project' THEN
               'Refresh the reporting read-model from a domain event. A MACHINE key: the principal is the '
               'event relay, never a person. A human holding this could write the facts their own dashboards '
               'are computed from.'
           WHEN 'finance:project' THEN
               'Refresh the finance read-model from a domain event. A MACHINE key, for the same reason as '
               'reporting:project — cost facts must not be authorable by the people the cost report is about.'
           ELSE description
       END
 WHERE name IN ('reporting:project', 'finance:project');

-- ---------------------------------------------------------------- 2. revoke the human grants
--
-- Across every tenant, not just the platform default. `role_scope` is tenant-scoped and a tenant that has its
-- own rows does not inherit the default's — 0027 learned that in the other direction, granting only the
-- default row and issuing tokens that were quietly short. The same asymmetry applies to a revocation: leaving
-- a tenant's own row in place would revoke the scope everywhere except where it had been deliberately set up.
DELETE FROM identity.role_scope
 WHERE scope_name IN ('reporting:project', 'finance:project');

-- ---------------------------------------------------------------- 3. keep it revoked
--
-- A trigger rather than a comment. The seeds in 0001 and 0005 are `ON CONFLICT DO NOTHING` inserts that run
-- on every migration pass, and re-seeding a role list is the single most likely way for this to come back —
-- it is, in fact, how it arrived. A constraint that names the invariant refuses the re-grant at the moment it
-- is attempted, and names the reason in the error rather than leaving somebody to rediscover this file.
CREATE OR REPLACE FUNCTION identity.refuse_machine_scope_on_a_role() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM identity.scope s WHERE s.name = NEW.scope_name AND s.service_only) THEN
        RAISE EXCEPTION
            'scope % is a machine key and cannot be granted to role % — a service credential attached to a '
            'person is one no access review would catch as one (identity 0039)',
            NEW.scope_name, NEW.role_name
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_role_scope_no_machine_keys ON identity.role_scope;
CREATE TRIGGER trg_role_scope_no_machine_keys
    BEFORE INSERT OR UPDATE ON identity.role_scope
    FOR EACH ROW EXECUTE FUNCTION identity.refuse_machine_scope_on_a_role();
