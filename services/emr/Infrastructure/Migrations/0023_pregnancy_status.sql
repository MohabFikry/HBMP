-- ============================================================================================================
-- 0023 — pregnancy as a recorded STATUS (44-clinical-validation-hardening §5, phase 28 Gate 9). Idempotent.
-- ============================================================================================================
-- For a refugee primary-care population this is the single highest-yield clinical check the platform can
-- make, and it is cheap: it is a status somebody asks about, not a laboratory value nobody has ordered.
--
-- WHY ITS OWN TABLE AND NOT A COLUMN ON `beneficiary_clinical`.
-- That table is documented as holding "standing clinical facts that are not encounter observations" — blood
-- group, which does not change. Pregnancy is the opposite: a status with a shelf life, which needs a history
-- and a staleness judgement. Putting it beside blood group would mean either losing the previous value on
-- every update, or bolting a history onto a table whose whole premise is that its values do not move.
--
-- WHY `Unknown` IS A VALUE AND NOT AN ABSENT ROW.
-- "Nobody has asked" and "asked, and she is not pregnant" are different facts, and only one of them lets an
-- ACE inhibitor be prescribed without a second thought. An absent row cannot express the first honestly —
-- it looks identical to a beneficiary nobody has opened yet.
--
-- WHY NO FDA LETTER CATEGORY ANYWHERE HERE.
-- The FDA replaced the A/B/C/D/X categories with narrative labelling in 2015 precisely because a single
-- letter compressed away the trimester, the dose and the alternative. The risk statement lives on the
-- contraindication rule (masterdata.drug_disease_contraindication), as prose with a citation.

CREATE TABLE IF NOT EXISTS emr.pregnancy_status (
    beneficiary_id  uuid PRIMARY KEY,
    tenant_id       text NOT NULL,
    status          text NOT NULL DEFAULT 'Unknown'
                    CHECK (status IN ('Pregnant', 'NotPregnant', 'Unknown')),
    -- Estimated delivery date, where one is known. Nullable and usually null; it is what lets a later phase
    -- reason about trimester, which several of these rules genuinely depend on.
    edd             date,
    recorded_by     text,
    recorded_at     timestamptz,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE emr.pregnancy_status IS
    'Current pregnancy status per beneficiary. Unknown is a real recorded value, not an absent row: '
    '"nobody has asked" and "asked, not pregnant" are different facts and only one permits an ACE inhibitor.';
COMMENT ON COLUMN emr.pregnancy_status.recorded_at IS
    'When the status was last established. A status older than the configured window reads as STALE rather '
    'than current — the same discipline 28.8 applies to weight. A fourteen-month-old "Pregnant" is not a '
    'current pregnancy, and a fourteen-month-old "NotPregnant" is not a current absence of one.';

-- Append-only, like every other clinical history on this platform. A status that changed and left no trace
-- is one nobody can audit — and "when did we know she was pregnant" is exactly the question asked after a
-- teratogen reaches a patient.
CREATE TABLE IF NOT EXISTS emr.pregnancy_status_history (
    history_id      uuid PRIMARY KEY,
    beneficiary_id  uuid NOT NULL,
    tenant_id       text NOT NULL,
    status          text NOT NULL CHECK (status IN ('Pregnant', 'NotPregnant', 'Unknown')),
    edd             date,
    recorded_by     text,
    recorded_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_pregnancy_history_beneficiary
    ON emr.pregnancy_status_history (beneficiary_id, recorded_at DESC);

-- RLS in exactly the shape 0007 gave every other emr table: same policy name, same USING clause, FORCE so
-- the owning role is bound by it too. Privileges come from 0007's ALTER DEFAULT PRIVILEGES.
ALTER TABLE emr.pregnancy_status ENABLE ROW LEVEL SECURITY;
ALTER TABLE emr.pregnancy_status FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_pregnancy_status ON emr.pregnancy_status;
CREATE POLICY rls_pregnancy_status ON emr.pregnancy_status
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

ALTER TABLE emr.pregnancy_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE emr.pregnancy_status_history FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_pregnancy_status_history ON emr.pregnancy_status_history;
CREATE POLICY rls_pregnancy_status_history ON emr.pregnancy_status_history
    USING (tenant_id = current_setting('app.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

-- 0015's rule: no unscoped rows anywhere in emr.
ALTER TABLE emr.pregnancy_status DROP CONSTRAINT IF EXISTS ck_pregnancy_status_tenant_not_blank;  -- migrate-compat: contract-ok (added immediately below; the drop exists only to make this migration re-runnable)
ALTER TABLE emr.pregnancy_status ADD CONSTRAINT ck_pregnancy_status_tenant_not_blank
    CHECK (tenant_id <> '');
