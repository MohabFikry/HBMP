-- orders-service — 0004 examination type + pinned sensitivity (phase 14.6, design 37 §5). ADDITIVE.
-- The classification is denormalized onto the order and its lines so read-time gating (14.7) never needs a
-- cross-service join. Sensitivity is PINNED at order creation (a later masterdata reclassification cannot
-- retroactively unlock already-restricted data). Pre-existing rows default to 'Standard' and behave as before.

ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS sensitivity_level varchar(16) NOT NULL DEFAULT 'Standard'
        CHECK (sensitivity_level IN ('Standard','Sensitive','HighlySensitive'));

ALTER TABLE orders.order_line
    ADD COLUMN IF NOT EXISTS examination_type_id uuid,
    ADD COLUMN IF NOT EXISTS sensitivity_level varchar(16) NOT NULL DEFAULT 'Standard'
        CHECK (sensitivity_level IN ('Standard','Sensitive','HighlySensitive'));

CREATE INDEX IF NOT EXISTS ix_order_line_sensitivity ON orders.order_line (sensitivity_level)
    WHERE sensitivity_level <> 'Standard';
