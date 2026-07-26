-- 16.2 (C1): durable transactional outbox for the reporting schema. Additive + idempotent.
CREATE TABLE IF NOT EXISTS "reporting".outbox_message (
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
CREATE INDEX IF NOT EXISTS ix_reporting_outbox_pending
    ON "reporting".outbox_message (occurred_at) WHERE processed_at IS NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON "reporting".outbox_message TO hbmp_app;
    END IF;
END $$;
