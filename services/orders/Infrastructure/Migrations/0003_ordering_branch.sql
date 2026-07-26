-- orders-service — 0003 ordering branch (phase 14.4, design 37 §3). ADDITIVE / backward-compatible.
-- Records the Mersal branch where an investigation order was raised, so the CLINICIAN-side worklist can be
-- branch-scoped. The PROVIDER fulfillment queue (phase 5.1) is provider-scoped and is deliberately NOT
-- given a branch filter. Existing rows default to NULL and behave exactly as before.

ALTER TABLE orders.investigation_order ADD COLUMN IF NOT EXISTS ordering_branch_id uuid;
CREATE INDEX IF NOT EXISTS ix_order_ordering_branch ON orders.investigation_order (ordering_branch_id);
