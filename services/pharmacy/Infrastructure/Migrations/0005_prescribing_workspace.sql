-- pharmacy-service — 0005 prescribing workspace (phase 26.4, doc 43 §5/§6).
--
-- Adds what the validation engine needs to be checkable and what its verdicts need to be recorded in:
-- a duration on the line, the diagnoses the prescription was written against, an append-only log of every
-- validation run, and the clinician's recorded reason for proceeding past a warning.

-- ---------------------------------------------------------------------------------------------------
-- duration — the field that makes a dosing ceiling checkable at all
-- ---------------------------------------------------------------------------------------------------
-- The line has carried dose, route, frequency and quantity since phase 4 but never a duration, so
-- "maximum 14 days" and "maximum daily dose" had nothing to evaluate against.
ALTER TABLE pharmacy.prescription_line ADD COLUMN IF NOT EXISTS duration_days integer
    CHECK (duration_days IS NULL OR duration_days > 0);

-- ---------------------------------------------------------------------------------------------------
-- the diagnoses this prescription was written against
-- ---------------------------------------------------------------------------------------------------
-- A SNAPSHOT, deliberately, not a foreign key to the encounter's diagnoses. The indication check is a
-- statement about what was known when the prescription was written; if a diagnosis is corrected next week,
-- the record of what was checked must not silently change to match. A FK would rewrite history.
ALTER TABLE pharmacy.prescription ADD COLUMN IF NOT EXISTS primary_icd_code varchar(10);
ALTER TABLE pharmacy.prescription ADD COLUMN IF NOT EXISTS diagnosis_snapshot jsonb;

COMMENT ON COLUMN pharmacy.prescription.diagnosis_snapshot IS
    'The encounter''s recorded ICD codes AS AT prescribing time. Immutable snapshot, not a join — a later '
    'diagnosis edit must not rewrite what the indication check actually compared against.';

-- ---------------------------------------------------------------------------------------------------
-- validation runs — append-only
-- ---------------------------------------------------------------------------------------------------
-- prescription_id is NULL for a draft validation: the doctor validates while composing, before anything is
-- submitted, and those runs are still part of the record of what the prescriber was shown.
CREATE TABLE IF NOT EXISTS pharmacy.prescription_validation (
    validation_id   uuid PRIMARY KEY,
    prescription_id uuid REFERENCES pharmacy.prescription(prescription_id),
    encounter_id    uuid NOT NULL,
    beneficiary_id  uuid NOT NULL,
    tenant_id       text NOT NULL DEFAULT '',
    ran_at          timestamptz NOT NULL DEFAULT now(),
    ran_by          text,
    -- 'Step1' — advisory, run while composing. 'Step2' — authoritative, run by the server on submit.
    -- Recorded separately because step 2 re-evaluates from scratch and a divergence between them is normal
    -- and must be visible rather than resolved silently (doc 43 §5).
    step            text NOT NULL DEFAULT 'Step1' CHECK (step IN ('Step1','Step2')),
    engine_version  varchar(32) NOT NULL,
    overall_state   text NOT NULL CHECK (overall_state IN ('Ok','Warning','Blocked','NotChecked','Unavailable')),
    findings        jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_rx_validation_prescription ON pharmacy.prescription_validation (prescription_id);
CREATE INDEX IF NOT EXISTS ix_rx_validation_encounter ON pharmacy.prescription_validation (encounter_id, ran_at DESC);

COMMENT ON TABLE pharmacy.prescription_validation IS
    'Append-only log of every validation run. Never updated, never deleted: it is the record of what the '
    'prescriber was shown and what the server concluded, which is the evidence a later review depends on.';

-- ---------------------------------------------------------------------------------------------------
-- overrides — the clinician's recorded reason for proceeding
-- ---------------------------------------------------------------------------------------------------
-- Doc 43 §1 rule 3: overrides are expected and recorded, not prevented. The reason is NOT NULL because an
-- acknowledgement with no reason is a click, and a click is not a justification.
CREATE TABLE IF NOT EXISTS pharmacy.prescription_line_override (
    override_id     uuid PRIMARY KEY,
    prescription_id uuid NOT NULL REFERENCES pharmacy.prescription(prescription_id),
    line_id         uuid NOT NULL REFERENCES pharmacy.prescription_line(prescription_line_id),
    tenant_id       text NOT NULL DEFAULT '',
    -- Which finding was overridden: check kind + the drug it was raised against.
    finding_kind    text NOT NULL CHECK (finding_kind IN ('Indication','Interaction','Allergy','DoseDuration','Benefit')),
    finding_ref     varchar(200),
    reason          varchar(300) NOT NULL CHECK (length(btrim(reason)) >= 3),
    acknowledged_by text NOT NULL,
    acknowledged_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_rx_override_prescription ON pharmacy.prescription_line_override (prescription_id);
CREATE INDEX IF NOT EXISTS ix_rx_override_line ON pharmacy.prescription_line_override (line_id);

-- ---------------------------------------------------------------------------------------------------
-- prescription_alert.kind — widen for "the check could not run"
-- ---------------------------------------------------------------------------------------------------
-- The CHECK admitted only 'DrugInteraction' and 'Allergy', which was consistent with a screener that could
-- only ever report those two. Phase 26.3 made "this check could not run" an outcome the screener can
-- express — and without widening this constraint, an unreachable masterdata would now fail the INSERT and
-- take the entire prescription write down with it. Trading a silent false-OK for a hard failure to
-- prescribe would not be an improvement.
-- migrate-compat: contract-ok (WIDENING a CHECK, not narrowing one. The replacement accepts a strict
-- superset of the old values, so during a rolling deploy the old code keeps writing 'DrugInteraction' and
-- 'Allergy' successfully while the new code additionally writes 'Unavailable'. There is no window in which
-- either version's writes are rejected, which is what the expand/contract rule exists to protect.)
ALTER TABLE pharmacy.prescription_alert DROP CONSTRAINT IF EXISTS prescription_alert_kind_check;
ALTER TABLE pharmacy.prescription_alert ADD CONSTRAINT prescription_alert_kind_check
    CHECK (kind IN ('DrugInteraction','Allergy','Unavailable'));

-- ---------------------------------------------------------------------------------------------------
-- tenant RLS on the two new tables (same shape as 0003)
-- ---------------------------------------------------------------------------------------------------
-- Both hold beneficiary-linked clinical content — a validation's findings name the drugs and the
-- diagnoses. Adding the tables without the policy is how a table ends up outside the isolation everything
-- around it has.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['prescription_validation','prescription_line_override']
    LOOP
        EXECUTE format(
            'ALTER TABLE pharmacy.%I ALTER COLUMN tenant_id SET DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
        EXECUTE format('ALTER TABLE pharmacy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE pharmacy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON pharmacy.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON pharmacy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON pharmacy.prescription_validation TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON pharmacy.prescription_line_override TO hbmp_app;
