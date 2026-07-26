-- Phase 13: durable transactional outbox for the interop schema (16.2 / C1 convention). Additive + idempotent.
-- The façade emits outbound integration events here (13.2) in the same transaction as the create-idempotency
-- write, so an emit is never lost. audit events ride the same outbox pattern.
CREATE TABLE IF NOT EXISTS "interop".outbox_message (
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
CREATE INDEX IF NOT EXISTS ix_interop_outbox_pending
    ON "interop".outbox_message (occurred_at) WHERE processed_at IS NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON "interop".outbox_message TO hbmp_app;
    END IF;
END $$;
