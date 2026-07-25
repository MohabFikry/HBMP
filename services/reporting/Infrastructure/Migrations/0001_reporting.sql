-- Phase 8.2 — reporting read-model. AGGREGATE + DE-IDENTIFIED projection (fact) tables: no beneficiary
-- identifiers, no free-text clinical notes, no row-level PHI. financial_fact carries NO diagnosis column
-- (finance ≠ diagnosis, 11-permission-matrix) — enforced here and asserted by a test.
CREATE SCHEMA IF NOT EXISTS reporting;

CREATE TABLE IF NOT EXISTS reporting.authorization_fact (
    fact_id               uuid PRIMARY KEY,
    event_id              uuid NOT NULL UNIQUE,
    tenant_id             text NOT NULL,
    auth_no               text NOT NULL,
    priority              text NOT NULL,
    outcome               text NOT NULL,
    reviewer_id           text,
    rejection_reason_code text,
    tat_seconds           bigint,
    sla_breached          boolean NOT NULL DEFAULT false,
    period                date NOT NULL,
    decided_at            timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_authfact_tenant_period ON reporting.authorization_fact (tenant_id, period);

CREATE TABLE IF NOT EXISTS reporting.pending_authorization (
    authorization_id uuid PRIMARY KEY,
    tenant_id        text NOT NULL,
    priority         text NOT NULL,
    status           text NOT NULL,
    submitted_at     timestamptz NOT NULL,
    sla_due_at       timestamptz,
    sla_breached     boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_pending_tenant_status ON reporting.pending_authorization (tenant_id, status);

CREATE TABLE IF NOT EXISTS reporting.encounter_fact (
    fact_id   uuid PRIMARY KEY,
    event_id  uuid NOT NULL UNIQUE,
    tenant_id text NOT NULL,
    clinic_id text NOT NULL,
    kind      text NOT NULL,
    period    date NOT NULL,
    count     integer NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_encounter_tenant_clinic_period ON reporting.encounter_fact (tenant_id, clinic_id, period);

CREATE TABLE IF NOT EXISTS reporting.utilization_fact (
    fact_id   uuid PRIMARY KEY,
    event_id  uuid NOT NULL UNIQUE,
    tenant_id text NOT NULL,
    dimension text NOT NULL,
    code      text NOT NULL,
    period    date NOT NULL,
    count     integer NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_util_tenant_dim_period ON reporting.utilization_fact (tenant_id, dimension, period);

CREATE TABLE IF NOT EXISTS reporting.code_count (
    fact_id   uuid PRIMARY KEY,
    event_id  uuid NOT NULL UNIQUE,
    tenant_id text NOT NULL,
    kind      text NOT NULL,
    code      text NOT NULL,
    period    date NOT NULL,
    count     integer NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_code_tenant_kind_period ON reporting.code_count (tenant_id, kind, period);

-- FINANCIAL zone — service line + code + amount ONLY. Intentionally NO diagnosis / clinical column.
CREATE TABLE IF NOT EXISTS reporting.financial_fact (
    fact_id      uuid PRIMARY KEY,
    event_id     uuid NOT NULL UNIQUE,
    tenant_id    text NOT NULL,
    service_line text NOT NULL,
    service_code text NOT NULL,
    amount       numeric(18,2) NOT NULL DEFAULT 0,
    period       date NOT NULL,
    count        integer NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_fin_tenant_line_period ON reporting.financial_fact (tenant_id, service_line, period);

CREATE TABLE IF NOT EXISTS reporting.processed_event (
    event_id    uuid PRIMARY KEY,
    event_type  text NOT NULL,
    consumed_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS reporting.report_job (
    job_id           uuid PRIMARY KEY,
    tenant_id        text NOT NULL,
    report           text NOT NULL,
    status           text NOT NULL DEFAULT 'Running',
    progress_percent integer NOT NULL DEFAULT 0,
    result_json      text,
    created_at       timestamptz NOT NULL,
    completed_at     timestamptz
);

-- reporting-service owns this schema; it is a read-model, so the app role reads/writes its own facts.
GRANT USAGE ON SCHEMA reporting TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA reporting TO hbmp_app;
