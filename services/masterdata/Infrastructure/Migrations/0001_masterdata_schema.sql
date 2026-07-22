-- masterdata-service — 0001 reference schema (22-data-dictionary §10.5, 15-database-erd §13).
-- Public, read-mostly reference data; loads are versioned by source_release. Idempotent.

CREATE SCHEMA IF NOT EXISTS masterdata;

CREATE TABLE IF NOT EXISTS masterdata.icd_code (
    code            text PRIMARY KEY,
    title           text NOT NULL,
    chapter         text,
    is_billable     boolean NOT NULL DEFAULT false,
    icd11_map       text,
    source_release  text
);
CREATE INDEX IF NOT EXISTS ix_icd_chapter  ON masterdata.icd_code (chapter);
CREATE INDEX IF NOT EXISTS ix_icd_billable ON masterdata.icd_code (is_billable);

CREATE TABLE IF NOT EXISTS masterdata.cpt_code (
    code            text PRIMARY KEY,
    description     text NOT NULL,
    category        text,
    source_release  text
);
CREATE INDEX IF NOT EXISTS ix_cpt_category ON masterdata.cpt_code (category);

CREATE TABLE IF NOT EXISTS masterdata.loinc_code (
    code            text PRIMARY KEY,
    long_name       text NOT NULL,
    component       text,
    property        text,
    source_release  text
);

CREATE TABLE IF NOT EXISTS masterdata.atc_class (
    atc_code        text PRIMARY KEY,
    title           text NOT NULL,
    level           int  NOT NULL,
    source_release  text
);
CREATE INDEX IF NOT EXISTS ix_atc_level ON masterdata.atc_class (level);

CREATE TABLE IF NOT EXISTS masterdata.drug (
    drug_id         uuid PRIMARY KEY,
    drug_code       text NOT NULL UNIQUE,
    name            text NOT NULL,
    name_ar         text,
    scientific_name text,
    manufacturer    text,
    form            text,
    strength        text,
    atc_code        text REFERENCES masterdata.atc_class(atc_code) ON DELETE SET NULL,
    price_egp       numeric(14,2),
    source_release  text
);
CREATE INDEX IF NOT EXISTS ix_drug_atc ON masterdata.drug (atc_code);

CREATE TABLE IF NOT EXISTS masterdata.drug_interaction (
    interaction_id  uuid PRIMARY KEY,
    drug_a_id       uuid NOT NULL REFERENCES masterdata.drug(drug_id),
    drug_b_id       uuid NOT NULL REFERENCES masterdata.drug(drug_id),
    severity        text NOT NULL CHECK (severity IN ('Minor','Moderate','Major','Contraindicated')),
    description     text,
    source_release  text,
    CONSTRAINT uq_interaction_pair UNIQUE (drug_a_id, drug_b_id)
);

CREATE TABLE IF NOT EXISTS masterdata.allergen (
    allergen_id     uuid PRIMARY KEY,
    code            text NOT NULL UNIQUE,
    name            text NOT NULL,
    category        text NOT NULL CHECK (category IN ('Drug','Food','Environmental')),
    source_release  text
);
