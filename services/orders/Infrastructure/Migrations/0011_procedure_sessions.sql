-- orders-service — 0011 OP Procedures: procedure type + session accounting on the order line.
--
-- ============================================================================================================
-- SESSIONS ARE THE LINE'S QUANTITY, NOT A PARALLEL COUNTER
-- ============================================================================================================
-- 29.2 / design 45 §2 — "Sessions are the order line's quantity. Not a parallel counter." Ten physiotherapy
-- sessions is `quantity_ordered = 10`, consumed one at a time by the SAME atomic, idempotent consume path
-- that protects every other order line, with the remainder staying active.
--
-- That is the whole reason no `sessions_delivered` column appears here. A second counter would need its own
-- concurrency proof, its own idempotency rule and its own no-reuse guard — the three properties that took
-- several phases to get right on `quantity_consumed` — and the first time the two disagreed, one of them
-- would be the one the claim was built from.
--
-- ============================================================================================================
-- WHY requested_quantity IS A SEPARATE COLUMN FROM quantity_ordered
-- ============================================================================================================
-- "Sessions authorised != sessions requested" (design 45 §2). If the doctor asks for ten and the approval team
-- partially approves six, the DELIVERABLE count is six — so `quantity_ordered`, which is what consume meters
-- against, must become 6.
--
-- But the fact that ten were ASKED FOR does not stop being true, and it is not recoverable from an audit
-- trail when the question is "how often are we approving less than we ask for?". Overwriting it would destroy
-- the only signal that partial approval is happening at all. So the request is kept beside the entitlement:
--   requested_quantity — what the doctor asked for. Never changed after creation.
--   quantity_ordered   — what may actually be delivered. Set from the APPROVED scope.
-- On an auto-activated order (no approval required) the two are equal, which is why the backfill below is a
-- straight copy and why requested_quantity is NOT NULL going forward.

ALTER TABLE orders.order_line
    ADD COLUMN IF NOT EXISTS procedure_type_code varchar(32)  NULL,
    ADD COLUMN IF NOT EXISTS requested_quantity  numeric(14,3) NULL;

-- Backfill: every existing line was delivered at what it asked for.
UPDATE orders.order_line SET requested_quantity = quantity_ordered WHERE requested_quantity IS NULL;

ALTER TABLE orders.order_line ALTER COLUMN requested_quantity SET NOT NULL;

ALTER TABLE orders.order_line DROP CONSTRAINT IF EXISTS ck_order_line_requested_positive;
ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_requested_positive CHECK (requested_quantity > 0);

-- An approval may narrow the entitlement; it may NEVER widen it beyond what was asked for. A partial approval
-- that granted MORE than the request would be a defect upstream, and the database is the last place it can be
-- stopped before it becomes a delivered service and then a claim.
ALTER TABLE orders.order_line DROP CONSTRAINT IF EXISTS ck_order_line_ordered_within_requested;
ALTER TABLE orders.order_line
    ADD CONSTRAINT ck_order_line_ordered_within_requested CHECK (quantity_ordered <= requested_quantity);

-- procedure_type_code is only meaningful on a Procedure order. Enforced in the domain rather than by a CHECK,
-- because the order type lives on the parent row and a cross-row CHECK would need a trigger — see
-- ProcedureTypeRules.Validate, which is re-run on the WRITE path and not merely in the composer.
CREATE INDEX IF NOT EXISTS ix_order_line_procedure_type
    ON orders.order_line (procedure_type_code) WHERE procedure_type_code IS NOT NULL;
