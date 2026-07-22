-- audit-service — 0001 append-only, hash-chained, monthly-partitioned audit store.
-- 19-audit-strategy.md §4 (immutability/WORM/hash-chaining), §10 (isolation), 22-data-dictionary §10.4.
-- Idempotent: safe to re-run. Partition helper creates future months on demand.

CREATE SCHEMA IF NOT EXISTS audit;

-- Parent table, RANGE-partitioned monthly by occurred_at. PK includes the partition key.
CREATE TABLE IF NOT EXISTS audit.audit_event (
    audit_event_id      uuid          NOT NULL,
    partition_key       text          NOT NULL,               -- 'yyyyMM' (chain grouping)
    seq                 bigint        GENERATED ALWAYS AS IDENTITY,
    service_name        text          NOT NULL,
    source_service      text          NOT NULL,
    entity_type         text          NOT NULL,
    entity_id           text          NOT NULL,
    action              text          NOT NULL,
    severity            text          NOT NULL DEFAULT 'Info',
    actor_user_id       text,
    actor_role          text,
    tenant_id           text,
    provider_id         text,
    session_id          text,
    actor_mfa           boolean       NOT NULL DEFAULT false,
    before_state        jsonb,
    after_state         jsonb,
    field_classes       text[]        NOT NULL DEFAULT '{}',
    decision_outcome    text,
    decision_policy_id  text,
    decision_reason_code text,
    purpose             text,
    break_glass         boolean       NOT NULL DEFAULT false,
    correlation_id      text,
    occurred_at         timestamptz   NOT NULL,
    prev_hash           text,
    record_hash         text          NOT NULL,
    CONSTRAINT pk_audit_event PRIMARY KEY (audit_event_id, occurred_at)
) PARTITION BY RANGE (occurred_at);

CREATE INDEX IF NOT EXISTS ix_audit_entity   ON audit.audit_event (entity_type, entity_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_audit_corr     ON audit.audit_event (correlation_id);
CREATE INDEX IF NOT EXISTS ix_audit_part_seq ON audit.audit_event (partition_key, seq);

-- Helper: create the monthly partition covering a given date if absent.
CREATE OR REPLACE FUNCTION audit.ensure_partition(p_date date)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    start_ts timestamptz := date_trunc('month', p_date);
    end_ts   timestamptz := date_trunc('month', p_date) + interval '1 month';
    part     text := 'audit_event_' || to_char(start_ts, 'YYYYMM');
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = part) THEN
        EXECUTE format(
            'CREATE TABLE audit.%I PARTITION OF audit.audit_event FOR VALUES FROM (%L) TO (%L);',
            part, start_ts, end_ts);
    END IF;
END $$;

-- Materialize a rolling window of partitions (previous, current, next few months).
SELECT audit.ensure_partition((date_trunc('month', now()) + (g || ' month')::interval)::date)
FROM generate_series(-1, 3) AS g;

-- ------------------------------------------------------------------ Immutability
-- Dedicated append-only writer role: INSERT + SELECT only, NO UPDATE/DELETE within retention.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_audit_writer') THEN
        CREATE ROLE hbmp_audit_writer NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_audit_reader') THEN
        CREATE ROLE hbmp_audit_reader NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA audit TO hbmp_audit_writer, hbmp_audit_reader;
REVOKE ALL ON audit.audit_event FROM PUBLIC;
GRANT INSERT, SELECT ON audit.audit_event TO hbmp_audit_writer;   -- no UPDATE/DELETE
GRANT SELECT             ON audit.audit_event TO hbmp_audit_reader; -- read-only (Security/Compliance/DPO)

-- Defense in depth: a trigger blocks UPDATE/DELETE even for a mis-granted role, within retention.
CREATE OR REPLACE FUNCTION audit.deny_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit.audit_event is append-only: % is denied within retention', TG_OP;
END $$;

DROP TRIGGER IF EXISTS trg_audit_no_update ON audit.audit_event;
CREATE TRIGGER trg_audit_no_update BEFORE UPDATE OR DELETE ON audit.audit_event
    FOR EACH ROW EXECUTE FUNCTION audit.deny_mutation();

-- Row-level security on (isolation §10): only the audit roles may see rows.
ALTER TABLE audit.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit.audit_event FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_audit_read ON audit.audit_event;
CREATE POLICY p_audit_read ON audit.audit_event FOR SELECT
    USING (pg_has_role(current_user, 'hbmp_audit_reader', 'USAGE')
        OR pg_has_role(current_user, 'hbmp_audit_writer', 'USAGE'));
DROP POLICY IF EXISTS p_audit_insert ON audit.audit_event;
CREATE POLICY p_audit_insert ON audit.audit_event FOR INSERT
    WITH CHECK (pg_has_role(current_user, 'hbmp_audit_writer', 'USAGE'));
