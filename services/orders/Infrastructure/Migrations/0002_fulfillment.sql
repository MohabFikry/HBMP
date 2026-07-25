-- orders-service — Phase 5 lab/imaging fulfillment (22-data-dictionary §7.3, 23-state-machines §2).
-- The append-only order_fulfillment table is the duplicate-proof anchor of the atomic consume: one immutable row
-- per consumed line, keyed by a UNIQUE idempotency_key so a replayed consume is rejected by the DB and mapped to
-- "return prior outcome". Over-consume is impossible: order_line already carries CHECK (0 ≤ consumed ≤ ordered),
-- and consume additionally guards on the line's xmin (optimistic concurrency) so exactly one racer wins. Rows are
-- never updated (except a one-time result attachment in 5.3) or soft-deleted — full history is in audit_event.

CREATE TABLE IF NOT EXISTS orders.order_fulfillment (
    fulfillment_id         uuid PRIMARY KEY,
    order_line_id          uuid NOT NULL REFERENCES orders.order_line(order_line_id),
    performing_provider_id uuid NOT NULL,
    quantity               numeric(14,3) NOT NULL CHECK (quantity > 0),
    idempotency_key        varchar(80) NOT NULL UNIQUE,
    result_document_id     uuid,
    result_value           text,
    result_uploaded_at     timestamptz,
    consumed_at            timestamptz NOT NULL DEFAULT now(),
    consumed_by            uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_fulfillment_line ON orders.order_fulfillment (order_line_id);
-- Fast idempotent-replay lookup: all fulfillment rows for one consume request share a `<key>::<lineId>` prefix.
CREATE INDEX IF NOT EXISTS ix_fulfillment_idem ON orders.order_fulfillment (idempotency_key varchar_pattern_ops);
