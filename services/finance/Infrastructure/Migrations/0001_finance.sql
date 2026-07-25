-- finance-service — Phase 10.2 cost/utilization read-model + provider settlements. Built from domain events (never
-- by joining clinical tables). HARD INVARIANT: NO diagnosis / emr_note / lab_result / imaging_result column exists
-- anywhere in this schema — Finance ≠ diagnosis (11-permission-matrix §3.2/§4). Facts carry billing codes +
-- quantities + amounts only. Prices are READ from provider_contract; they are not duplicated or mutated here.

CREATE SCHEMA IF NOT EXISTS finance;

-- Delivered/authorized service line, valued. beneficiary_id is masked-min in projections; service_code is a BILLING
-- code (CPT/LOINC/ATC) — deliberately NOT a diagnosis.
CREATE TABLE IF NOT EXISTS finance.utilization_fact (
    fact_id           uuid PRIMARY KEY,
    event_id          uuid NOT NULL,
    tenant_id         text NOT NULL,
    beneficiary_id    uuid NOT NULL,
    coverage_category text NOT NULL DEFAULT 'General',
    service_code      text NOT NULL,
    service_line      text NOT NULL DEFAULT 'General',
    provider_id       uuid,
    authorized_qty    int  NOT NULL DEFAULT 0,
    delivered_qty     int  NOT NULL DEFAULT 0,
    unit_cost         numeric(14,2) NOT NULL DEFAULT 0,
    line_cost         numeric(14,2) NOT NULL DEFAULT 0,
    period            date NOT NULL,
    occurred_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_util_event ON finance.utilization_fact (event_id);
CREATE INDEX IF NOT EXISTS ix_util_tenant_period ON finance.utilization_fact (tenant_id, period);
CREATE INDEX IF NOT EXISTS ix_util_provider_period ON finance.utilization_fact (tenant_id, provider_id, period);

-- Monotonic per-year settlement-number sequence backing STL-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS finance.settlement_seq (
    year       int  PRIMARY KEY,
    last_value int  NOT NULL
);

CREATE TABLE IF NOT EXISTS finance.settlement (
    settlement_id  uuid PRIMARY KEY,
    settlement_no  varchar(24) NOT NULL UNIQUE,
    tenant_id      text NOT NULL,
    provider_id    uuid NOT NULL,
    contract_id    uuid,
    period_start   date NOT NULL,
    period_end     date NOT NULL,
    currency_code  text NOT NULL DEFAULT 'EGP',
    total          numeric(16,2) NOT NULL DEFAULT 0,
    status         text NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Submitted','Approved','Paid')),
    submitted_by   text,
    submitted_at   timestamptz,
    approved_by    text,
    approved_at    timestamptz,
    created_by     text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    -- SoD defense-in-depth: an approved settlement must have a submitter and an approver, and they must differ.
    CHECK (status <> 'Approved' OR (submitted_by IS NOT NULL AND approved_by IS NOT NULL AND submitted_by <> approved_by))
);
CREATE INDEX IF NOT EXISTS ix_settlement_provider ON finance.settlement (tenant_id, provider_id, period_start);

CREATE TABLE IF NOT EXISTS finance.settlement_line (
    settlement_line_id uuid PRIMARY KEY,
    settlement_id      uuid NOT NULL REFERENCES finance.settlement(settlement_id),
    service_code       text NOT NULL,
    service_line       text NOT NULL DEFAULT 'General',
    delivered_qty      int  NOT NULL DEFAULT 0,
    agreed_unit_price  numeric(14,2) NOT NULL DEFAULT 0,
    line_total         numeric(16,2) NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_settlement_line_parent ON finance.settlement_line (settlement_id);

-- Dedupe ledger for the idempotent read-model refresh.
CREATE TABLE IF NOT EXISTS finance.processed_event (
    event_id    uuid PRIMARY KEY,
    event_type  text NOT NULL,
    consumed_at timestamptz NOT NULL DEFAULT now()
);

-- Audited-export log (data.export — high severity). Masked PII; row count + filter + correlation id recorded.
CREATE TABLE IF NOT EXISTS finance.export_record (
    export_id     uuid PRIMARY KEY,
    tenant_id     text NOT NULL,
    report        text NOT NULL,
    format        text NOT NULL DEFAULT 'csv',
    filter        text,
    row_count     int  NOT NULL DEFAULT 0,
    requested_by  text,
    correlation_id text,
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- Belt-and-braces: assert no clinical column ever lands in the finance schema (the authz test also proves this at
-- the type level). This is a comment-guard for reviewers; the structural guarantee is the FinanceProjection layer.
