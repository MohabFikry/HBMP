-- provider-service — 0001 provider network schema (15-database-erd provider domain, 22-data-dictionary §5).
-- Every row is tenant-scoped (tenant_id) and provider-owned rows carry provider_id; RLS predicates are
-- added in 0002 (phase 2b.3). Soft-delete + *_history only — Suspended/Terminated providers stay
-- readable for audit and are never hard-deleted.
--
-- DEVIATION (documented): 22 §5.2 specifies provider_location.geo_point geography(Point). The dev
-- Postgres image ships without PostGIS, so location is stored as geo_lat/geo_lng numeric(9,6). Swap to
-- geography(Point) once PostGIS is available; the API contract is unaffected.

CREATE SCHEMA IF NOT EXISTS provider;

-- btree_gist backs the contract effective-range exclusion constraint below.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE IF NOT EXISTS provider.provider (
    provider_id      uuid PRIMARY KEY,
    tenant_id        text NOT NULL,
    provider_code    varchar(20) NOT NULL,
    legal_name       varchar(160) NOT NULL,
    provider_type    varchar(16) NOT NULL CHECK (provider_type IN ('Hospital','Clinic','Lab','Pharmacy','Imaging')),
    status           varchar(16) NOT NULL DEFAULT 'Suspended' CHECK (status IN ('Active','Suspended','Terminated')),
    onboarding_state varchar(20) NOT NULL DEFAULT 'Draft'
                     CHECK (onboarding_state IN ('Draft','DocumentsCollected','Credentialed','Contracted','Activated','Suspended','Terminated')),
    is_deleted       boolean NOT NULL DEFAULT false,
    row_version      int NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_provider_code UNIQUE (tenant_id, provider_code)
);
CREATE INDEX IF NOT EXISTS ix_provider_tenant ON provider.provider (tenant_id);

CREATE TABLE IF NOT EXISTS provider.provider_location (
    location_id  uuid PRIMARY KEY,
    provider_id  uuid NOT NULL REFERENCES provider.provider(provider_id),
    tenant_id    text NOT NULL,
    name         varchar(120) NOT NULL,
    governorate  varchar(60),
    address      varchar(256),
    geo_lat      numeric(9,6),
    geo_lng      numeric(9,6),
    is_primary   boolean NOT NULL DEFAULT false,
    is_deleted   boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_location_provider ON provider.provider_location (provider_id);
-- Exactly one primary location per provider.
CREATE UNIQUE INDEX IF NOT EXISTS uq_location_primary
    ON provider.provider_location (provider_id) WHERE is_primary AND NOT is_deleted;

CREATE TABLE IF NOT EXISTS provider.provider_contract (
    contract_id    uuid PRIMARY KEY,
    provider_id    uuid NOT NULL REFERENCES provider.provider(provider_id),
    tenant_id      text NOT NULL,
    contract_no    varchar(30) NOT NULL,
    effective_from date NOT NULL,
    effective_to   date,
    status         varchar(16) NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Active','Expired','Terminated')),
    is_deleted     boolean NOT NULL DEFAULT false,
    CONSTRAINT uq_contract_no UNIQUE (tenant_id, contract_no),
    -- Effective ranges must not overlap for the same provider (open-ended = 'infinity').
    CONSTRAINT ex_contract_no_overlap EXCLUDE USING gist (
        provider_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    ) WHERE (NOT is_deleted AND status <> 'Terminated')
);
CREATE INDEX IF NOT EXISTS ix_contract_provider ON provider.provider_contract (provider_id);

CREATE TABLE IF NOT EXISTS provider.contract_service_line (
    service_line_id uuid PRIMARY KEY,
    contract_id     uuid NOT NULL REFERENCES provider.provider_contract(contract_id),
    tenant_id       text NOT NULL,
    service_type    varchar(16) NOT NULL CHECK (service_type IN ('Lab','Imaging','Consult','Procedure')),
    code_system     varchar(10) NOT NULL CHECK (code_system IN ('CPT','LOINC','LOCAL')),
    code            varchar(20) NOT NULL,
    agreed_price    numeric(14,2) NOT NULL CHECK (agreed_price >= 0),
    currency_code   char(3) NOT NULL DEFAULT 'EGP',
    CONSTRAINT uq_service_line_code UNIQUE (contract_id, code_system, code)
);
CREATE INDEX IF NOT EXISTS ix_service_line_contract ON provider.contract_service_line (contract_id);

CREATE TABLE IF NOT EXISTS provider.provider_credential (
    credential_id   uuid PRIMARY KEY,
    provider_id     uuid NOT NULL REFERENCES provider.provider(provider_id),
    tenant_id       text NOT NULL,
    credential_type varchar(40) NOT NULL,
    status          varchar(16) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Valid','Expired','Rejected')),
    valid_from      date,
    valid_to        date,
    document_id     uuid,
    is_mandatory    boolean NOT NULL DEFAULT false,
    is_deleted      boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_credential_provider ON provider.provider_credential (provider_id);

-- Soft-delete history: any mutation to a provider row is captured immutably (audit is separate + hash-chained).
CREATE TABLE IF NOT EXISTS provider.provider_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    provider_id  uuid NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    changed_at   timestamptz NOT NULL DEFAULT now()
);
CREATE OR REPLACE FUNCTION provider.write_provider_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO provider.provider_history (provider_id, operation, row_snapshot)
    VALUES (NEW.provider_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_provider_history ON provider.provider;
CREATE TRIGGER trg_provider_history AFTER INSERT OR UPDATE ON provider.provider
    FOR EACH ROW EXECUTE FUNCTION provider.write_provider_history();
