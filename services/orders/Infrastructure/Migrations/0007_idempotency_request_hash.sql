-- orders-service — 0007 bind the idempotency key to its request payload (phase 18.A3 / audit R2).
--
-- Two defects in the consume idempotency guard:
--   1. Keys were matched by StartsWith(key || '::'), and nothing stopped a caller putting '::' in the
--      header — so key 'A' could false-replay rows written by key 'A::L'.
--   2. The key was not bound to the request body. Replaying a key with DIFFERENT lines or quantities
--      silently returned the ORIGINAL fulfillments, so a client that changed the payload and reused the
--      key believed work had been done that never happened.
--
-- request_hash is a SHA-256 over the canonical (orderId, sorted (lineId, quantity)) tuple. A replay
-- whose hash differs from the stored one is now REJECTED rather than answered with someone else's work.
-- Nullable + backfill-free: pre-existing rows carry NULL and are treated as "unverifiable, allow replay"
-- exactly as before, so this is additive (expand/contract).

ALTER TABLE orders.order_fulfillment ADD COLUMN IF NOT EXISTS request_hash text;

CREATE INDEX IF NOT EXISTS ix_order_fulfillment_idempotency_key
    ON orders.order_fulfillment (idempotency_key);
