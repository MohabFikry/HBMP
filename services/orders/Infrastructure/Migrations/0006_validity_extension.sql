-- orders-service — 0006 record WHICH approval put an expired investigation order back in date. Additive.
--
-- The twin of pharmacy 0007, and here for the same reason: `validity_extended_by` is what makes the apply
-- idempotent. approvals retries the callback on a timeout, and without a record of which authorization has
-- already been actioned a retry grants a second full validity period on top of the first.

ALTER TABLE orders.investigation_order
    ADD COLUMN IF NOT EXISTS validity_extended_by uuid,
    ADD COLUMN IF NOT EXISTS validity_extended_at  timestamptz;

COMMENT ON COLUMN orders.investigation_order.validity_extended_by IS
    'The approvals authorization that revalidated this order. Also the idempotency key for the apply.';
