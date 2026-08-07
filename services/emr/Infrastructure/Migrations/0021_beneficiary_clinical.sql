-- ============================================================================================================
-- 0021 — standing clinical facts about a person, starting with blood group.
-- ============================================================================================================
-- Blood group is the first field the platform has that is neither an encounter observation nor an allergy: it
-- belongs to the PERSON, it does not change, and it is needed before anyone acts. That is a different shape
-- from emr.vital (measured, encounter-scoped, many rows over time) and from emr.allergy (a list), so it gets
-- its own one-row-per-beneficiary table rather than being forced into either.
--
-- WHY EMR AND NOT PATIENT-SERVICE. The identity strip that displays it is assembled by profile-service from
-- patient-service's administrative record, and putting blood group there would have been one column and no
-- new endpoints. It would also have handed the field to every role that may read a beneficiary's
-- demographics — reception, the call centre, finance — because that record is deliberately broad. Blood group
-- is clinical: recording it requires emr:write and a treating relationship, and reading it goes through the
-- same ClinicalGate as an allergy. Storing PHI in the administrative record to save a fetch is how
-- minimum-necessary erodes, one convenient column at a time.
--
-- WHY NOT AN ENUM. The eight values ARE their display strings ('A+', 'O-'); a C# enum would need members
-- named APos/ONeg and a two-way map whose only job is to undo itself. A CHECK constraint gives the same
-- closed set with the value intact.

CREATE TABLE IF NOT EXISTS emr.beneficiary_clinical (
    beneficiary_id  uuid PRIMARY KEY,
    tenant_id       text NOT NULL,
    -- Nullable, and that is the normal state. "Not recorded" is the honest answer for a patient nobody has
    -- typed, and it must stay distinguishable from a recorded value — a UI that renders unknown as blank
    -- beside seven known facts invites the reader to skim past it.
    blood_group     text CHECK (blood_group IN ('A+','A-','B+','B-','AB+','AB-','O+','O-')),
    recorded_by     text,
    recorded_at     timestamptz
);

COMMENT ON TABLE emr.beneficiary_clinical IS
    'One row per beneficiary: standing clinical facts that are not encounter observations. Blood group today.';
COMMENT ON COLUMN emr.beneficiary_clinical.blood_group IS
    'ABO + Rh, as displayed. NULL = never recorded, which is never rendered as a negative finding.';

-- RLS, in exactly the shape 0007 gave every other emr table: same policy name (`rls_<table>`), same USING
-- clause, FORCE so the owning role is bound by it too. Privileges come from 0007's ALTER DEFAULT PRIVILEGES.
ALTER TABLE emr.beneficiary_clinical ENABLE ROW LEVEL SECURITY;
ALTER TABLE emr.beneficiary_clinical FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_beneficiary_clinical ON emr.beneficiary_clinical;
CREATE POLICY rls_beneficiary_clinical ON emr.beneficiary_clinical
    USING (tenant_id = current_setting('app.tenant_id', true));

-- 0015's rule applied to the new table rather than discovered on it later: a row belonging to no tenant is
-- invisible to every tenant and deletable by none.
-- migrate-compat: contract-ok (the constraint being dropped is one THIS migration adds four lines below, on a
-- table THIS migration creates. The drop exists so a re-run is idempotent, not to retire a constraint any
-- deployed code depends on — there is no rollout window in which an older service saw it.)
ALTER TABLE emr.beneficiary_clinical DROP CONSTRAINT IF EXISTS ck_beneficiary_clinical_tenant_not_blank;
ALTER TABLE emr.beneficiary_clinical ADD CONSTRAINT ck_beneficiary_clinical_tenant_not_blank
    CHECK (length(btrim(tenant_id)) > 0);
