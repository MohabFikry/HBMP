-- orders-service — 0018 CONTRACT: order_line.requested_quantity becomes NOT NULL. DEFERRED.
--
-- ⚠ NOT applied by tools/ci/apply-migrations.sh — it globs Migrations/*.sql at maxdepth 1 and this file is a
-- level down. Apply with: tools/ci/apply-deferred-migrations.sh
--
-- WHY THIS IS NOT IN 0011.
-- 0011 adds requested_quantity as NULL and backfills every existing row from quantity_ordered. It originally
-- set NOT NULL in the same breath, which is an expand-phase migration doing a contract-phase job: during a
-- rolling deploy an OLD orders-service replica still inserts order_line rows without requested_quantity, and
-- the constraint turns that into a violation — surfacing as a 500 to a doctor placing an order mid-encounter.
-- This is the identical defect deferred/0014 was written for, on a different column of the same table, and
-- 0011 predates that lesson. The column has no sensible DEFAULT to paper over the gap: requested_quantity is
-- what the doctor ASKED FOR, and inventing a value for it would fabricate the one number the column exists to
-- preserve (see the header of 0011 on why it is kept separate from quantity_ordered).
--
-- PRECONDITION: the 29.x procedure-sessions deploy has fully rolled out to EVERY replica of orders-service —
-- not merely started. From that point the API fills requested_quantity on every line it creates
-- (Api/Orders.cs, ProcedureCourse.MeteredTotal), so no new row can be written without it, and the backfill
-- below is belt and braces for anything a lagging replica wrote in the meantime.
--
-- SAFE TO RUN LATE: the backfill is idempotent and the constraint is a no-op once satisfied.

BEGIN;

UPDATE orders.order_line SET requested_quantity = quantity_ordered WHERE requested_quantity IS NULL;
ALTER TABLE orders.order_line ALTER COLUMN requested_quantity SET NOT NULL;  -- migrate-compat: contract-ok (post-rollout contract step; see header)

COMMIT;
