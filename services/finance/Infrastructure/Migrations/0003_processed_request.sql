-- finance-service — 0003 HTTP idempotency ledger (16.9, API conventions: "endpoints that must not double-apply
-- accept Idempotency-Key"). Settlement generation mints a financial artifact, so a replayed key must return the
-- prior settlement rather than create a second one. RLS-free like processed_event (keys are opaque + globally
-- unique, not tenant data). Additive + idempotent.

CREATE TABLE IF NOT EXISTS finance.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    result_id       uuid NOT NULL,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE, DELETE ON finance.processed_request TO hbmp_app;
