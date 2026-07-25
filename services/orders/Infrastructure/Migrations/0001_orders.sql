-- orders-service — Phase 4.2 investigation orders (22-data-dictionary §7, 23-state-machines §2). Create +
-- approval routing here; the atomic-consume fulfillment path (order_fulfillment) arrives in phase 5. Enum
-- values are CHECK-constrained to the canonical sets exactly; order_line carries the consume accumulator with
-- the 0 ≤ consumed ≤ ordered invariant so phase-5 consume can only ever move it forward.

CREATE SCHEMA IF NOT EXISTS orders;

-- Monotonic per-year order-number sequence backing ORD-YYYY-NNNNNN.
CREATE TABLE IF NOT EXISTS orders.order_seq (
    year       int  PRIMARY KEY,
    last_value int  NOT NULL
);

CREATE TABLE IF NOT EXISTS orders.investigation_order (
    order_id             uuid PRIMARY KEY,
    order_no             varchar(20) NOT NULL UNIQUE,
    beneficiary_id       uuid NOT NULL,
    encounter_id         uuid NOT NULL,
    ordering_provider_id uuid NOT NULL,
    authorization_id     uuid,
    order_type           text NOT NULL CHECK (order_type IN ('Lab','Imaging','Procedure')),
    status               text NOT NULL DEFAULT 'Requested'
        CHECK (status IN ('Requested','PendingApproval','Approved','Rejected','Active','PartiallyUsed','Completed','Expired','Cancelled')),
    requested_at         timestamptz NOT NULL DEFAULT now(),
    expires_at           timestamptz,
    idempotency_key      text,
    created_by           text,
    CHECK (expires_at IS NULL OR expires_at > requested_at)
);
CREATE INDEX IF NOT EXISTS ix_order_beneficiary_status ON orders.investigation_order (beneficiary_id, status);
CREATE INDEX IF NOT EXISTS ix_order_expiry ON orders.investigation_order (expires_at)
    WHERE status IN ('Active','PartiallyUsed');
-- Idempotent creation: at most one order per Idempotency-Key.
CREATE UNIQUE INDEX IF NOT EXISTS ux_order_idempotency ON orders.investigation_order (idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS orders.order_line (
    order_line_id     uuid PRIMARY KEY,
    order_id          uuid NOT NULL REFERENCES orders.investigation_order(order_id),
    code_system       text NOT NULL CHECK (code_system IN ('CPT','LOINC','LOCAL')),
    code              varchar(20) NOT NULL,
    description       varchar(200),
    quantity_ordered  numeric(14,3) NOT NULL CHECK (quantity_ordered > 0),
    quantity_consumed numeric(14,3) NOT NULL DEFAULT 0
        CHECK (quantity_consumed >= 0 AND quantity_consumed <= quantity_ordered),
    status            text NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','PartiallyUsed','Completed','Cancelled'))
);
CREATE INDEX IF NOT EXISTS ix_order_line_order ON orders.order_line (order_id);

-- Idempotency ledger for mutating endpoints (create / cancel).
CREATE TABLE IF NOT EXISTS orders.processed_request (
    idempotency_key text PRIMARY KEY,
    operation       text NOT NULL,
    order_id        uuid,
    status_code     int  NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
