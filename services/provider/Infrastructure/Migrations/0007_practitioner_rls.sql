-- provider-service — 0007 tenant RLS on the practitioner tables (Phase 18.E2). Additive + idempotent.
--
-- FOUND BY THE NEW ARCHITECTURE TEST, not by the R2 audit and not by review.
--
-- 0006 (Phase 14.5) added provider.practitioner with `tenant_id NOT NULL` and no policy, while 0003 had
-- already established RLS for every table that existed at the time. The pattern was in place; the new tables
-- simply did not join it, and nothing checked — which is exactly the omission class the house-pattern rules
-- exist to catch. `Every_tenant_scoped_table_has_an_rls_policy` reported it on its first run.
--
-- What was exposed: practitioner holds staff full names (EN + AR), the professional LICENCE NUMBER and its
-- expiry. Not beneficiary PHI, but personal data about identifiable clinicians and a regulator-issued
-- credential — and the doctor↔branch assignment table (18.C2 / FR-BRN-026-027) is now a live input to
-- booking decisions, so its integrity matters operationally as well.
--
-- practitioner_specialty and practitioner_branch_assignment carry no tenant_id of their own; they inherit
-- visibility through their parent practitioner, the same shape claims.claim_line uses.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON provider.practitioner, provider.practitioner_specialty,
    provider.practitioner_branch_assignment, provider.specialty TO hbmp_app;

ALTER TABLE provider.practitioner ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.practitioner FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_practitioner ON provider.practitioner;
-- Fail-CLOSED: an unset or empty app.tenant_id matches nothing (18.B2 — no `OR ... IS NULL` escape).
CREATE POLICY rls_practitioner ON provider.practitioner
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- Children: visibility follows the parent, so a practitioner outside the caller's tenant takes its
-- specialties and branch assignments with it.
ALTER TABLE provider.practitioner_specialty ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.practitioner_specialty FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_practitioner_specialty ON provider.practitioner_specialty;
CREATE POLICY rls_practitioner_specialty ON provider.practitioner_specialty
    USING (EXISTS (SELECT 1 FROM provider.practitioner p
                   WHERE p.practitioner_id = practitioner_specialty.practitioner_id));

ALTER TABLE provider.practitioner_branch_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.practitioner_branch_assignment FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_practitioner_branch_assignment ON provider.practitioner_branch_assignment;
CREATE POLICY rls_practitioner_branch_assignment ON provider.practitioner_branch_assignment
    USING (EXISTS (SELECT 1 FROM provider.practitioner p
                   WHERE p.practitioner_id = practitioner_branch_assignment.practitioner_id));

-- provider.specialty is a reference catalogue (specialty codes + bilingual labels), tenant-free by design
-- like masterdata: a cardiology code means the same thing for every tenant. Left un-isolated deliberately.
