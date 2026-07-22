-- patient-service — 0001 beneficiary schema (15-database-erd §4, 23-state-machines §1).
-- Soft-delete + history (twin written by trigger, never app code). Dedup via partial unique index.

CREATE SCHEMA IF NOT EXISTS patient;

CREATE TABLE IF NOT EXISTS patient.beneficiary (
    beneficiary_id   uuid PRIMARY KEY,
    member_no        text UNIQUE,
    given_name       text NOT NULL,
    family_name      text NOT NULL,
    birth_date       date,
    sex              text,
    nationality_code text,
    status           text NOT NULL DEFAULT 'Pending'
                     CHECK (status IN ('Pending','Active','Suspended','Expired','Blocked','Inactive')),
    family_group_id  uuid,
    is_deleted       boolean NOT NULL DEFAULT false,
    row_version      int NOT NULL DEFAULT 0,
    created_by       text,
    updated_by       text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS patient.beneficiary_identifier (
    identifier_id    uuid PRIMARY KEY,
    beneficiary_id   uuid NOT NULL REFERENCES patient.beneficiary(beneficiary_id),
    identifier_type  text NOT NULL CHECK (identifier_type IN ('NationalID','Passport','RefugeeID','UNHCRNo','MemberNo')),
    identifier_value text NOT NULL,
    issuing_country  text,
    valid_from       date,
    valid_to         date,
    is_primary       boolean NOT NULL DEFAULT false,
    is_deleted       boolean NOT NULL DEFAULT false
);
-- Duplicate detection: an identifier (type+value) is unique among ACTIVE (non-deleted) rows.
CREATE UNIQUE INDEX IF NOT EXISTS uq_identifier_active
    ON patient.beneficiary_identifier (identifier_type, identifier_value)
    WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS patient.contact (
    contact_id        uuid PRIMARY KEY,
    beneficiary_id    uuid NOT NULL REFERENCES patient.beneficiary(beneficiary_id),
    contact_type      text NOT NULL CHECK (contact_type IN ('Phone','Email','Address','EmergencyContact')),
    value             text NOT NULL,
    preferred_channel text,
    is_primary        boolean NOT NULL DEFAULT false,
    is_deleted        boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS patient.family_group (
    family_group_id    uuid PRIMARY KEY,
    family_code        text NOT NULL UNIQUE,
    head_beneficiary_id uuid
);

CREATE TABLE IF NOT EXISTS patient.dependent_link (
    dependent_link_id       uuid PRIMARY KEY,
    family_group_id         uuid NOT NULL REFERENCES patient.family_group(family_group_id),
    guardian_beneficiary_id uuid NOT NULL REFERENCES patient.beneficiary(beneficiary_id),
    dependent_beneficiary_id uuid NOT NULL REFERENCES patient.beneficiary(beneficiary_id),
    relationship            text NOT NULL CHECK (relationship IN ('Child','Spouse','Parent','Other'))
);

-- Member No sequence per year (monotonic), issued at activation (1.4).
CREATE TABLE IF NOT EXISTS patient.member_no_seq (
    year       int PRIMARY KEY,
    last_value int NOT NULL DEFAULT 0
);

-- ------------------------------------------------------------------ History twin (trigger-written)
CREATE TABLE IF NOT EXISTS patient.beneficiary_history (
    history_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    beneficiary_id uuid NOT NULL,
    operation      text NOT NULL,           -- INSERT | UPDATE | SOFT_DELETE
    row_snapshot   jsonb NOT NULL,
    changed_at     timestamptz NOT NULL DEFAULT now(),
    changed_by     text
);

CREATE OR REPLACE FUNCTION patient.write_beneficiary_history()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE op text;
BEGIN
    op := CASE
        WHEN TG_OP = 'INSERT' THEN 'INSERT'
        WHEN TG_OP = 'UPDATE' AND NEW.is_deleted AND NOT OLD.is_deleted THEN 'SOFT_DELETE'
        ELSE 'UPDATE' END;
    INSERT INTO patient.beneficiary_history (beneficiary_id, operation, row_snapshot, changed_by)
    VALUES (NEW.beneficiary_id, op, to_jsonb(NEW), NEW.updated_by);
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_beneficiary_history ON patient.beneficiary;
CREATE TRIGGER trg_beneficiary_history
    AFTER INSERT OR UPDATE ON patient.beneficiary
    FOR EACH ROW EXECUTE FUNCTION patient.write_beneficiary_history();
