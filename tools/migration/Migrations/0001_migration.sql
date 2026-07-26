-- Phase 12.1 — migration toolkit schema (staging + prod). Idempotent.
-- The migration tool runs as an operator/admin process (not the app runtime); the app role
-- hbmp_app gets read-only access so isolation/reconciliation checks can query landed rows.

CREATE SCHEMA IF NOT EXISTS migration;

-- One row per migration run: the reversibility + reproducibility boundary.
CREATE TABLE IF NOT EXISTS migration.batch (
    batch_id       uuid PRIMARY KEY,
    stream         text        NOT NULL,
    config_version text        NOT NULL,
    environment    text        NOT NULL,
    source_system  text        NOT NULL,
    started_at     timestamptz NOT NULL,
    masked         boolean     NOT NULL
);

-- Landed rows, provenance-tagged and soft-active. UPSERT on (stream, natural_key) = idempotency;
-- active=false = reverted by rollback-by-batch. Payload is the mapped target document.
CREATE TABLE IF NOT EXISTS migration.landing (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    stream        text        NOT NULL,
    natural_key   text        NOT NULL,
    payload       jsonb       NOT NULL,
    source_system text        NOT NULL,
    source_id     text        NOT NULL,
    batch_id      uuid        NOT NULL,
    loaded_at     timestamptz NOT NULL,
    active        boolean     NOT NULL DEFAULT true,
    CONSTRAINT uq_landing_stream_key UNIQUE (stream, natural_key)
);

CREATE INDEX IF NOT EXISTS ix_landing_batch  ON migration.landing (batch_id) WHERE active;
CREATE INDEX IF NOT EXISTS ix_landing_stream ON migration.landing (stream)   WHERE active;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT USAGE ON SCHEMA migration TO hbmp_app;
        GRANT SELECT ON migration.batch, migration.landing TO hbmp_app;
    END IF;
END $$;
