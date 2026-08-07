-- orders-service — 0014 CONTRACT: order_line.root_line_id becomes NOT NULL. DEFERRED.
--
-- ⚠ NOT applied by tools/ci/apply-migrations.sh — it globs Migrations/*.sql at maxdepth 1 and this file is a
-- level down. Apply with: tools/ci/apply-deferred-migrations.sh
--
-- WHY THIS IS NOT IN 0013.
-- 0013 adds the column, backfills every existing row to itself, and stops. Setting NOT NULL in the same
-- migration would be an expand-phase migration doing a contract-phase job: during a rolling deploy an OLD
-- orders-service replica still inserts order_line rows without root_line_id, and the constraint would turn
-- that into a violation — surfacing as a 500 to a doctor placing an order mid-encounter. Exactly the failure
-- the radiology rename's deferred/0010 exists to avoid, on a different column.
--
-- PRECONDITION: the 30.1 deploy has fully rolled out to EVERY replica of orders-service — not merely started.
-- From that point OrdersDbContext fills root_line_id at the SaveChanges choke point, so no new row can be
-- written without it, and the backfill below is belt and braces for anything a lagging replica wrote in the
-- meantime.

BEGIN;

UPDATE orders.order_line SET root_line_id = order_line_id WHERE root_line_id IS NULL;
ALTER TABLE orders.order_line ALTER COLUMN root_line_id SET NOT NULL;  -- migrate-compat: contract-ok (post-rollout contract step; see header)

COMMIT;
