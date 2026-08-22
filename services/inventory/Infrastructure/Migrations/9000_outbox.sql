-- 25.5: durable transactional outbox for the inventory schema. Additive + idempotent.
--
-- IT WAS MISSING, AND THAT BROKE READS. `AddHbmpDurableOutbox<InventoryDbContext>` stages every domain event
-- into this table inside the business transaction — and `AddHbmpEvents` reroutes the AUDIT client through the
-- same outbox, so a stock read (a PHI-adjacent read this platform audits) writes here too. With no table,
-- every request to inventory-service ended in `42P01: relation "inventory.outbox_message" does not exist`,
-- and the relay logged the same failure once a second forever. The screen reported "the service couldn't
-- complete this request" for what was a missing DDL file.
--
-- Nineteen services carry this file; inventory shipped without it. `OutboxRelayRegistrationTests` checked that
-- a service staging an outbox also registers a relay — both were present here — and nothing checked that the
-- table either of them talks to exists.
CREATE TABLE IF NOT EXISTS "inventory".outbox_message (
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
CREATE INDEX IF NOT EXISTS ix_inventory_outbox_pending
    ON "inventory".outbox_message (occurred_at) WHERE processed_at IS NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON "inventory".outbox_message TO hbmp_app;
    END IF;
END $$;
