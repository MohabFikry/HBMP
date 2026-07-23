-- eligibility-service schema (Phase 2.1). Derived read models + snapshots; NOT a source of truth.
-- Owns only minimum-necessary member facts (11-permission-matrix): no clinical/EMR columns here.
CREATE SCHEMA IF NOT EXISTS eligibility;

-- Minimum-necessary member projection (status + identity for eligibility + reception search).
CREATE TABLE IF NOT EXISTS eligibility.member_projection (
    beneficiary_id  uuid PRIMARY KEY,
    member_no       text,
    given_name      text NOT NULL DEFAULT '',
    family_name     text NOT NULL DEFAULT '',
    status          text NOT NULL DEFAULT 'Pending',
    primary_phone   text,
    national_id     text,
    passport        text,
    refugee_id      text,
    unhcr_no        text,
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_member_projection_member_no   ON eligibility.member_projection (member_no);
CREATE INDEX IF NOT EXISTS ix_member_projection_national_id ON eligibility.member_projection (national_id);
CREATE INDEX IF NOT EXISTS ix_member_projection_passport    ON eligibility.member_projection (passport);
CREATE INDEX IF NOT EXISTS ix_member_projection_phone       ON eligibility.member_projection (primary_phone);

-- Coverage projection with denormalized limits (jsonb array of {limitType,limitValue,consumedValue}).
CREATE TABLE IF NOT EXISTS eligibility.coverage_projection (
    coverage_id      uuid PRIMARY KEY,
    beneficiary_id   uuid NOT NULL,
    benefit_category text NOT NULL DEFAULT '',
    policy_no        text NOT NULL DEFAULT '',
    status           text NOT NULL DEFAULT 'Active',
    effective_from   date NOT NULL DEFAULT CURRENT_DATE,
    effective_to     date,
    limits_json      jsonb NOT NULL DEFAULT '[]'::jsonb,
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_coverage_projection_beneficiary ON eligibility.coverage_projection (beneficiary_id);

-- Derived eligibility snapshot (cache-optimized; invalidated by policy/coverage/status events).
CREATE TABLE IF NOT EXISTS eligibility.eligibility_snapshot (
    snapshot_id      uuid PRIMARY KEY,
    beneficiary_id   uuid NOT NULL,
    benefit_category text NOT NULL,
    decision         text NOT NULL,
    coverage_id      uuid,
    limit_state_json jsonb NOT NULL DEFAULT 'null'::jsonb,
    reasons_json     jsonb NOT NULL DEFAULT '[]'::jsonb,
    version_hash     text NOT NULL DEFAULT '',
    computed_at      timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_eligibility_snapshot_key
    ON eligibility.eligibility_snapshot (beneficiary_id, benefit_category);

-- Idempotency ledger for at-least-once event consumers.
CREATE TABLE IF NOT EXISTS eligibility.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);
