-- 16.6: durable transactional outbox for the masterdata schema so audit events emitted by the
-- clinical-decision-support screening endpoints are staged in the same DB write and relayed, not lost.
-- Additive + idempotent. masterdata is reference data (no tenant_id / no RLS).
CREATE TABLE IF NOT EXISTS "masterdata".outbox_message (
    event_id       uuid PRIMARY KEY,
    event_type     text NOT NULL,
    destination    text NOT NULL,
    payload        jsonb NOT NULL,
    correlation_id text NULL,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    processed_at   timestamptz NULL,
    attempts       int NOT NULL DEFAULT 0,
    last_error     text NULL
);
CREATE INDEX IF NOT EXISTS ix_masterdata_outbox_pending
    ON "masterdata".outbox_message (occurred_at) WHERE processed_at IS NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON "masterdata".outbox_message TO hbmp_app;
    END IF;
END $$;
