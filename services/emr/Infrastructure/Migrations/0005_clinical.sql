-- emr-service — Phase 4.1 clinical documentation (22-data-dictionary §6.3–6.7). Adds SOAP/Progress/Nursing
-- notes, coded diagnoses (ICD-10, validated vs masterdata), vitals (per-type range + optional LOINC),
-- allergies (allergen catalogue) and medication history. All clinical rows are soft-deletable (is_deleted) —
-- there is NO hard delete of clinical data; corrections to a SIGNED note are made with an addendum note, never
-- an in-place edit. Enum values are CHECK-constrained to the canonical sets exactly.

CREATE SCHEMA IF NOT EXISTS emr;

-- SOAP / Progress / Nursing note. is_signed locks the note (immutable); addendum_of_note_id links a correction.
CREATE TABLE IF NOT EXISTS emr.emr_note (
    note_id            uuid PRIMARY KEY,
    encounter_id       uuid NOT NULL REFERENCES emr.encounter(encounter_id),
    note_type          text NOT NULL DEFAULT 'SOAP' CHECK (note_type IN ('SOAP','Progress','Nursing')),
    subjective         text,
    objective          text,
    assessment         text,
    plan               text,
    addendum_of_note_id uuid REFERENCES emr.emr_note(note_id),
    authored_by        text NOT NULL,
    authored_at        timestamptz NOT NULL DEFAULT now(),
    is_signed          boolean NOT NULL DEFAULT false,
    signed_at          timestamptz,
    is_deleted         boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_emr_note_encounter ON emr.emr_note (encounter_id);

-- Coded diagnosis. icd_code MUST exist in masterdata.icd_code (validated by the service before insert).
CREATE TABLE IF NOT EXISTS emr.diagnosis (
    diagnosis_id     uuid PRIMARY KEY,
    encounter_id     uuid NOT NULL REFERENCES emr.encounter(encounter_id),
    icd_code         varchar(10) NOT NULL,
    diagnosis_rank   text NOT NULL DEFAULT 'Primary' CHECK (diagnosis_rank IN ('Primary','Secondary')),
    clinical_status  text NOT NULL DEFAULT 'Active' CHECK (clinical_status IN ('Active','Resolved','Recurrence')),
    recorded_by      text NOT NULL,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    is_deleted       boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_diagnosis_encounter ON emr.diagnosis (encounter_id);

-- Vital observation. value_num validated against a per-type plausible range in the service; loinc optional.
CREATE TABLE IF NOT EXISTS emr.vital (
    vital_id      uuid PRIMARY KEY,
    encounter_id  uuid NOT NULL REFERENCES emr.encounter(encounter_id),
    vital_type    text NOT NULL CHECK (vital_type IN ('BP','HR','Temp','SpO2','Weight','Height','BMI')),
    value_num     numeric(10,3),
    unit          varchar(10),
    loinc_code    varchar(10),
    recorded_by   text NOT NULL,
    measured_at   timestamptz NOT NULL DEFAULT now(),
    is_deleted    boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_vital_encounter ON emr.vital (encounter_id);

-- Allergy at the beneficiary level. allergen_id references masterdata.allergen (validated by the service).
CREATE TABLE IF NOT EXISTS emr.allergy (
    allergy_id     uuid PRIMARY KEY,
    beneficiary_id uuid NOT NULL,
    allergen_id    uuid NOT NULL,
    reaction       varchar(120),
    severity       text NOT NULL DEFAULT 'Mild' CHECK (severity IN ('Mild','Moderate','Severe')),
    status         text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive','Resolved')),
    recorded_by    text NOT NULL,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_allergy_beneficiary ON emr.allergy (beneficiary_id);

-- Medication history at the beneficiary level. drug_id references masterdata.drug (validated by the service).
CREATE TABLE IF NOT EXISTS emr.medication_history (
    med_history_id uuid PRIMARY KEY,
    beneficiary_id uuid NOT NULL,
    drug_id        uuid NOT NULL,
    source         text NOT NULL DEFAULT 'SelfReported' CHECK (source IN ('Prescribed','SelfReported','External')),
    start_date     date,
    end_date       date,
    status         text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Stopped')),
    recorded_by    text NOT NULL,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_medication_history_beneficiary ON emr.medication_history (beneficiary_id);
